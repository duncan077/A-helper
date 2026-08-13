// SPDX-License-Identifier: GPL-3.0-or-later
//
// Low-level keyboard hook, for keys that never reach WMI.
//
// The Nitro key on ANV15-41 is not an APGeEvent at all - it emits an ordinary
// HID scancode (it types "@" in a console). No amount of watching APGeEvent will
// see it, which is why --learn-nitro captured nothing.
//
// WH_KEYBOARD_LL requires a message loop on the thread that installed the hook,
// so this owns a dedicated thread and pumps messages there.
//
// The callback is [UnmanagedCallersOnly], so it is a plain function pointer with
// no delegate marshalling - which is what makes it work under Native AOT.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcerHelper.Hardware.Input;

public readonly record struct KeyStroke(uint VirtualKey, uint ScanCode, uint Flags)
{
    public bool IsExtended => (Flags & 0x01) != 0;

    public override string ToString()
        => $"vk=0x{VirtualKey:X2} scan=0x{ScanCode:X2}{(IsExtended ? " extended" : "")}";
}

[SupportedOSPlatform("windows")]
public static unsafe partial class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookEx(int idHook, delegate* unmanaged<int, nuint, nint, nint> fn,
                                                 nint hmod, uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int code, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    private static partial int PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    private static nint _hook;
    private static uint _threadId;
    private static Thread? _thread;
    private static readonly Lock Gate = new();

    /// <summary>
    /// Raised for every key-down, on the hook thread. Handlers must be fast:
    /// Windows removes a hook that blocks too long.
    /// </summary>
    public static event Action<KeyStroke>? KeyPressed;

    [UnmanagedCallersOnly]
    private static nint Callback(int code, nuint wParam, nint lParam)
    {
        if (code == HC_ACTION && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            try
            {
                var data = *(KbdLlHookStruct*)lParam;
                KeyPressed?.Invoke(new KeyStroke(data.vkCode, data.scanCode, data.flags));
            }
            catch { /* never let a handler fault escape into the hook chain */ }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    public static bool IsRunning { get { lock (Gate) return _hook != 0; } }

    /// <summary>Installs the hook on a dedicated message-pumping thread.</summary>
    public static bool Start()
    {
        lock (Gate)
        {
            if (_hook != 0) return true;

            var ready = new ManualResetEventSlim(false);
            var installed = false;

            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, &Callback, 0, 0);
                installed = _hook != 0;
                ready.Set();

                if (!installed) return;

                // WH_KEYBOARD_LL delivers callbacks through this thread's queue,
                // so it must keep pumping for the hook to fire at all.
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                    if (msg.message == WM_QUIT) break;

                UnhookWindowsHookEx(_hook);
                _hook = 0;
            })
            {
                IsBackground = true,
                Name = "AcerKeyboardHook",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            ready.Wait(TimeSpan.FromSeconds(5));
            return installed;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_hook == 0 || _threadId == 0) return;

            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(2));

            _thread = null;
            _threadId = 0;
            _hook = 0;
        }
    }
}
