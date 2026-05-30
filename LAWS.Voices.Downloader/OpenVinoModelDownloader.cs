using LAWS.Voices.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LAWS.Voices.Downloader
{
    public static class OpenVinoModelDownloader
    {
        private static readonly HttpClient _httpClient;

        public static string ModelZooBaseUrl { get; set; } = "https://github.com/openvinotoolkit/open_model_zoo/tree/master/models";
        public static string ModelsDirectory { get; set; } = @"D:\Models\OpenVino";
        public static int TimeoutDownloadSeconds { get; set; } = 300;

        static OpenVinoModelDownloader()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutDownloadSeconds) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OpenVinoDownloader/1.0)");
        }

        public static async Task<List<OpenVinoModelInfo>> ListAvailableModelsAsync(string subRepo = "intel")
        {
            string apiDirUrl = $"https://api.github.com/repos/openvinotoolkit/open_model_zoo/contents/models/{subRepo}";

            using var response = await _httpClient.GetAsync(apiDirUrl);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var directoryNames = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.GetProperty("type").GetString() == "dir")
                {
                    directoryNames.Add(element.GetProperty("name").GetString() ?? string.Empty);
                }
            }

            var tasks = directoryNames.Select(async modelId =>
            {
                string modelRepoUrl = $"{ModelZooBaseUrl.TrimEnd('/')}/{subRepo}/{modelId}";
                try
                {
                    return await OpenVinoModelXmlParser.ParseModelXmlAsync(modelRepoUrl);
                }
                catch
                {
                    return new OpenVinoModelInfo { Id = modelId, ModelRepoUrl = modelRepoUrl };
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        public static async Task DownloadModelAsync(
            string modelId,
            string subRepo = "intel",
            List<OpenVinoModelQuantization>? targetQuants = null,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string modelRepoUrl = $"{ModelZooBaseUrl.TrimEnd('/')}/{subRepo}/{modelId}";
            string rawBaseUrl = modelRepoUrl.Replace("github.com", "raw.githubusercontent.com").Replace("/tree/", "/");

            string modelRootDir = Path.Combine(ModelsDirectory, subRepo, modelId);
            Directory.CreateDirectory(modelRootDir);

            bool isComposite = false;
            string ymlContent;

            try
            {
                ymlContent = await _httpClient.GetStringAsync(rawBaseUrl + "/model.yml", cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(modelRootDir, "model.yml"), ymlContent, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                ymlContent = await _httpClient.GetStringAsync(rawBaseUrl + "/composite-model.yml", cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(modelRootDir, "composite-model.yml"), ymlContent, cancellationToken);
                isComposite = true;
            }

            List<string> subModels = isComposite ? OpenVinoModelXmlParser.ExtractSubModelsFromCompositeContent(ymlContent, modelId) : [];
            OpenVinoModelInfo combinedModelInfo;

            if (!isComposite)
            {
                combinedModelInfo = OpenVinoModelXmlParser.ParseModelXmlContent(ymlContent, modelId, modelRepoUrl);
            }
            else
            {
                combinedModelInfo = new OpenVinoModelInfo { Id = modelId, ModelRepoUrl = modelRepoUrl };
                foreach (var subModel in subModels)
                {
                    try
                    {
                        string subModelDir = Path.Combine(modelRootDir, subModel);
                        Directory.CreateDirectory(subModelDir);

                        string subRawYmlUrl = $"{rawBaseUrl}/{subModel}/model.yml";
                        string subYmlContent = await _httpClient.GetStringAsync(subRawYmlUrl, cancellationToken);
                        await File.WriteAllTextAsync(Path.Combine(subModelDir, "model.yml"), subYmlContent, cancellationToken);

                        var subInfo = OpenVinoModelXmlParser.ParseModelXmlContent(subYmlContent, subModel, $"{modelRepoUrl}/{subModel}");
                        foreach (var kvp in subInfo.QuantizationUrls)
                        {
                            combinedModelInfo.QuantizationUrls.TryAdd(kvp.Key, kvp.Value);
                        }

                        foreach (var kvp in subInfo.UrlsOrPathSizes)
                        {
                            combinedModelInfo.UrlsOrPathSizes.TryAdd(kvp.Key, kvp.Value);
                        }
                    }
                    catch { }
                }
            }

            var quantsToDownload = targetQuants ?? combinedModelInfo.QuantizationUrls.Keys.ToList();
            var downloadQueue = new List<(string Url, long Size, OpenVinoModelQuantization Quant, string TargetDir)>();

            foreach (var (url, size) in combinedModelInfo.UrlsOrPathSizes)
            {
                // Fix: Default to FP32 if public URLs lack explicit precision strings (e.g. raw PyTorch/HuggingFace assets)
                var fileQuant = DetermineQuantizationFromUrl(url) ?? OpenVinoModelQuantization.FP32;

                if (quantsToDownload.Contains(fileQuant))
                {
                    string targetDir = Path.Combine(modelRootDir, fileQuant.ToString());
                    foreach (var subModel in subModels)
                    {
                        if (url.Contains($"/{subModel}/", StringComparison.OrdinalIgnoreCase) || url.Contains(subModel + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            targetDir = Path.Combine(modelRootDir, subModel, fileQuant.ToString());
                            break;
                        }
                    }
                    downloadQueue.Add((url, size, fileQuant, targetDir));
                }
            }

            if (downloadQueue.Count == 0)
            {
                progress?.Report(1.0);
                return;
            }

            long totalBytes = downloadQueue.Sum(q => q.Size);
            long totalDownloadedBytes = 0;

            foreach (var (url, expectedSize, quant, targetDir) in downloadQueue)
            {
                Uri uri = new(url);
                string fileName = Path.GetFileName(uri.LocalPath);

                Directory.CreateDirectory(targetDir);
                string localFilePath = Path.Combine(targetDir, fileName);

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

                byte[] buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await downloadStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalDownloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        progress?.Report(Math.Min((double) totalDownloadedBytes / totalBytes, 1.0));
                    }
                }

                // Fix: Assign tracking pointer path if it matches standard model formats or acts as primary fallback
                if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(combinedModelInfo.ModelXmlPath))
                {
                    combinedModelInfo.ModelXmlPath = localFilePath;
                }
            }

            progress?.Report(1.0);
        }

        private static OpenVinoModelQuantization? DetermineQuantizationFromUrl(string url)
        {
            if (url.Contains("FP32", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.FP32;
            }

            if (url.Contains("INT8", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.INT8;
            }

            if (url.Contains("FP16", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.FP16;
            }

            if (url.Contains("INT16", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.INT16;
            }

            return null;
        }
    }
}