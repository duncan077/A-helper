// SPDX-License-Identifier: GPL-3.0-or-later
//
// Run at Windows startup.
//
// Uses a scheduled task rather than the Run registry key. The app manifest
// requests requireAdministrator, and a Run-key entry for an elevated program
// produces a UAC prompt on every single logon - Windows will not silently
// elevate it. A scheduled task created with "run with highest privileges"
// starts elevated without prompting, which is the only way to make this usable.
//
// Driven through schtasks.exe rather than the Task Scheduler COM API: the COM
// route is a large amount of interop for one task, and schtasks ships with
// every Windows install.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace AcerHelper.App;

[SupportedOSPlatform("windows")]
public static class StartupManager
{
    private const string TaskName = "AcerHelper";

    /// <summary>Passed to the app by the scheduled task so it starts hidden.</summary>
    public const string MinimisedArgument = "--minimised";

    private static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "AcerHelper.App.exe");

    /// <summary>Runs schtasks silently and reports whether it succeeded.</summary>
    private static bool Run(string arguments, out string output)
    {
        output = string.Empty;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            // Bounded: a hung schtasks must not wedge the UI thread.
            if (!process.WaitForExit(10_000)) return false;

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Diagnostics.WriteException("schtasks", ex);
            return false;
        }
    }

    /// <summary>Whether the startup task exists.</summary>
    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"", out _);

    /// <summary>
    /// Creates or replaces the startup task. Returns null on success, or a
    /// description of the failure.
    /// </summary>
    public static string? Enable()
    {
        var exe = ExecutablePath;
        if (!File.Exists(exe)) return $"Executable not found: {exe}";

        // schtasks takes the whole command as one /TR argument, so the inner
        // quotes around the path have to be escaped.
        var command = $"\\\"{exe}\\\" {MinimisedArgument}";

        var user = WindowsIdentity.GetCurrent().Name;

        // /RL HIGHEST is what avoids the UAC prompt; /RU scopes the task to this
        // user rather than creating it for everyone; /F replaces an existing one
        // so a moved executable can be re-registered.
        var arguments = $"/Create /TN \"{TaskName}\" /TR \"{command}\" " +
                        $"/SC ONLOGON /RU \"{user}\" /RL HIGHEST /F";

        if (Run(arguments, out var output))
        {
            Diagnostics.Write($"startup task created for {exe}");
            return null;
        }

        var reason = output.Trim();
        Diagnostics.Write($"startup task creation failed: {reason}");
        return string.IsNullOrEmpty(reason) ? "schtasks failed." : reason;
    }

    /// <summary>Removes the startup task. Returns null on success.</summary>
    public static string? Disable()
    {
        // Already absent is success, not an error.
        if (!IsEnabled()) return null;

        if (Run($"/Delete /TN \"{TaskName}\" /F", out var output))
        {
            Diagnostics.Write("startup task removed");
            return null;
        }

        var reason = output.Trim();
        return string.IsNullOrEmpty(reason) ? "schtasks failed." : reason;
    }
}
