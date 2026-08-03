using System.Collections.Generic;
using Newtonsoft.Json;

namespace ArkFramework
{
    public sealed class JsonConfigManifest
    {
        [JsonProperty("entries")]
        public List<JsonConfigManifestEntry> Entries { get; set; }
    }

    public sealed class JsonConfigManifestEntry
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }
    }
}
