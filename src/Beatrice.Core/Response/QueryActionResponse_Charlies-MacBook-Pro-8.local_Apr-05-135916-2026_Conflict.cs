using System.Collections.Generic;
using Newtonsoft.Json;

namespace Beatrice.Response
{
    public class QueryActionResponse
    {
        [JsonProperty("requestId")]
        public string RequestId { get; set; }

        [JsonProperty("payload")]
        public QueryActionPayload Payload { get; set; }

        public class QueryActionPayload
        {
            // BUG FIX: Was typed as Dictionary<string,object>[] (an array),
            // which serialises to a JSON array. Google's QUERY intent requires
            // a map (object) keyed by device ID:
            //
            //   "devices": {
            //     "device-id-1": { "online": true, "on": false },
            //     "device-id-2": { "online": true, "on": true }
            //   }
            //
            // Changing the type to Dictionary<string, Dictionary<string,object>>
            // produces the correct JSON shape without any other code changes.
            [JsonProperty("devices")]
            public Dictionary<string, Dictionary<string, object>> Devices { get; set; }
        }
    }
}
