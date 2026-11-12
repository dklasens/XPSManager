using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace XpsManager.Core;

public static class BatteryIdentity
{
    public static string? Ppid => CachedPpid.Value;
    static readonly Lazy<string?> CachedPpid = new(() =>
    {
        try
        {
            using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT SMBiosData FROM MSSmBios_RawSMBiosTables");
            using var results = query.Get();
            foreach (ManagementObject row in results)
                using (row)
                    if (row["SMBiosData"] is byte[] table) return FindPpid(table);
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { }
        return null;
    });

    /// <summary>Dell exposes a PPID as an OEM string in SMBIOS type 22 on this XPS.
    /// Restrict discovery to one Dell battery record with one unambiguous PPID-shaped string.</summary>
    public static string? FindPpid(byte[] table)
    {
        string? found = null;
        int batteries = 0;
        for (int offset = 0; offset + 4 <= table.Length;)
        {
            int type = table[offset], length = table[offset + 1];
            if (length < 4 || offset + length > table.Length) return null;
            int end = offset + length;
            while (end + 1 < table.Length && (table[end] != 0 || table[end + 1] != 0)) end++;
            if (end + 1 >= table.Length) return null;
            if (type == 22)
            {
                if (++batteries > 1) return null;
                var strings = Encoding.ASCII.GetString(table, offset + length, end - offset - length).Split('\0');
                if (strings.Any(s => s.StartsWith("DELL ", StringComparison.OrdinalIgnoreCase)))
                {
                    var candidates = strings.Where(s => Regex.IsMatch(s, @"^[A-Z]{2}0[A-Z0-9]{17}(?:A[A-Z0-9]{2})?$", RegexOptions.CultureInvariant)).ToArray();
                    if (candidates.Length == 1) found = candidates[0];
                }
            }
            if (type == 127) break;
            offset = end + 2;
        }
        return found;
    }
}
