using System.Management;

namespace XpsManager.Core;

/// <summary>A single BIOS attribute as reported by the Dell WMI provider.</summary>
public sealed record BiosAttribute(
    string Name,
    string DisplayName,
    string CurrentValue,
    string DefaultValue,
    bool IsReadOnly,
    IReadOnlyList<string> PossibleValues,
    int? Min = null,
    int? Max = null);

public class BiosException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class BiosWriteException(string attribute, string value, int status)
    : BiosException($"BIOS rejected {attribute}={value}: {Describe(status)} (status {status})")
{
    public int Status { get; } = status;

    // Status codes documented for Dell's BIOSAttributeInterface.SetAttribute.
    static string Describe(int status) => status switch
    {
        1 => "failed",
        2 => "invalid parameter",
        3 => "access denied (BIOS password set?)",
        4 => "not supported",
        5 => "memory error",
        6 => "protocol error",
        _ => "unknown error",
    };
}

/// <summary>
/// Thin wrapper over Dell's BIOS attribute WMI interface (root\dcim\sysman\biosattributes).
/// Requires an elevated process. Only enumeration and integer attributes are surfaced.
/// </summary>
/// <remarks>
/// Enumerating attributes costs ~2 s (the provider walks all ~130 in firmware, even with a WHERE
/// clause), but fetching one by its key costs ~25 ms. So keys are discovered once, cached to disk,
/// and each fetched instance is checked by name; a stale cache triggers rediscovery.
/// </remarks>
public sealed class BiosClient
{
    const string Namespace = @"root\dcim\sysman\biosattributes";
    const string EnumClass = "EnumerationAttribute";
    const string IntClass = "IntegerAttribute";

    public static string DefaultKeyCachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XpsManager", "attribute-keys.json");

    readonly ManagementScope _scope = new(Namespace);
    readonly string? _keyCachePath;
    Dictionary<string, AttributeKey> _keys;

    /// <param name="keyCachePath">Where to persist attribute keys between runs; null keeps them in memory only.</param>
    public BiosClient(string? keyCachePath = null)
    {
        _keyCachePath = keyCachePath;
        _keys = AttributeKey.Load(keyCachePath);
        Wmi(_scope.Connect);
    }

    /// <summary>Reads every attribute (slow, ~2 s) and refreshes the key cache.</summary>
    public IReadOnlyDictionary<string, BiosAttribute> ReadAttributes()
    {
        var result = new Dictionary<string, BiosAttribute>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, AttributeKey>(StringComparer.OrdinalIgnoreCase);

        foreach (var cls in new[] { EnumClass, IntClass })
        {
            foreach (var mo in Query(cls))
            {
                using (mo)
                {
                    var attr = ToAttribute(mo, cls);
                    result[attr.Name] = attr;
                    keys[attr.Name] = new AttributeKey(cls, (string)mo["InstanceName"]);
                }
            }
        }

        _keys = keys;
        AttributeKey.Save(_keyCachePath, keys);
        return result;
    }

    /// <summary>Reads just the named attributes, by key where possible (fast).</summary>
    public IReadOnlyDictionary<string, BiosAttribute> ReadAttributes(IEnumerable<string> names)
    {
        var result = new Dictionary<string, BiosAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (TryReadByKey(name) is { } attr)
            {
                result[name] = attr;
                continue;
            }

            // Missing or stale key (e.g. first run, or a BIOS update renumbered instances).
            var all = ReadAttributes();
            return names.Where(all.ContainsKey).ToDictionary(n => n, n => all[n], StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    BiosAttribute? TryReadByKey(string name)
    {
        if (!_keys.TryGetValue(name, out var key))
            return null;

        var path = $"{key.ClassName}.InstanceName=\"{key.InstanceName.Replace(@"\", @"\\")}\"";
        try
        {
            using var mo = new ManagementObject(_scope, new ManagementPath(path), null);
            Wmi(mo.Get);
            var attr = ToAttribute(mo, key.ClassName);
            return string.Equals(attr.Name, name, StringComparison.OrdinalIgnoreCase) ? attr : null;
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.NotFound)
        {
            return null;
        }
    }

    static BiosAttribute ToAttribute(ManagementBaseObject mo, string className) => className == IntClass
        ? new BiosAttribute(
            Name: (string)mo["AttributeName"],
            DisplayName: mo["DisplayName"] as string ?? "",
            CurrentValue: Convert.ToString(mo["CurrentValue"]) ?? "",
            DefaultValue: Convert.ToString(mo["DefaultValue"]) ?? "",
            IsReadOnly: Convert.ToInt32(mo["ReadOnly"]) != 0,
            PossibleValues: [],
            Min: Convert.ToInt32(mo["LowerBound"]),
            Max: Convert.ToInt32(mo["UpperBound"]))
        : new BiosAttribute(
            Name: (string)mo["AttributeName"],
            DisplayName: mo["DisplayName"] as string ?? "",
            CurrentValue: mo["CurrentValue"] as string ?? "",
            DefaultValue: mo["DefaultValue"] as string ?? "",
            IsReadOnly: Convert.ToInt32(mo["ReadOnly"]) != 0,
            PossibleValues: mo["PossibleValue"] as string[] ?? []);

    /// <summary>Writes one attribute. Assumes no BIOS admin password is set.</summary>
    public void SetAttribute(string name, string value)
    {
        using var iface = Query("BIOSAttributeInterface").FirstOrDefault()
            ?? throw new BiosException("BIOSAttributeInterface instance not found.");
        using var inParams = iface.GetMethodParameters("SetAttribute");
        inParams["AttributeName"] = name;
        inParams["AttributeValue"] = value;
        inParams["SecType"] = 0u;
        inParams["SecHndCount"] = 0u;
        inParams["SecHandle"] = Array.Empty<byte>();

        var status = Wmi(() =>
        {
            using var outParams = iface.InvokeMethod("SetAttribute", inParams, null);
            return Convert.ToInt32(outParams["Status"]);
        });
        if (status != 0)
            throw new BiosWriteException(name, value, status);
    }

    List<ManagementObject> Query(string className) => Wmi(() =>
    {
        using var searcher = new ManagementObjectSearcher(_scope, new ObjectQuery($"SELECT * FROM {className}"));
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().ToList();
    });

    // Connect succeeds unelevated; access is only denied once instances are enumerated,
    // so every WMI touchpoint goes through this translation.
    static T Wmi<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception e) when (e is UnauthorizedAccessException ||
                                  e is ManagementException { ErrorCode: ManagementStatus.AccessDenied })
        {
            throw new BiosException("Access denied to the Dell BIOS interface. Run as administrator.", e);
        }
        catch (ManagementException e) when (e.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
        {
            throw new BiosException("Dell BIOS WMI interface not found on this machine.", e);
        }
    }

    static void Wmi(Action call) => Wmi(() => { call(); return 0; });
}
