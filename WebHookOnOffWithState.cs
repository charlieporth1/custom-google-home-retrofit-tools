// ============================================================
// WebHookOnOffWithState.cs
// Add to: src/Beatrice.Core/Device/Features/
//
// Extension of WebHookOnOff that also implements
// IDeviceSupportWithState. A GET request is made to a
// status URL; the response JSON is expected to contain
// { "on": true/false }.
//
// Config example:
// {
//   "Feature": "Beatrice.Device.Features.WebHookOnOffWithState",
//   "Options": {
//     "On":  { "Url": "http://mydevice/on",     "Body": "{\"on\":true}" },
//     "Off": { "Url": "http://mydevice/off",    "Body": "{\"on\":false}" },
//     "State": {
//       "Url":     "http://mydevice/status",
//       "OnKey":   "on"          // JSON key to read (default "on")
//     }
//   }
// }
// ============================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Beatrice.Configuration;
using Beatrice.Request;

namespace Beatrice.Device.Features
{
    public class WebHookOnOffWithStateOptions
    {
        public WebHookOptions On    { get; set; }
        public WebHookOptions Off   { get; set; }
        /// <summary>Optional — omit to skip state reporting for this device.</summary>
        public WebHookStateOptions State { get; set; }
    }

    public class WebHookOptions
    {
        public string Url         { get; set; }
        public string Body        { get; set; }
        public string ContentType { get; set; } = "application/json";
    }

    public class WebHookStateOptions
    {
        public string Url   { get; set; }
        /// <summary>The JSON property name in the response body that holds the boolean on/off value.</summary>
        public string OnKey { get; set; } = "on";
    }

    public class WebHookOnOffWithState : IDeviceFeature, IDeviceSupportWithState
    {
        // Shared HttpClient — never create per-request in production.
        private static readonly HttpClient Http = new HttpClient();

        private readonly WebHookOnOffWithStateOptions _options;

        public WebHookOnOffWithState(WebHookOnOffWithStateOptions options)
        {
            _options = options;
        }

        // ── IDeviceFeature ───────────────────────────────────────────
        public IEnumerable<string> Traits => new[] { "action.devices.traits.OnOff" };

        public async Task InvokeAsync(DeviceDefinition definition, ExecutionCommand command)
        {
            bool turnOn = command.Command == "action.devices.commands.OnOff"
                && command.Params.TryGetValue("on", out var v)
                && (bool)v;

            var opts = turnOn ? _options.On : _options.Off;

            var content = new StringContent(
                opts.Body ?? string.Empty,
                System.Text.Encoding.UTF8,
                opts.ContentType);

            var response = await Http.PostAsync(opts.Url, content);
            response.EnsureSuccessStatusCode();
        }

        // ── IDeviceSupportWithState ──────────────────────────────────
        /// <summary>
        /// GETs the status URL and reads the on/off boolean from the JSON body.
        /// Returns null if no State options are configured (opting out of state reporting).
        /// </summary>
        public async Task<Dictionary<string, object>> GetStateAsync(DeviceDefinition definition)
        {
            if (_options.State == null)
                return null;

            var response = await Http.GetAsync(_options.State.Url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            bool isOn = false;
            if (doc.RootElement.TryGetProperty(_options.State.OnKey, out var prop))
                isOn = prop.GetBoolean();

            return new Dictionary<string, object>
            {
                ["on"] = isOn
            };
        }
    }
}
