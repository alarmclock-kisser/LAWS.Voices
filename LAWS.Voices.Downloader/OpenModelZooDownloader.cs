using System;
using System.Collections.Generic;
using System.Text;

namespace LAWS.Voices.Downloader
{
    public static class OpenModelZooDownloader
    {
        public const string ModelsBaseUrl = "https://github.com/openvinotoolkit/open_model_zoo/tree/master/models";

        public static Task<List<string>> GetAvailableModelsAsync(string subRepo = "intel")
        {
            HttpClient client = new()
            {
                BaseAddress = new Uri(ModelsBaseUrl),
                Timeout = TimeSpan.FromMinutes(5)
            };

            // Get & verify subRepos from ModelsBaseUrl
            string[] subRepoNames = client.GetAsync(ModelsBaseUrl).Result.Content.ReadAsStringAsync().Result
                .Split(new[] { "href=\"/openvinotoolkit/open_model_zoo/tree/master/models/" }, StringSplitOptions.None)
                .Skip(1)
                .Select(s => s.Split('"')[0])
                .Where(s => s.StartsWith(subRepo + "/"))
                .Select(s => s.Substring(subRepo.Length + 1))
                .ToArray();
            if (subRepoNames.Length > 0 && !subRepoNames.Contains(subRepo))
            {
                throw new InvalidOperationException($"Sub-repo '{subRepo}' not found.");
            }


        }

    }
}
