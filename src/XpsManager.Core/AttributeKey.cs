using System.Text.Json;

namespace XpsManager.Core;

/// <summary>WMI key of a BIOS attribute instance, e.g. EnumerationAttribute / ACPI\PNP0C14\WBAT_36.</summary>
internal sealed record AttributeKey(string ClassName, string InstanceName)
{
    public static Dictionary<string, AttributeKey> Load(string? path)
    {
        var empty = new Dictionary<string, AttributeKey>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path))
            return empty;
        try
        {
            var keys = JsonSerializer.Deserialize<Dictionary<string, AttributeKey>>(File.ReadAllText(path));
            return keys is null ? empty : new(keys, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return empty; // A bad cache only costs one slow rediscovery.
        }
    }

    public static void Save(string? path, Dictionary<string, AttributeKey> keys)
    {
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: next run rediscovers.
        }
    }
}
