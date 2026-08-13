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
