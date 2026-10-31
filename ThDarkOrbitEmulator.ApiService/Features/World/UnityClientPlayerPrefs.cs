using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ThDarkOrbitEmulator.ApiService.Features.World;

// The Unity client's Settings.InitLocalBooleanSetting reads PlayerPrefs "darkorbit_settings" (a JSON
// string dictionary, values parsed with SafeBool) but nothing in the client ever writes it back.
// Unity stores PlayerPrefs strings under HKCU\Software\{company}\{product} as "{key}_h{djb2-xor hash}"
// REG_BINARY UTF-8 values terminated by a NUL byte.
[SupportedOSPlatform("windows")]
public static class UnityClientPlayerPrefs
{
    private const string RegistryPath = @"Software\Bigpoint\DarkOrbit";
    private const string SettingsKey = "darkorbit_settings";
    private const string Force2DKey = "force2D";
    private static readonly Lock Gate = new();

    public static string ValueName(string key)
    {
        var hash = 5381u;
        foreach (var b in Encoding.UTF8.GetBytes(key))
            hash = unchecked(hash * 33) ^ b;
        return $"{key}_h{hash}";
    }

    public static string UpdateSettingsJson(string? json, string key, bool value)
    {
        Dictionary<string, string>? settings = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
            catch (JsonException) { }
        }

        settings ??= [];
        settings[key] = value ? "True" : "False";
        return JsonSerializer.Serialize(settings);
    }

    public static bool TrySetForce2D(bool force2D)
    {
        try
        {
            lock (Gate)
            {
                using var registryKey = Registry.CurrentUser.CreateSubKey(RegistryPath);
                var name = ValueName(SettingsKey);
                var current = registryKey.GetValue(name) is byte[] bytes
                    ? Encoding.UTF8.GetString(bytes).TrimEnd('\0')
                    : null;
                var updated = UpdateSettingsJson(current, Force2DKey, force2D);
                if (updated == current) return true;
                byte[] value = [.. Encoding.UTF8.GetBytes(updated), 0];
                registryKey.SetValue(name, value, RegistryValueKind.Binary);
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
