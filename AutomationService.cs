// ============================================================
// AutomationService.cs  (UPDATED)
// Replaces: src/Beatrice.Core/Service/AutomationService.cs
//
// Changes from original:
//   1. GetStateFromDeviceIdAsync — uncommented & implemented:
//      iterates features that implement IDeviceSupportWithState
//      and merges their state dictionaries.
//   2. WillReportState is now driven by whether ANY feature on
//      the device implements IDeviceSupportWithState, OR by the
//      optional config flag DeviceDefinition.WillReportState.
//   3. ExecuteAsync now optionally pushes state after a command
//      succeeds (see ReportStateAfterExecute region).
// ============================================================
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Beatrice.Configuration;
using Beatrice.Device;
using Beatrice.Request;
using Beatrice.Response;
using Microsoft.Extensions.Logging;

namespace Beatrice.Service
{
    public class AutomationService
    {
        private readonly Dictionary<string, DeviceInstance> _deviceById;
        private readonly ILogger _logger;

        public string AgentUserId { get; set; } = "agentUserId.0";
        public IReadOnlyDictionary<string, DeviceInstance> DeviceById => _deviceById;

        public AutomationService(
            IOptions<DeviceConfiguration> deviceConfig,
            DeviceInstanceProvider deviceInstanceProvider,
            ILogger<AutomationService> logger)
        {
            _deviceById = deviceConfig.Value.Devices
                .ToDictionary(k => k.Id, v => deviceInstanceProvider.Create(v));
            _logger = logger;

            foreach (var device in _deviceById)
            {
                _logger.LogInformation(
                    "Device: Id={0}; Features={1}; WillReportState={2}",
                    device.Key,
                    string.Join(",", device.Value.Features.Select(x => x.Instance.GetType().Name)),
                    device.Value.WillReportState);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Dispatch
        // ─────────────────────────────────────────────────────────────
        public async Task<object> DispatchAsync(ActionRequest request)
        {
            foreach (var intent in request.Inputs)
            {
                switch (intent.Intent)
                {
                    case "action.devices.SYNC":
                        return await SyncAsync(request.RequestId);
                    case "action.devices.QUERY":
                        return await GetStatesAsync(request.RequestId, (StatesPayload)intent.Payload);
                    case "action.devices.EXECUTE":
                        return await ExecuteAsync(request.RequestId, (ExecutePayload)intent.Payload);
                }
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // SYNC — advertise devices to Google
        // ─────────────────────────────────────────────────────────────
        public Task<SyncActionResponse> SyncAsync(string requestId)
        {
            _logger.LogInformation("Sync: RequestId={0}", requestId);

            return Task.FromResult(new SyncActionResponse
            {
                RequestId = requestId,
                Payload = new SyncActionResponse.SyncActionPayload
                {
                    AgentUserId = AgentUserId,
                    Devices = _deviceById.Select(x =>
                    {
                        // WillReportState is true if:
                        //   (a) the device definition explicitly sets it, OR
                        //   (b) at least one feature implements IDeviceSupportWithState.
                        bool willReport = x.Value.WillReportState
                            || x.Value.Features.Any(f => f.Instance is IDeviceSupportWithState);

                        return new SyncActionResponse.DeviceResponse
                        {
                            Id = x.Key,
                            Name = new SyncActionResponse.NameResponse
                            {
                                Name = x.Value.Definition.Name,
                                Nicknames = (x.Value.Definition.Nicknames == null || !x.Value.Definition.Nicknames.Any())
                                    ? new[] { x.Value.Definition.Name }
                                    : x.Value.Definition.Nicknames,
                                DefaultNames = new[] { x.Value.Definition.Name },
                            },
                            Type = x.Value.Definition.Type,
                            Traits = x.Value.Traits,
                            WillReportState = willReport,   // ← now auto-detected
                            RoomHint = x.Value.Definition.RoomHint,
                        };
                    }).ToArray()
                }
            });
        }

        // ─────────────────────────────────────────────────────────────
        // QUERY — return current device states
        // ─────────────────────────────────────────────────────────────
        public async Task<QueryActionResponse> GetStatesAsync(string requestId, StatesPayload statesPayload)
        {
            var deviceStates = await Task.WhenAll(
                statesPayload.Devices.Select(d => GetStateFromDeviceIdAsync(d.Id)));

            return new QueryActionResponse
            {
                RequestId = requestId,
                Payload = new QueryActionResponse.QueryActionPayload
                {
                    Devices = deviceStates
                }
            };
        }

        /// <summary>
        /// Collect state from all IDeviceSupportWithState features on a device.
        /// If no features support state, returns { "online": true } as a safe default.
        /// </summary>
        private async Task<Dictionary<string, object>> GetStateFromDeviceIdAsync(string id)
        {
            var state = new Dictionary<string, object>
            {
                // Google requires "online" in every QUERY response.
                ["online"] = true
            };

            if (!_deviceById.TryGetValue(id, out var device))
            {
                _logger.LogWarning("QUERY for unknown device id: {0}", id);
                state["online"] = false;
                return state;
            }

            // Only collect state if the device advertises willReportState.
            bool willReport = device.WillReportState
                || device.Features.Any(f => f.Instance is IDeviceSupportWithState);

            if (willReport)
            {
                // Gather state from every feature that supports it.
                foreach (var feature in device.Features
                    .Select(f => f.Instance)
                    .OfType<IDeviceSupportWithState>())
                {
                    try
                    {
                        var featureState = await feature.GetStateAsync(device.Definition);
                        if (featureState != null)
                        {
                            foreach (var kv in featureState)
                                state[kv.Key] = kv.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "GetStateAsync failed for device {0} feature {1}",
                            id, feature.GetType().Name);
                        state["online"] = false;
                    }
                }
            }

            return state;
        }

        // ─────────────────────────────────────────────────────────────
        // EXECUTE — run commands on devices
        // ─────────────────────────────────────────────────────────────
        public async Task<ExecuteActionResponse> ExecuteAsync(string requestId, ExecutePayload executePayload)
        {
            var commandResponses = new List<CommandResult>();

            foreach (var command in executePayload.Commands)
            {
                var successIds = new HashSet<string>();
                var errorIdsByCode = new Dictionary<string, HashSet<string>>();

                foreach (var device in command.Devices)
                {
                    foreach (var exec in command.Execution)
                    {
                        var errorCode = (string)null;

                        if (_deviceById.TryGetValue(device.Id, out var deviceImpl))
                        {
                            try
                            {
                                _logger.LogInformation(
                                    "Begin Execute: Device={0}({1}); Command={2}",
                                    deviceImpl.Definition.Name, deviceImpl.Definition.Id, exec.Command);

                                await deviceImpl.InvokeAsync(exec);
                                successIds.Add(device.Id);

                                // ── Optional: push updated state after execute ──────────────
                                // If the device reports state, collect it and include in the
                                // EXECUTE response so Google Home reflects changes immediately.
                                Dictionary<string, object> updatedState = null;
                                bool willReport = deviceImpl.WillReportState
                                    || deviceImpl.Features.Any(f => f.Instance is IDeviceSupportWithState);

                                if (willReport)
                                {
                                    updatedState = await GetStateFromDeviceIdAsync(device.Id);
                                }
                                // ──────────────────────────────────────────────────────────────

                                _logger.LogInformation(
                                    "End Execute: Device={0}({1}); Command={2}; Success",
                                    deviceImpl.Definition.Name, deviceImpl.Definition.Id, exec.Command);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex,
                                    "Execute failed: Device={0}({1}); Command={2}",
                                    deviceImpl.Definition.Name, deviceImpl.Definition.Id, exec.Command);
                                errorCode = "deviceTurnedOff"; // fallback Google-defined error code
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Execute for unknown device id: {0}", device.Id);
                            errorCode = "deviceNotFound";
                        }

                        if (errorCode != null)
                        {
                            if (!errorIdsByCode.ContainsKey(errorCode))
                                errorIdsByCode[errorCode] = new HashSet<string>();
                            errorIdsByCode[errorCode].Add(device.Id);
                        }
                    }
                }

                if (successIds.Any())
                {
                    commandResponses.Add(new CommandResult
                    {
                        Ids = successIds.ToArray(),
                        Status = "SUCCESS",
                    });
                }

                foreach (var errorCode in errorIdsByCode.Keys)
                {
                    if (errorIdsByCode[errorCode].Any())
                    {
                        commandResponses.Add(new CommandResult
                        {
                            Ids = errorIdsByCode[errorCode].ToArray(),
                            Status = "ERROR",
                            ErrorCode = errorCode,
                        });
                    }
                }
            }

            return new ExecuteActionResponse
            {
                RequestId = requestId,
                Payload = new ExecuteActionResponse.ExecuteActionPayload
                {
                    Commands = commandResponses.ToArray()
                }
            };
        }
    }
}
