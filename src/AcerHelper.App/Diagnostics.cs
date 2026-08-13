// SPDX-License-Identifier: GPL-3.0-or-later
//
// Minimal file logging. The app runs elevated on a machine the developer does
// not have, so an exception that only ever reaches a status bar is close to
// useless for diagnosis. Everything interesting also lands in a log file next
// to the executable.

using System.Runtime.InteropServices;
using AcerHelper.Hardware;
using AcerHelper.Hardware.Interop;

namespace AcerHelper.App;

internal static class Diagnostics
{
    private static readonly object Gate = new();

    public static string LogPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "acerhelper.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        try
        {
            lock (Gate) File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { /* logging must never take the app down */ }
    }

    public static void WriteException(string context, Exception ex)
    {
        Write($"{context}: {Describe(ex)}");
        if (ex.StackTrace is { } st)
            Write("    " + st.Replace(Environment.NewLine, Environment.NewLine + "    "));
    }

    /// <summary>
    /// Produces a message that identifies the failure rather than restating it.
    /// COM HRESULTs in particular are meaningless as decimal numbers.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        ComCallException com => $"{com.Message} [{HResultName(com.HResult32)}]",
        AcerWmiException acer => acer.Message,
        COMException com => $"COM [{HResultName(com.HResult)}] {com.Message}",
        UnauthorizedAccessException => "Not elevated. Restart the app as administrator.",
        PlatformNotSupportedException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static string HResultName(int hr) => (uint)hr switch
    {
        0x8001010E => "RPC_E_WRONG_THREAD - COM pointer used from the wrong apartment",
        0x80010106 => "RPC_E_CHANGED_MODE",
        0x80041001 => "WBEM_E_FAILED",
        0x80041002 => "WBEM_E_NOT_FOUND",
        0x80041003 => "WBEM_E_ACCESS_DENIED",
        0x80041008 => "WBEM_E_INVALID_PARAMETER",
        0x80070005 => "E_ACCESSDENIED",
        0x80004001 => "E_NOTIMPL",
        0x80004003 => "E_POINTER",
        0x80070057 => "E_INVALIDARG",
        _ => $"0x{(uint)hr:X8}",
    };
}
