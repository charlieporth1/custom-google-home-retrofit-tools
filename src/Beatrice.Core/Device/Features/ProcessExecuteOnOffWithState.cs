// ============================================================
// ProcessExecuteOnOffWithState.cs
// Add to: src/Beatrice.Core/Device/Features/
//
// Drop-in replacement for ProcessExecuteOnOff that also
// implements IDeviceSupportWithState.  A shell command of
// your choice is used to report the current on/off state.
//
// Config example (appsettings.Beatrice.json):
// {
//   "Feature": "Beatrice.Device.Features.ProcessExecuteOnOffWithState",
//   "Options": {
//     "On":  { "Executable": "/usr/bin/my-outlet", "Arguments": "on" },
//     "Off": { "Executable": "/usr/bin/my-outlet", "Arguments": "off" },
//     "State": {
//       "Executable": "/usr/bin/my-outlet",
//       "Arguments":  "status",
//       "OnExitCode": 0          // exit-code that means "currently ON"
//     }
//   }
// }
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Beatrice.Configuration;
using Beatrice.Request;

namespace Beatrice.Device.Features
{
    public class ProcessExecuteOnOffWithStateOptions
    {
        public ProcessOptions On    { get; set; }
        public ProcessOptions Off   { get; set; }
        /// <summary>Optional — omit if the feature should NOT report state.</summary>
        public StateProcessOptions State { get; set; }
    }

    public class ProcessOptions
    {
        public string Executable   { get; set; }
        public string Arguments    { get; set; }
        public bool   WaitForExit  { get; set; } = true;
    }

    /// <summary>
    /// Options for the shell command that checks current state.
    /// The process exit code is used to determine on/off:
    ///   exit == OnExitCode  →  on:true
    ///   anything else       →  on:false
    /// </summary>
    public class StateProcessOptions
    {
        public string Executable { get; set; }
        public string Arguments  { get; set; }
        /// <summary>Exit code that means the device is currently ON. Default = 0.</summary>
        public int    OnExitCode { get; set; } = 0;
    }

    public class ProcessExecuteOnOffWithState : IDeviceFeature, IDeviceSupportWithState
    {
        private readonly ProcessExecuteOnOffWithStateOptions _options;

        public ProcessExecuteOnOffWithState(ProcessExecuteOnOffWithStateOptions options)
        {
            _options = options;
        }

        // ── IDeviceFeature ───────────────────────────────────────────
        public IEnumerable<string> Traits => new[] { "action.devices.traits.OnOff" };

        public async Task InvokeAsync(DeviceDefinition definition, ExecutionCommand command)
        {
            var processOptions = command.Command == "action.devices.commands.OnOff"
                && command.Params.TryGetValue("on", out var onValue)
                && (bool)onValue
                    ? _options.On
                    : _options.Off;

            var psi = new ProcessStartInfo
            {
                FileName        = processOptions.Executable,
                Arguments       = processOptions.Arguments,
                UseShellExecute = false,
            };

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start process.");

            if (processOptions.WaitForExit)
                await Task.Run(() => process.WaitForExit());
        }

        // ── IDeviceSupportWithState ──────────────────────────────────
        /// <summary>
        /// Returns {"on": bool} by running the State shell command.
        /// If no State options are configured, returns null (no state reported).
        /// </summary>
        public async Task<Dictionary<string, object>> GetStateAsync(DeviceDefinition definition)
        {
            if (_options.State == null)
                return null; // This feature opts out of state reporting.

            var psi = new ProcessStartInfo
            {
                FileName        = _options.State.Executable,
                Arguments       = _options.State.Arguments,
                UseShellExecute = false,
            };

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start state-check process.");

            await Task.Run(() => process.WaitForExit());

            bool isOn = process.ExitCode == _options.State.OnExitCode;

            return new Dictionary<string, object>
            {
                ["on"] = isOn
            };
        }
    }
}
