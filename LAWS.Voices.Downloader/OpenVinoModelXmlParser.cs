using LAWS.Voices.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace LAWS.Voices.Downloader
{
    public static class OpenVinoModelXmlParser
    {
        private static readonly HttpClient _httpClient = new();

        static OpenVinoModelXmlParser()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OpenVinoDownloader/1.0)");
        }

        public static async Task<OpenVinoModelInfo> ParseModelXmlAsync(string modelRepoUrl)
        {
            string modelId = modelRepoUrl.Substring(modelRepoUrl.LastIndexOf('/') + 1);
            string rawBaseUrl = modelRepoUrl.Replace("github.com", "raw.githubusercontent.com").Replace("/tree/", "/");

            string content;
            bool isComposite = false;

            try
            {
                content = await _httpClient.GetStringAsync(rawBaseUrl + "/model.yml");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                content = await _httpClient.GetStringAsync(rawBaseUrl + "/composite-model.yml");
                isComposite = true;
            }

            if (!isComposite)
            {
                return ParseModelXmlContent(content, modelId, modelRepoUrl);
            }
            else
            {
                var parentInfo = new OpenVinoModelInfo
                {
                    Id = modelId,
                    ModelRepoUrl = modelRepoUrl
                };

                var subModels = ExtractSubModelsFromCompositeContent(content, modelId);

                foreach (var subModel in subModels)
                {
                    try
                    {
                        string subRawYmlUrl = $"{rawBaseUrl}/{subModel}/model.yml";
                        string subContent = await _httpClient.GetStringAsync(subRawYmlUrl);
                        var subInfo = ParseModelXmlContent(subContent, subModel, $"{modelRepoUrl}/{subModel}");

                        foreach (var kvp in subInfo.QuantizationUrls)
                        {
                            parentInfo.QuantizationUrls.TryAdd(kvp.Key, kvp.Value);
                        }
                        foreach (var kvp in subInfo.UrlsOrPathSizes)
                        {
                            parentInfo.UrlsOrPathSizes.TryAdd(kvp.Key, kvp.Value);
                        }
                    }
                    catch
                    {
                        // Safely skip missing or broken sub-model tracks
                    }
                }

                return parentInfo;
            }
        }

        public static OpenVinoModelInfo ParseModelXmlContent(string content, string modelId, string modelRepoUrl)
        {
            var modelInfo = new OpenVinoModelInfo
            {
                Id = modelId,
                ModelRepoUrl = modelRepoUrl
            };

            modelInfo.QuantizationUrls = ExtractQuantizationUrls(content);
            modelInfo.UrlsOrPathSizes = ExtractUrlsOrPathSizes(content);

            return modelInfo;
        }

        public static List<string> ExtractSubModelsFromCompositeContent(string content, string modelId)
        {
            var subModels = new List<string>();
            var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("- name:") || trimmed.StartsWith("name:"))
                {
                    string name = trimmed.Replace("- name:", "").Replace("name:", "").Trim(' ', '"', '\'', ':');
                    if (!string.IsNullOrEmpty(name) && name.Contains(modelId) && !subModels.Contains(name))
                    {
                        subModels.Add(name);
                    }
                }
            }

            if (subModels.Count == 0)
            {
                var words = content.Split([' ', '\n', '\r', '"', '\'', ':', ',', '[', ']', '{', '}'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                {
                    string cleaned = word.Trim('-', ' ');
                    if (cleaned.StartsWith(modelId) && cleaned.Length > modelId.Length && !subModels.Contains(cleaned))
                    {
                        subModels.Add(cleaned);
                    }
                }
            }
            return subModels;
        }

        public static Dictionary<OpenVinoModelQuantization, string> ExtractQuantizationUrls(string modelXmlContent)
        {
            var quantizationUrls = new Dictionary<OpenVinoModelQuantization, string>();
            var lines = modelXmlContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string currentName = string.Empty;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("- name:"))
                {
                    currentName = trimmed.Replace("- name:", "").Trim(' ', '"', '\'');
                }
                else if (trimmed.StartsWith("source:"))
                {
                    string sourceUrl = trimmed.Replace("source:", "").Trim(' ', '"', '\'');

                    if (!string.IsNullOrEmpty(currentName))
                    {
                        // Fix: Support all model file extensions (.bin, .pt, .json, .py) for public frameworks
                        var quant = DetermineQuantization(currentName) ?? DetermineQuantization(sourceUrl);

                        // Default fallback: Public framework weights are natively parsed as unquantized base specs (FP32)
                        quant ??= OpenVinoModelQuantization.FP32;

                        quantizationUrls.TryAdd(quant.Value, sourceUrl);
                    }
                }
            }
            return quantizationUrls;
        }

        public static Dictionary<string, long> ExtractUrlsOrPathSizes(string modelXmlContent)
        {
            var urlsOrPathSizes = new Dictionary<string, long>();
            var lines = modelXmlContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string currentSource = string.Empty;
            long currentSize = 0;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("- name:"))
                {
                    if (!string.IsNullOrEmpty(currentSource))
                    {
                        urlsOrPathSizes.TryAdd(currentSource, currentSize);
                        currentSource = string.Empty;
                        currentSize = 0;
                    }
                }
                else if (trimmed.StartsWith("size:"))
                {
                    long.TryParse(trimmed.Replace("size:", "").Trim(), out currentSize);
                }
                else if (trimmed.StartsWith("source:"))
                {
                    currentSource = trimmed.Replace("source:", "").Trim(' ', '"', '\'');
                }
            }

            if (!string.IsNullOrEmpty(currentSource))
            {
                urlsOrPathSizes.TryAdd(currentSource, currentSize);
            }
            return urlsOrPathSizes;
        }

        public static OpenVinoModelQuantization? DetermineQuantization(string pathOrUrl)
        {
            if (pathOrUrl.Contains("FP32", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.FP32;
            }

            if (pathOrUrl.Contains("INT8", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.INT8;
            }

            if (pathOrUrl.Contains("FP16", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.FP16;
            }

            if (pathOrUrl.Contains("INT16", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVinoModelQuantization.INT16;
            }

            return null;
        }
    }
}