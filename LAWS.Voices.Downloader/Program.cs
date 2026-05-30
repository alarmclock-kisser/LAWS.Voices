using LAWS.Voices.Shared;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LAWS.Voices.Downloader
{
    internal class Program
    {
        // Eine synchrone IProgress-Implementierung, um Thread-Überlagerungen im CLI zu verhindern
        private class SyncProgress : IProgress<double>
        {
            public void Report(double value)
            {
                Console.Write($"\rProgress: {value:P1}   ");
            }
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine("LAWS.Voices.Downloader (CLI)\n\n");

            // Read appsettings.json
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            int timeoutSeconds = config.GetValue<int>("TimeoutDownloadSeconds");
            OpenVinoModelDownloader.TimeoutDownloadSeconds = Math.Clamp(timeoutSeconds > 0 ? timeoutSeconds : 300, 1, int.MaxValue);

            string defaultDocsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Models", "OpenVino");
            string modelsDirectory = config["ModelsDirectory"] ?? defaultDocsPath;

            if (!Directory.Exists(modelsDirectory))
            {
                Console.WriteLine($"Models directory '{modelsDirectory}' does not exist. Creating it...");
                Directory.CreateDirectory(modelsDirectory);
            }
            else
            {
                Console.WriteLine($"Models will be downloaded to: {modelsDirectory}");
            }
            OpenVinoModelDownloader.ModelsDirectory = modelsDirectory;

            // Ask user for sub-repo (default to "intel")
            Console.Write("\nEnter sub-repo to list models from ('intel' or 'public') >>> : ");
            string? subRepoInput = Console.ReadLine();
            string subRepo = string.IsNullOrWhiteSpace(subRepoInput) ? "intel" : subRepoInput.Trim();

            Console.WriteLine("\nFetching available models from GitHub API...");
            var availableModels = await OpenVinoModelDownloader.ListAvailableModelsAsync(subRepo);
            var modelList = availableModels.ToList();

            Console.WriteLine("\nAvailable models:");
            for (int i = 0; i < modelList.Count; i++)
            {
                string id = modelList[i].Id;
                int spaces = Math.Max(0, 4 - i.ToString().Length);

                string modelFolder = Path.Combine(modelsDirectory, subRepo, id);
                string localYmlFile = Path.Combine(modelFolder, "model.yml");
                string localCompositeFile = Path.Combine(modelFolder, "composite-model.yml");
                var localQuants = new List<string>();

                if (File.Exists(localYmlFile) || File.Exists(localCompositeFile))
                {
                    foreach (OpenVinoModelQuantization quant in Enum.GetValues<OpenVinoModelQuantization>())
                    {
                        bool hasQuantFiles = Directory.Exists(Path.Combine(modelFolder, quant.ToString())) && Directory.EnumerateFiles(Path.Combine(modelFolder, quant.ToString())).Any();

                        if (!hasQuantFiles && Directory.Exists(modelFolder))
                        {
                            foreach (var subDir in Directory.GetDirectories(modelFolder))
                            {
                                if (Directory.Exists(Path.Combine(subDir, quant.ToString())) && Directory.EnumerateFiles(Path.Combine(subDir, quant.ToString())).Any())
                                {
                                    hasQuantFiles = true;
                                    break;
                                }
                            }
                        }

                        if (hasQuantFiles)
                        {
                            localQuants.Add(quant.ToString());
                        }
                    }
                }

                bool isDownloaded = localQuants.Count > 0;
                string mark = isDownloaded ? "✓" : "-";
                string quantsSuffix = isDownloaded ? $" <{string.Join(", ", localQuants)}>" : string.Empty;

                long sizeMb = modelList[i].UrlsOrPathSizes.Sum(u => u.Value) / 1024 / 1024;

                Console.WriteLine($"    [{i}]" + new string(' ', spaces) + $"{mark} '{id} [{sizeMb} MB]'{quantsSuffix}");
            }

            // Loop til empty input to download models
            while (true)
            {
                Console.Write("\nEnter model id or index to download (or press Enter to finish) >>> : ");
                string input = Console.ReadLine()?.Trim().Trim(['\'', '"']) ?? "";
                if (string.IsNullOrWhiteSpace(input))
                {
                    break;
                }

                string modelId = input;
                if (int.TryParse(input, out int index))
                {
                    if (index < 0 || index >= modelList.Count)
                    {
                        Console.WriteLine("Invalid index. Please try again.");
                        continue;
                    }
                    modelId = modelList[index].Id;
                }

                var selectedModel = modelList.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
                if (selectedModel == null)
                {
                    Console.WriteLine($"Model '{modelId}' not found in the online repository list.");
                    continue;
                }

                string targetModelDir = Path.Combine(modelsDirectory, subRepo, modelId);
                if (Directory.Exists(targetModelDir))
                {
                    Console.Write($"Model '{modelId}' is already downloaded. Do you want to delete and redownload it? (y/n) >>> : ");
                    string confirm = Console.ReadLine() ?? "n";
                    if (!confirm.Equals("y", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Skipping download.");
                        continue;
                    }
                    Console.WriteLine("Deleting existing model...");
                    Directory.Delete(targetModelDir, true);
                }

                List<OpenVinoModelQuantization>? targetQuants = null;
                if (selectedModel.QuantizationUrls.Count > 0)
                {
                    Console.WriteLine($"Available precisions: {string.Join(", ", selectedModel.QuantizationUrls.Keys)}");
                    Console.Write("Enter desired precision (e.g. FP32, FP16, INT8) or press Enter to download all: ");
                    string? pinput = Console.ReadLine()?.Trim();

                    if (!string.IsNullOrWhiteSpace(pinput))
                    {
                        string normalizedInput = pinput.Contains("INT8", StringComparison.OrdinalIgnoreCase) ? "INT8" : pinput;

                        if (Enum.TryParse<OpenVinoModelQuantization>(normalizedInput, true, out var parsedQuant))
                        {
                            targetQuants = [parsedQuant];
                        }
                        else
                        {
                            Console.WriteLine($"Unknown precision '{pinput}'. Defaulting to ALL available precisions.");
                        }
                    }
                }

                Console.WriteLine($"Downloading model '{modelId}'...");

                try
                {
                    // Nutzt hier die neue synchrone Klasse SyncProgress
                    await OpenVinoModelDownloader.DownloadModelAsync(
                        modelId,
                        subRepo,
                        targetQuants,
                        new SyncProgress()
                    );

                    // Fix: Zeilenumbruch direkt nach Beendigung erzwingen, um den Carriage Return (\r) zu brechen
                    Console.WriteLine();

                    string selectedQuantsStr = targetQuants != null
                        ? string.Join(", ", targetQuants)
                        : (selectedModel.QuantizationUrls.Count > 0 ? string.Join(", ", selectedModel.QuantizationUrls.Keys) : "All");

                    Console.WriteLine($"Model '{modelId}' downloaded successfully. <{selectedQuantsStr}>");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\nFailed to download model '{modelId}'. Error: {ex.Message}");
                }
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}