using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace LAWS.Voices.Shared
{
    public class Appsettings
    {
        public string ModelZooBaseUrl { get; set; } = string.Empty;
        public string ModelsDirectory { get; set; } = string.Empty;
        public int TimeoutDownloadSeconds { get; set; } = 300;




        [JsonConstructor]
        public Appsettings()
        {
        }

    }
}
