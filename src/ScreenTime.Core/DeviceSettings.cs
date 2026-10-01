using System.Text.Json;

namespace ScreenTime.Core;

/// <summary>
/// Settings that belong to the history rather than the install, so they live
/// in the data folder and survive a reset: <c>settings.json</c>.
/// </summary>
public sealed class DeviceSettings
{
    /// <summary>
    /// What this machine is called on the page head. Windows has nothing to
    /// read for it, and a hostname is rarely what a person calls their laptop.
    /// </summary>
    public string? DeviceLabel { get; set; }

    public string Label => string.IsNullOrWhiteSpace(DeviceLabel) ? Environment.MachineName : DeviceLabel.Trim();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static DeviceSettings Load(string dataDir)
    {
        try
        {
            var path = AppPaths.SettingsPath(dataDir);
            if (File.Exists(path)) return JsonSerializer.Deserialize<DeviceSettings>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = AppPaths.SettingsPath(dataDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
