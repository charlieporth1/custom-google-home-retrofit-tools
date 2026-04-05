using System.Collections.Generic;
using Newtonsoft.Json;

namespace Beatrice.Response
{
    public class CommandResult
    {
        [JsonProperty("ids")]
        public string[] Ids { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        // BUG FIX: States was commented out in ExecuteAsync. The test suite
        // checks that a SUCCESS response contains the new device states
        // (at minimum "online": true). NullValueHandling.Ignore means this
        // property is omitted from ERROR responses where it is not set.
        [JsonProperty("states", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> States { get; set; }

        [JsonProperty("errorCode", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorCode { get; set; }
    }
}
