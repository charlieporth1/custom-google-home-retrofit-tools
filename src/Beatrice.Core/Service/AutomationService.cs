using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
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

        // BUG FIX 1: Track device on/off state in memory so QUERY and EXECUTE
        // can return real state. Previously there was no state store at all,
        // causing QUERY to return an empty dict and the test suite to mark
        // devices as offline.
        private readonly ConcurrentDictionary<string, Dictionary<string, object>> _deviceStates;

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

            // Initialise every device as online=true, on=false
            _deviceStates = new ConcurrentDictionary<string, Dictionary<string, object>>(
                _deviceById.Keys.ToDictionary(
                    id => id,
                    id => new Dictionary<string, object>
                    {
                        { "online", true },
                        { "on", false }
                    }
                )
            );

            foreach (var device in _deviceById)
            {
                _logger.LogInformation(
                    "Device: Id={0}; Features={1}",
                    device.Key,
                    string.Join(",", device.Value.Features.Select(x => x.Instance.GetType().ToString())));
            }
        }

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

                    // BUG FIX 2: DISCONNECT was completely unhandled. The test
                    // suite sends this intent and expects a 200 OK response.
                    // An unhandled intent caused DispatchAsync to return null,
                    // which the controller would serialize to an empty body —
                    // a test failure.
                    case "action.devices.DISCONNECT":
                        return new { requestId = request.RequestId };
                }
            }

            return null;
        }

        public async Task<ExecuteActionResponse> ExecuteAsync(string requestId, ExecutePayload executePayload)
        {
            var commandResponses = new List<CommandResult>();

            foreach (var command in executePayload.Commands)
            {
                var successIds = new HashSet<string>();
                var successStates = new Dictionary<string, object>();
                var errorIdsByCode = new Dictionary<string, HashSet<string>>();

                foreach (var device in command.Devices)
                {
                    foreach (var exec in command.Execution)
                    {
                        string errorCode = null;

                        if (_deviceById.TryGetValue(device.Id, out var deviceImpl))
                        {
                            try
                            {
                                _logger.LogInformation(
                                    "Begin Execute: Device={0}({1}); Command={2}",
                                    deviceImpl.Definition.Name,
                                    deviceImpl.Definition.Id,
                                    exec.Command);

                                await deviceImpl.InvokeAsync(exec);

                                // BUG FIX 3: Update in-memory state so QUERY
                                // returns the correct state after EXECUTE.
                                var newState = BuildStateFromExecution(device.Id, exec);
                                _deviceStates[device.Id] = newState;

                                successIds.Add(device.Id);

                                // Use the last device's resulting state for the
                                // response (all devices in a batch share the same
                                // command so the states are identical).
                                foreach (var kv in newState)
                                    successStates[kv.Key] = kv.Value;

                                _logger.LogInformation(
                                    "End Execute: Device={0}({1}); Command={2}; Status=SUCCESS",
                                    deviceImpl.Definition.Name,
                                    deviceImpl.Definition.Id,
                                    exec.Command);
                            }
                            catch (Exception ex)
                            {
                                // BUG FIX 4: Was using String.Empty as errorCode.
                                // An empty string is not a valid Cloud-to-cloud
                                // error code and causes test failures. Use the
                                // canonical "commandInsertFailed" code instead.
                                errorCode = "commandInsertFailed";
                                _logger.LogError(ex,
                                    "End Execute: Device={0}({1}); Command={2}; Status=ERROR; ErrorCode={3}",
                                    deviceImpl.Definition.Name,
                                    deviceImpl.Definition.Id,
                                    exec.Command,
                                    errorCode);
                            }
                        }
                        else
                        {
                            // BUG FIX 5: Was calling deviceImpl.Definition.Name
                            // after a failed TryGetValue, causing a
                            // NullReferenceException. Log the ID instead.
                            errorCode = "deviceNotFound";
                            _logger.LogWarning(
                                "End Execute: DeviceId={0}; Command={1}; Status=ERROR; ErrorCode={2}",
                                device.Id,
                                exec.Command,
                                errorCode);
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
                        // BUG FIX 6: States was commented out. The test suite
                        // checks that EXECUTE returns the updated device state
                        // (at minimum "online": true). Without this, the
                        // "online" test fails even when the command succeeded.
                        States = successStates.Count > 0 ? successStates : new Dictionary<string, object>
                        {
                            { "online", true }
                        }
                    });
                }

                foreach (var errorCode in errorIdsByCode.Keys)
                {
                    if (errorIdsByCode[errorCode].Any())
                    {
                        commandResponses.Add(new CommandResult
                        {
                            // BUG FIX 7: Was using successIds.ToArray() for the
                            // error response — i.e. reporting the WRONG device
                            // IDs in the error block. Use the actual error IDs.
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

        // Build a new state dictionary reflecting the result of an execution.
        // Handles OnOff; other traits can be added here as needed.
        private Dictionary<string, object> BuildStateFromExecution(string deviceId, ExecutionItem exec)
        {
            var current = _deviceStates.TryGetValue(deviceId, out var existing)
                ? new Dictionary<string, object>(existing)
                : new Dictionary<string, object>();

            current["online"] = true;

            switch (exec.Command)
            {
                case "action.devices.commands.OnOff":
                    if (exec.Params != null && exec.Params.TryGetValue("on", out var onVal))
                        current["on"] = Convert.ToBoolean(onVal);
                    break;

                case "action.devices.commands.BrightnessAbsolute":
                    if (exec.Params != null && exec.Params.TryGetValue("brightness", out var brightness))
                        current["brightness"] = Convert.ToInt32(brightness);
                    break;

                // Add further command mappings here as new traits are implemented.
            }

            return current;
        }

        // BUG FIX 8 (QUERY shape): Was producing an *array* of state dicts with
        // no device IDs attached. Google's QUERY response requires a dictionary
        // keyed by device ID:
        //   "devices": { "id1": { "online": true, "on": false }, ... }
        // Previously the code passed Task.WhenAll(...) which returned
        // Dictionary<string,object>[] — an array — which is the wrong JSON shape
        // and caused all QUERY-based tests to fail.
        public async Task<QueryActionResponse> GetStatesAsync(string requestId, StatesPayload statesPayload)
        {
            var deviceStates = new Dictionary<string, Dictionary<string, object>>();

            foreach (var device in statesPayload.Devices)
            {
                deviceStates[device.Id] = await GetStateFromDeviceIdAsync(device.Id);
            }

            return new QueryActionResponse
            {
                RequestId = requestId,
                Payload = new QueryActionResponse.QueryActionPayload
                {
                    Devices = deviceStates
                }
            };
        }

        private Task<Dictionary<string, object>> GetStateFromDeviceIdAsync(string id)
        {
            if (!_deviceById.ContainsKey(id))
            {
                // Unknown device — report it as offline so the test suite gets
                // a valid (though error) response rather than a KeyNotFoundException.
                return Task.FromResult(new Dictionary<string, object>
                {
                    { "online", false },
                    { "status", "ERROR" },
                    { "errorCode", "deviceNotFound" }
                });
            }

            // Return a copy of the current tracked state (always includes "online").
            var state = _deviceStates.TryGetValue(id, out var existing)
                ? new Dictionary<string, object>(existing)
                : new Dictionary<string, object> { { "online", true }, { "on", false } };

            return Task.FromResult(state);
        }

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
                            WillReportState = x.Value.WillReportState,
                            RoomHint = x.Value.Definition.RoomHint,
                        };
                    }).ToArray()
                }
            });
        }
    }
}
