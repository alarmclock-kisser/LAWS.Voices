using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace LAWS.Voices.Shared
{
    public enum OpenVinoModelQuantization
    {
        FP32,
        FP16,
        INT8,
        INT16
    }

    public class OpenVinoModelInfo
    {
        public string Id { get; set; } = string.Empty;
        public string ModelRepoUrl { get; set; } = string.Empty;

        public string? ModelXmlPath { get; set; } = null;

        public Dictionary<OpenVinoModelQuantization, string> QuantizationUrls { get; set; } = [];
        public Dictionary<string, long> UrlsOrPathSizes { get; set; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        [JsonConstructor]
        public OpenVinoModelInfo()
        {

        }
    }
}
