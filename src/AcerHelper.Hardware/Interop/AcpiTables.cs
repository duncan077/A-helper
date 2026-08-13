// SPDX-License-Identifier: GPL-3.0-or-later
//
// ACPI firmware table extraction.
//
// The WMI methods on AcerGamingFunction are implemented by ACPI control methods
// in the BIOS. Dumping those tables and decompiling them shows what a method
// actually does with its input - which is the authoritative answer for
// undocumented ones like SetGamingFanTable, and needs no vendor software.
//
// This is the standard technique behind acer-wmi.c and every other platform
// driver; Linux ships acpidump/iasl for exactly this. It reads firmware the
// machine owner already possesses, and touches no Acer application.
//
// Read-only: GetSystemFirmwareTable copies tables out, it cannot modify them.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AcerHelper.Hardware.Interop;

[SupportedOSPlatform("windows")]
public static unsafe partial class AcpiTables
{
    // The provider signature is documented as the C multi-character literal
    // 'ACPI', which is big-endian: 'A'<<24 | 'C'<<16 | 'P'<<8 | 'I'. Table IDs
    // are the opposite - the four signature bytes in memory order. Using one
    // convention for both makes EnumSystemFirmwareTables return nothing.
    private const uint ProviderAcpi = 0x41435049;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint EnumSystemFirmwareTables(uint provider, byte* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint provider, uint tableId,
                                                       byte* buffer, uint bufferSize);

    private static uint Signature(string fourCc)
    {
        var bytes = Encoding.ASCII.GetBytes(fourCc.PadRight(4)[..4]);
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }

    private static string SignatureText(uint value) =>
        new([(char)(value & 0xFF), (char)((value >> 8) & 0xFF),
             (char)((value >> 16) & 0xFF), (char)((value >> 24) & 0xFF)]);

    /// <summary>Signatures of every ACPI table the firmware exposes.</summary>
    public static IReadOnlyList<string> Enumerate()
    {
        var size = EnumSystemFirmwareTables(ProviderAcpi, null, 0);
        if (size == 0) return [];

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (EnumSystemFirmwareTables(ProviderAcpi, p, size) == 0) return [];
        }

        var result = new List<string>();
        for (var offset = 0; offset + 4 <= buffer.Length; offset += 4)
        {
            var id = (uint)(buffer[offset] | (buffer[offset + 1] << 8)
                            | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
            result.Add(SignatureText(id));
        }

        return result;
    }

    /// <summary>Raw bytes of one table, or null when it is not present.</summary>
    public static byte[]? Read(string signature)
    {
        var id = Signature(signature);

        var size = GetSystemFirmwareTable(ProviderAcpi, id, null, 0);
        if (size == 0) return null;

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            return GetSystemFirmwareTable(ProviderAcpi, id, p, size) == 0 ? null : buffer;
        }
    }

    /// <summary>
    /// Writes every ACPI table to <paramref name="directory"/> as .aml files.
    /// </summary>
    /// <remarks>
    /// Several SSDTs share the signature "SSDT" and Windows returns only the
    /// first through this API, so the DSDT is the one that matters - on Acer it
    /// carries the WMxx control methods behind the gaming interface.
    /// </remarks>
    public static IReadOnlyList<(string Signature, string Path, int Bytes)> DumpAll(string directory)
    {
        Directory.CreateDirectory(directory);

        var written = new List<(string, string, int)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // DSDT is NOT returned by EnumSystemFirmwareTables - it is referenced
        // through the FADT rather than listed in the RSDT/XSDT - but it can be
        // fetched by name, and it is the table that matters here.
        foreach (var signature in new[] { "DSDT" }.Concat(Enumerate()))
        {
            if (!seen.Add(signature)) continue;

            var bytes = Read(signature);
            if (bytes is null) continue;

            var path = Path.Combine(directory, $"{signature.Trim()}.aml");
            File.WriteAllBytes(path, bytes);
            written.Add((signature, path, bytes.Length));
        }

        return written;
    }

    // ------------------------------------------------------------- registry
    //
    // GetSystemFirmwareTable returns only the FIRST table for a duplicated
    // signature, so on a machine with a dozen SSDTs it hands back one of them
    // and silently hides the rest - including, on Acer, the one that actually
    // carries the gaming WMI methods.
    //
    // Windows stores every table separately under HKLM\HARDWARE\ACPI, keyed
    // SSDT, SSD1, SSD2 ... so reading there gets the complete set.

    private static readonly nint HKEY_LOCAL_MACHINE = unchecked((nint)0x80000002);
    private const uint KEY_READ = 0x20019;

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(nint key, string subKey, uint options, uint access, out nint result);

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegEnumKeyEx(nint key, uint index, [Out] char[] name, ref uint nameLength,
                                            nint reserved, nint className, nint classLength, nint lastWrite);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueEx(nint key, string? valueName, nint reserved,
                                               out uint type, [Out] byte[]? data, ref uint dataLength);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(nint key);

    private static List<string> ChildKeys(nint key)
    {
        var names = new List<string>();
        var buffer = new char[256];

        for (uint i = 0; ; i++)
        {
            var length = (uint)buffer.Length;
            if (RegEnumKeyEx(key, i, buffer, ref length, 0, 0, 0, 0) != 0) break;
            names.Add(new string(buffer, 0, (int)length));
        }

        return names;
    }

    private static byte[]? ReadBinaryValue(nint key, string valueName)
    {
        uint size = 0;
        if (RegQueryValueEx(key, valueName, 0, out _, null, ref size) != 0 || size == 0) return null;

        var data = new byte[size];
        return RegQueryValueEx(key, valueName, 0, out _, data, ref size) == 0 ? data : null;
    }

    /// <summary>
    /// Every ACPI table Windows recorded in the registry, keyed by its registry
    /// name (DSDT, SSDT, SSD1, ...). This is the only way to reach the SSDTs
    /// that GetSystemFirmwareTable hides behind a duplicated signature.
    /// </summary>
    public static IReadOnlyList<(string Name, byte[] Data)> ReadFromRegistry()
    {
        var results = new List<(string, byte[])>();

        if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, @"HARDWARE\ACPI", 0, KEY_READ, out var root) != 0)
            return results;

        try
        {
            // Layout is ACPI\<signature>\<OEM id>\<OEM table id>\<revision>,
            // with the table itself in a single binary value.
            foreach (var signature in ChildKeys(root))
            {
                if (RegOpenKeyEx(root, signature, 0, KEY_READ, out var sigKey) != 0) continue;

                try
                {
                    foreach (var oem in ChildKeys(sigKey))
                    {
                        if (RegOpenKeyEx(sigKey, oem, 0, KEY_READ, out var oemKey) != 0) continue;

                        try
                        {
                            foreach (var table in ChildKeys(oemKey))
                            {
                                if (RegOpenKeyEx(oemKey, table, 0, KEY_READ, out var tableKey) != 0) continue;

                                try
                                {
                                    foreach (var revision in ChildKeys(tableKey))
                                    {
                                        if (RegOpenKeyEx(tableKey, revision, 0, KEY_READ, out var revKey) != 0)
                                            continue;

                                        try
                                        {
                                            var data = ReadBinaryValue(revKey, "00000000");
                                            if (data is { Length: > 4 }) results.Add((signature, data));
                                        }
                                        finally { RegCloseKey(revKey); }
                                    }
                                }
                                finally { RegCloseKey(tableKey); }
                            }
                        }
                        finally { RegCloseKey(oemKey); }
                    }
                }
                finally { RegCloseKey(sigKey); }
            }
        }
        finally { RegCloseKey(root); }

        return results;
    }

    /// <summary>
    /// Finds the ACPI method names behind a WMI GUID by scanning for its _WDG
    /// entry, so the right control method can be located in decompiled output.
    /// </summary>
    /// <remarks>
    /// _WDG is a packed array of 20-byte entries: 16-byte GUID, then a two-byte
    /// object id, an instance count and flags. An object id of "AB" means the
    /// methods are WMAB, with the WMI method id passed as an argument.
    /// </remarks>
    public static IReadOnlyList<string> FindWmiMethodNames(byte[] table, Guid guid)
    {
        var needle = guid.ToByteArray();   // little-endian, matching _WDG layout
        var names = new List<string>();

        for (var i = 0; i + 20 <= table.Length; i++)
        {
            var match = true;
            for (var j = 0; j < 16 && match; j++)
                if (table[i + j] != needle[j]) match = false;

            if (!match) continue;

            var objectId = new string([(char)table[i + 16], (char)table[i + 17]]);

            // Printable object ids are real; anything else is a coincidental
            // byte match rather than a _WDG entry.
            if (char.IsLetterOrDigit(objectId[0]) && char.IsLetterOrDigit(objectId[1]))
                names.Add($"WM{objectId}");
        }

        return names;
    }
}
