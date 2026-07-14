using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.INDI;
using NINA.INDI.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Exposes Three Point Polar Alignment plugin settings via REST API.
/// Settings are read/written via reflection on NINA.Plugins.PolarAlignment.Properties.Settings
/// so the Touch-N-Stars backend requires no compile-time reference to the PolarAlignment plugin.
/// </summary>
public class TppaOatController : WebApiController
{
    private static readonly Type SettingsType = Type.GetType(
        "NINA.Plugins.PolarAlignment.Properties.Settings, NINA.Plugins.PolarAlignment");

    // ── defaults (mirrors Settings.settings) ────────────────────────────────
    private static readonly Dictionary<string, object> Defaults = new()
    {
        ["DefaultMoveRate"]                = 3.0,
        ["DefaultEastDirection"]           = true,
        ["MoveTimeoutFactor"]              = 2.0,
        ["DefaultTargetDistance"]          = 10,
        ["DefaultSearchRadius"]            = 10.0,
        ["DefaultAzimuthOffset"]           = 1.0,
        ["DefaultAltitudeOffset"]          = 2.0,
        ["AlignmentTolerance"]             = 0.0,
        ["RefractionAdjustment"]           = false,
        ["StopTrackingWhenDone"]           = true,
        ["AutomatedAdjustmentSettleTime"]  = 2.0,
        ["AutoPause"]                      = false,
        ["LogError"]                       = false,
        // OAT
        ["SelectedPolarAlignmentSystem"]   = "None",
        ["OATDeviceName"]                  = "LX200 OpenAstroTech",
        ["OATDoAutomatedAdjustments"]      = false,
        ["OATSettleTime"]                  = 2.0,
        ["OATXGearRatio"]                  = 1.0,
        ["OATYGearRatio"]                  = 1.0,
        ["OATXBacklashCompensation"]       = 0.0,
        ["OATReverseAzimuth"]              = false,
        ["OATReverseAltitude"]             = false,
    };

    // ── keys that have a "live" wrapper property on the running VM/plugin instance ─────────
    // Writing through these wrapper setters (instead of poking Properties.Settings.Default
    // directly via reflection) matters because NINA's own Options WPF page binds TwoWay to
    // these exact wrapper properties. A raw Settings.Default write never raises the wrapper's
    // PropertyChanged, so any live WPF binding (e.g. Options page open via VNC, or simply kept
    // alive in memory) keeps its OLD cached value — and the next time that binding re-pushes
    // (e.g. on visibility/re-render), it silently overwrites what we just saved via REST.
    // This was the root cause of "SelectedPolarAlignmentSystem / AlignmentTolerance /
    // OATDoAutomatedAdjustments reset as soon as the TPPA settings dialog is closed".
    private static readonly HashSet<string> PluginInstanceKeys = new()
    {
        "DefaultMoveRate", "DefaultEastDirection", "MoveTimeoutFactor",
        "DefaultTargetDistance", "DefaultSearchRadius", "DefaultAzimuthOffset",
        "DefaultAltitudeOffset", "AlignmentTolerance", "RefractionAdjustment",
        "StopTrackingWhenDone", "AutoPause", "LogError",
    };

    // OAT-prefixed settings key -> property name on the live OATAlignmentVM instance
    private static readonly Dictionary<string, string> OatVmPropertyMap = new()
    {
        ["OATDeviceName"]             = "OATDeviceName",
        ["OATDoAutomatedAdjustments"] = "DoAutomatedAdjustments",
        ["OATSettleTime"]             = "AutomatedAdjustmentSettleTime",
        ["OATXGearRatio"]             = "XGearRatio",
        ["OATYGearRatio"]             = "YGearRatio",
        ["OATXBacklashCompensation"]  = "XBacklashCompensation",
        ["OATReverseAzimuth"]         = "ReverseAzimuth",
        ["OATReverseAltitude"]        = "ReverseAltitude",
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    private static object GetSettingsInstance()
    {
        if (SettingsType == null) return null;
        return SettingsType
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
    }

    private static Type GetPluginType() =>
        Type.GetType("NINA.Plugins.PolarAlignment.PolarAlignmentPlugin, NINA.Plugins.PolarAlignment");

    private static object GetPluginInstance() =>
        GetPluginType()?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    private static object GetOatVmInstance() =>
        GetPluginType()?.GetProperty("OATAlignmentVM", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    private static object ReadProperty(object settings, string name)
    {
        try
        {
            var val = SettingsType.GetProperty(name)?.GetValue(settings);
            // Expose float as double so JSON stays consistent
            return val is float f ? (double)f : val;
        }
        catch { return null; }
    }

    private static void WriteProperty(object settings, string name, JsonElement element)
    {
        var prop = SettingsType.GetProperty(name);
        if (prop == null) return;
        SetTypedValue(settings, prop, element);
    }

    private static void SetTypedValue(object target, PropertyInfo prop, JsonElement element)
    {
        object value;
        var t = prop.PropertyType;

        if      (t == typeof(bool))   value = element.GetBoolean();
        else if (t == typeof(double)) value = element.GetDouble();
        else if (t == typeof(float))  value = (float)element.GetDouble();
        else if (t == typeof(int))    value = element.GetInt32();
        else if (t == typeof(string)) value = element.GetString();
        else return;

        prop.SetValue(target, value);
    }

    /// <summary>
    /// Writes one option, routing through the live plugin/VM wrapper property when one exists
    /// (so RaisePropertyChanged fires and any live WPF binding stays in sync), falling back to
    /// a raw Properties.Settings.Default write for keys without a live wrapper.
    /// </summary>
    private static void WriteOption(string key, JsonElement element, object settings)
    {
        var pluginType = GetPluginType();

        if (key == "SelectedPolarAlignmentSystem")
        {
            var pluginInstance = GetPluginInstance();
            var enumType = Type.GetType("NINA.Plugins.PolarAlignment.PolarAlignmentSystemType, NINA.Plugins.PolarAlignment");
            if (pluginInstance != null && enumType != null)
            {
                var raw = element.GetString();
                try
                {
                    var enumValue = Enum.Parse(enumType, raw ?? "None", ignoreCase: true);
                    pluginType.GetProperty("SelectedPolarAlignmentSystem")?.SetValue(pluginInstance, enumValue);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[TppaOatController] Could not parse SelectedPolarAlignmentSystem='{raw}' as enum: {ex.Message}");
                }
            }
        }
        else if (PluginInstanceKeys.Contains(key))
        {
            var pluginInstance = GetPluginInstance();
            var prop = pluginType?.GetProperty(key);
            if (pluginInstance != null && prop != null)
            {
                SetTypedValue(pluginInstance, prop, element);
                return;
            }
        }
        else if (OatVmPropertyMap.TryGetValue(key, out var vmPropName))
        {
            var oatVm = GetOatVmInstance();
            var prop = oatVm?.GetType().GetProperty(vmPropName);
            if (oatVm != null && prop != null)
            {
                SetTypedValue(oatVm, prop, element);
                return;
            }
        }

        // Fallback: no live instance available yet (plugin not fully initialised) or the key
        // has no live wrapper (e.g. the generic AutomatedAdjustmentSettleTime, which OAT doesn't
        // actually consume — OAT reads OATSettleTime instead via ActiveSystem).
        WriteProperty(settings, key, element);
    }

    private static void SetTypedValueRaw(object target, PropertyInfo prop, object rawValue)
    {
        var value = rawValue;
        if (prop.PropertyType == typeof(float) && value is double d)
            value = (float)d;
        else if (prop.PropertyType == typeof(int) && value is double di)
            value = (int)di;
        prop.SetValue(target, value);
    }

    /// <summary>Same routing as <see cref="WriteOption"/> but for an already-typed CLR value
    /// (used by PostReset, which iterates <see cref="Defaults"/> rather than parsing JSON).</summary>
    private static void WriteOptionRaw(string key, object rawValue, object settings)
    {
        var pluginType = GetPluginType();

        if (key == "SelectedPolarAlignmentSystem")
        {
            var pluginInstance = GetPluginInstance();
            var enumType = Type.GetType("NINA.Plugins.PolarAlignment.PolarAlignmentSystemType, NINA.Plugins.PolarAlignment");
            if (pluginInstance != null && enumType != null)
            {
                try
                {
                    var enumValue = Enum.Parse(enumType, (rawValue as string) ?? "None", ignoreCase: true);
                    pluginType.GetProperty("SelectedPolarAlignmentSystem")?.SetValue(pluginInstance, enumValue);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[TppaOatController] Could not parse SelectedPolarAlignmentSystem='{rawValue}' as enum: {ex.Message}");
                }
            }
        }
        else if (PluginInstanceKeys.Contains(key))
        {
            var pluginInstance = GetPluginInstance();
            var prop = pluginType?.GetProperty(key);
            if (pluginInstance != null && prop != null)
            {
                SetTypedValueRaw(pluginInstance, prop, rawValue);
                return;
            }
        }
        else if (OatVmPropertyMap.TryGetValue(key, out var vmPropName))
        {
            var oatVm = GetOatVmInstance();
            var prop = oatVm?.GetType().GetProperty(vmPropName);
            if (oatVm != null && prop != null)
            {
                SetTypedValueRaw(oatVm, prop, rawValue);
                return;
            }
        }

        var fallbackProp = SettingsType.GetProperty(key);
        if (fallbackProp != null)
            SetTypedValueRaw(settings, fallbackProp, rawValue);
    }

    private static Dictionary<string, object> BuildOptions(object settings)
    {
        var result = new Dictionary<string, object>();
        foreach (var key in Defaults.Keys)
        {
            var value = ReadProperty(settings, key) ?? Defaults[key];
            result[key] = new { Value = value, Default = Defaults[key] };
        }
        return result;
    }

    /// <summary>
    /// Reads the raw request body and parses it as a JsonDocument.
    /// EmbedIO/Swan does not correctly deserialize string values into JsonElement,
    /// so we bypass GetRequestDataAsync and use System.Text.Json directly.
    /// </summary>
    private async Task<JsonDocument> ReadBodyAsync()
    {
        using var reader = new StreamReader(HttpContext.Request.InputStream);
        var raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw)) raw = "{}";
        return JsonDocument.Parse(raw);
    }

    // ── endpoints ────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/tppa/oat/options — returns all TPPA/OAT settings with their current values.
    /// Namespaced under /tppa/oat/* (rather than /tppa/options) to avoid colliding with the
    /// generic TPPAController's /tppa/options route, which serves a different, non-OAT settings set.
    /// </summary>
    [Route(HttpVerbs.Get, "/tppa/oat/options")]
    public async Task<object> GetOptions()
    {
        var settings = GetSettingsInstance();
        if (settings == null)
        {
            Logger.Warning("[TppaOatController] PolarAlignment plugin not loaded – returning defaults");
            var fallback = new Dictionary<string, object>();
            foreach (var kv in Defaults)
                fallback[kv.Key] = new { Value = kv.Value, Default = kv.Value };
            return await Task.FromResult<object>(new { Success = true, Options = fallback });
        }

        return await Task.FromResult<object>(new { Success = true, Options = BuildOptions(settings) });
    }

    /// <summary>POST /api/tppa/oat/options — updates one or more settings. Body: { "Key": value, … }</summary>
    [Route(HttpVerbs.Post, "/tppa/oat/options")]
    public async Task<object> PostOptions()
    {
        var settings = GetSettingsInstance();
        if (settings == null)
            return new { Success = false, Error = "PolarAlignment plugin not loaded" };

        try
        {
            using var doc = await ReadBodyAsync();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (Defaults.ContainsKey(prop.Name))
                    WriteOption(prop.Name, prop.Value, settings);
                else
                    Logger.Warning($"[TppaOatController] Unknown option key ignored: {prop.Name}");
            }
            // Safety net: the live wrapper setters already call CoreUtil.SaveSettings internally,
            // but this covers keys that fell back to a raw Settings.Default write.
            SettingsType.GetMethod("Save")?.Invoke(settings, null);
            return new { Success = true };
        }
        catch (Exception ex)
        {
            Logger.Error($"[TppaOatController] PostOptions error: {ex.Message}");
            return new { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// POST /api/tppa/connect — explicitly connects the active alignment system VM.
    /// Useful for testing the OAT connection without running a full polar alignment.
    /// Returns the current connection status.
    /// </summary>
    [Route(HttpVerbs.Post, "/tppa/connect")]
    public async Task<object> PostConnect()
    {
        try
        {
            // Resolve PolarAlignmentPlugin.OATAlignmentVM via reflection
            var pluginType = Type.GetType(
                "NINA.Plugins.PolarAlignment.PolarAlignmentPlugin, NINA.Plugins.PolarAlignment");
            if (pluginType == null)
                return new { Success = false, Error = "PolarAlignment plugin not loaded" };

            var vmProp = pluginType.GetProperty("OATAlignmentVM",
                BindingFlags.Public | BindingFlags.Static);
            var vm = vmProp?.GetValue(null);
            if (vm == null)
                return new { Success = false, Error = "OATAlignmentVM is null — plugin not initialised yet" };

            // Check whether already connected
            var connectedProp = vm.GetType().GetProperty("Connected");
            var isConnected = (bool)(connectedProp?.GetValue(vm) ?? false);

            if (!isConnected)
            {
                // Execute the ConnectCommand
                var cmdProp = vm.GetType().GetProperty("ConnectCommand");
                var cmd = cmdProp?.GetValue(vm) as System.Windows.Input.ICommand;
                if (cmd != null && cmd.CanExecute(null))
                    cmd.Execute(null);

                // Wait up to 10 s
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!(bool)(connectedProp.GetValue(vm) ?? false) && DateTime.UtcNow < deadline)
                    await Task.Delay(200);

                isConnected = (bool)(connectedProp?.GetValue(vm) ?? false);
            }

            return new { Success = true, Connected = isConnected };
        }
        catch (Exception ex)
        {
            Logger.Error($"[TppaOatController] PostConnect error: {ex}");
            return new { Success = false, Error = ex.Message };
        }
    }

    /// <summary>GET /api/tppa/status — returns connection status of the active alignment system.</summary>
    [Route(HttpVerbs.Get, "/tppa/status")]
    public async Task<object> GetStatus()
    {
        try
        {
            var pluginType = Type.GetType(
                "NINA.Plugins.PolarAlignment.PolarAlignmentPlugin, NINA.Plugins.PolarAlignment");
            if (pluginType == null)
                return await Task.FromResult<object>(new { Success = false, Error = "PolarAlignment plugin not loaded" });

            var settings = GetSettingsInstance();
            var selectedSystem = settings != null
                ? (string)ReadProperty(settings, "SelectedPolarAlignmentSystem") ?? "None"
                : "None";

            var vmPropName = selectedSystem switch {
                "OAT"  => "OATAlignmentVM",
                "UPAS" => "UniversalPolarAlignmentVM",
                "OAPA" => "UniversalPolarAlignmentOAPAVM",
                _      => null
            };

            bool connected = false;
            if (vmPropName != null)
            {
                var vm = pluginType.GetProperty(vmPropName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (vm != null)
                    connected = (bool)(vm.GetType().GetProperty("Connected")?.GetValue(vm) ?? false);
            }

            return await Task.FromResult<object>(new {
                Success = true,
                SelectedSystem = selectedSystem,
                Connected = connected
            });
        }
        catch (Exception ex)
        {
            return await Task.FromResult<object>(new { Success = false, Error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/tppa/test-move — connects OATAlignmentVM and sends a small test nudge.
    /// Body: { "axis": "AZ"|"ALT", "arcmin": 1.0 }
    /// Used to validate the full code path (connect → INDI property → motor) without needing stars.
    /// </summary>
    [Route(HttpVerbs.Post, "/tppa/test-move")]
    public async Task<object> PostTestMove()
    {
        try
        {
            using var doc = await ReadBodyAsync();
            var root   = doc.RootElement;
            var axis   = root.TryGetProperty("axis",   out var axEl) ? axEl.GetString() : "AZ";
            var arcmin = root.TryGetProperty("arcmin", out var amEl) ? amEl.GetDouble() : 1.0;

            var pluginType = Type.GetType(
                "NINA.Plugins.PolarAlignment.PolarAlignmentPlugin, NINA.Plugins.PolarAlignment");
            if (pluginType == null)
                return new { Success = false, Error = "PolarAlignment plugin not loaded" };

            var vmProp = pluginType.GetProperty("OATAlignmentVM", BindingFlags.Public | BindingFlags.Static);
            var vm = vmProp?.GetValue(null);
            if (vm == null)
                return new { Success = false, Error = "OATAlignmentVM not initialised" };

            var vmType        = vm.GetType();
            var connectedProp = vmType.GetProperty("Connected");
            var isConnected   = (bool)(connectedProp?.GetValue(vm) ?? false);

            // Connect if needed
            if (!isConnected)
            {
                var cmdProp = vmType.GetProperty("ConnectCommand");
                var cmd = cmdProp?.GetValue(vm) as System.Windows.Input.ICommand;
                if (cmd != null && cmd.CanExecute(null)) cmd.Execute(null);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!(bool)(connectedProp.GetValue(vm) ?? false) && DateTime.UtcNow < deadline)
                    await Task.Delay(200);
                isConnected = (bool)(connectedProp?.GetValue(vm) ?? false);
            }

            if (!isConnected)
                return new { Success = false, Error = "Could not connect OATAlignmentVM — check NINA log" };

            // Call TryNudgeX or TryNudgeY
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            bool moved;
            if (axis?.ToUpperInvariant() == "ALT")
            {
                var method = vmType.GetMethod("TryNudgeY");
                moved = await (Task<bool>)method.Invoke(vm, [(float)arcmin, cts.Token]);
            }
            else
            {
                var method = vmType.GetMethod("TryNudgeX");
                moved = await (Task<bool>)method.Invoke(vm, [(float)arcmin, cts.Token]);
            }

            return new { Success = moved, Axis = axis, Arcmin = arcmin,
                         Message = moved ? "Motor move completed" : "Motor move failed — check NINA log" };
        }
        catch (Exception ex)
        {
            Logger.Error($"[TppaOatController] PostTestMove error: {ex}");
            return new { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// POST /api/tppa/test-indi — sends POLAR_AZ or POLAR_ALT directly via INDIClient,
    /// bypassing OATAlignmentVM entirely. Use this to verify the INDI layer works.
    /// Body: { "axis": "AZ"|"ALT", "arcmin": 2.0, "device": "LX200 OpenAstroTech" }
    /// </summary>
    [Route(HttpVerbs.Post, "/tppa/test-indi")]
    public async Task<object> PostTestIndi()
    {
        try
        {
            using var doc = await ReadBodyAsync();
            var root   = doc.RootElement;
            var axis   = root.TryGetProperty("axis",   out var axEl)  ? axEl.GetString()  : "AZ";
            var arcmin = root.TryGetProperty("arcmin", out var amEl)  ? amEl.GetDouble()  : 2.0;
            var device = root.TryGetProperty("device", out var devEl) ? devEl.GetString() : "LX200 OpenAstroTech";

            if (!INDIClient.Instance.IsConnected)
                return new { Success = false, Error = "INDI server not connected" };

            if (!INDIClient.Instance.IsDeviceKnown(device))
                return new { Success = false, Error = $"INDI device '{device}' not known" };

            string propName    = axis?.ToUpperInvariant() == "ALT" ? "POLAR_ALT" : "POLAR_AZ";
            string elementName = axis?.ToUpperInvariant() == "ALT" ? "OAT_POLAR_ALT" : "OAT_POLAR_AZ";

            var prop = new INDINumberProperty {
                DeviceName = device,
                Name       = propName,
                Numbers    = new List<INDINumber> {
                    new INDINumber { Name = elementName, Value = arcmin }
                }
            };

            Logger.Info($"[TppaOatController] test-indi: sending {propName}={arcmin} to '{device}'");
            INDIClient.Instance.SendProperty(prop);

            return await Task.FromResult<object>(new {
                Success = true,
                Device  = device,
                Axis    = axis,
                Arcmin  = arcmin,
                Message = $"Sent {propName}={arcmin} to INDI. Check motors."
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"[TppaOatController] PostTestIndi error: {ex}");
            return new { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// GET /api/tppa/indi-props?device=LX200+OpenAstroTech — dumps POLAR_AZ and POLAR_ALT
    /// property details as seen by INDIClient, to verify element names and current state.
    /// </summary>
    [Route(HttpVerbs.Get, "/tppa/indi-props")]
    public async Task<object> GetIndiProps()
    {
        try
        {
            var device = HttpContext.Request.QueryString["device"] ?? "LX200 OpenAstroTech";

            if (!INDIClient.Instance.IsConnected)
                return await Task.FromResult<object>(new { Success = false, Error = "INDI not connected" });

            // Use reflection to get the _allProperties dictionary from INDIClient
            var clientType   = typeof(INDIClient);
            var allPropField = clientType.GetField("_allProperties",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var allProps     = allPropField?.GetValue(INDIClient.Instance);

            var result = new List<object>();
            if (allProps != null)
            {
                // _allProperties is Dictionary<string, Dictionary<string, INDIProperty>>
                // key = deviceName, value = dict of propName -> INDIProperty
                var deviceDict = allProps as System.Collections.IDictionary;
                if (deviceDict != null && deviceDict.Contains(device))
                {
                    var props = deviceDict[device] as System.Collections.IDictionary;
                    if (props != null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in props)
                        {
                            var propName = entry.Key?.ToString();
                            if (propName != "POLAR_AZ" && propName != "POLAR_ALT") continue;

                            var prop = entry.Value;
                            var propType = prop?.GetType();
                            var stateProp = propType?.GetProperty("State");
                            var namesProp = propType?.GetProperty("Numbers");
                            var state = stateProp?.GetValue(prop)?.ToString();
                            var numbers = namesProp?.GetValue(prop);

                            var elements = new List<object>();
                            if (numbers is System.Collections.IEnumerable numList)
                            {
                                foreach (var n in numList)
                                {
                                    var nType = n?.GetType();
                                    var eName  = nType?.GetProperty("Name")?.GetValue(n)?.ToString();
                                    var eValue = nType?.GetProperty("Value")?.GetValue(n);
                                    elements.Add(new { Name = eName, Value = eValue });
                                }
                            }
                            result.Add(new { Property = propName, State = state, Elements = elements });
                        }
                    }
                }
            }

            return await Task.FromResult<object>(new {
                Success = true,
                Device  = device,
                IsKnown = INDIClient.Instance.IsDeviceKnown(device),
                Props   = result
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"[TppaOatController] GetIndiProps error: {ex}");
            return await Task.FromResult<object>(new { Success = false, Error = ex.Message });
        }
    }

    /// <summary>POST /api/tppa/oat/reset — resets all TPPA/OAT settings to their defaults.</summary>
    [Route(HttpVerbs.Post, "/tppa/oat/reset")]
    public async Task<object> PostReset()
    {
        var settings = GetSettingsInstance();
        if (settings == null)
            return new { Success = false, Error = "PolarAlignment plugin not loaded" };

        try
        {
            foreach (var kv in Defaults)
                WriteOptionRaw(kv.Key, kv.Value, settings);

            SettingsType.GetMethod("Save")?.Invoke(settings, null);
            return await GetOptions();
        }
        catch (Exception ex)
        {
            Logger.Error($"[TppaOatController] PostReset error: {ex.Message}");
            return new { Success = false, Error = ex.Message };
        }
    }
}
