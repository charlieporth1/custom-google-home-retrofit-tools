// ============================================================
// IDeviceSupportWithState.cs
// Add to: src/Beatrice.Core/Device/
//
// Implement this interface on any Feature class to opt-in to
// willReportState / QUERY state reporting.
// ============================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using Beatrice.Configuration;

namespace Beatrice.Device
{
    /// <summary>
    /// A Feature that implements this interface can report its current
    /// state back to Google Home via the QUERY intent and Report State API.
    /// </summary>
    public interface IDeviceSupportWithState
    {
        /// <summary>
        /// Return the current state key/value pairs for this feature.
        /// Keys must match Actions-on-Google trait attribute names,
        /// e.g. "on", "brightness", "color", "currentSensorStateData".
        /// </summary>
        Task<Dictionary<string, object>> GetStateAsync(DeviceDefinition definition);
    }
}
