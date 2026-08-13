// SPDX-License-Identifier: GPL-3.0-or-later
//
// WMI event reception without implementing a COM callback.
//
// The obvious route - ExecNotificationQueryAsync with an IWbemObjectSink - means
// *implementing* a COM interface so WMI can call back into managed code. That is
// possible under Native AOT via [UnmanagedCallersOnly] vtables, but it is a lot
// of surface for one event class.
//
// The semi-synchronous form avoids all of it: ExecNotificationQuery returns an
// enumerator, and Next() blocks until an event arrives or the timeout expires.
// Nothing calls into us, so there is no sink to build.
//
// The cost is a blocked thread, which is why AcerEventWatcher gives this its own
// connection and its own thread rather than sharing the dispatcher - blocking
// there would stall sensor polling.

using System.Runtime.Versioning;

namespace AcerHelper.Hardware.Interop;

[SupportedOSPlatform("windows")]
internal sealed unsafe class WbemNotificationChannel : IDisposable
{
    /// <summary>Returned by <see cref="WaitForEvent"/> when the timeout expires.</summary>
    public const int TimedOut = unchecked((int)0x80043001);   // WBEM_S_TIMEDOUT

    private void* _services;
    private void* _enumerator;
    private bool _disposed;

    private WbemNotificationChannel(void* services, void* enumerator)
    {
        _services = services;
        _enumerator = enumerator;
    }

    /// <summary>
    /// Starts a notification query. <paramref name="query"/> is WQL, e.g.
    /// <c>SELECT * FROM APGeEvent</c>.
    /// </summary>
    public static WbemNotificationChannel Open(string wmiNamespace, string query)
    {
        void* services = null;
        void* enumerator = null;

        try
        {
            services = WbemMethodChannel.ConnectServices(wmiNamespace);

            var lang = WbemMethodChannel.Bstr("WQL");
            var wql = WbemMethodChannel.Bstr(query);
            try
            {
                // IWbemServices::ExecNotificationQuery is vtable slot 22.
                var exec = (delegate* unmanaged<void*, void*, void*, int, void*, void**, int>)
                    WbemMethodChannel.Vtbl(services)[22];

                // RETURN_IMMEDIATELY | FORWARD_ONLY - required for semi-sync delivery.
                WbemMethodChannel.Check(
                    exec(services, lang, wql, 0x30, null, &enumerator),
                    $"ExecNotificationQuery({query})");
            }
            finally
            {
                Ole.SysFreeString(lang);
                Ole.SysFreeString(wql);
            }

            // The enumerator is a separate proxy and needs its own blanket.
            WbemMethodChannel.Check(
                Ole.CoSetProxyBlanket(enumerator, Ole.RPC_C_AUTHN_WINNT, Ole.RPC_C_AUTHZ_NONE,
                    null, Ole.RPC_C_AUTHN_LEVEL_CALL, Ole.RPC_C_IMP_LEVEL_IMPERSONATE,
                    null, Ole.EOAC_NONE),
                "CoSetProxyBlanket(enumerator)");

            var channel = new WbemNotificationChannel(services, enumerator);
            services = null; enumerator = null;   // ownership transferred
            return channel;
        }
        finally
        {
            WbemMethodChannel.Release(enumerator);
            WbemMethodChannel.Release(services);
        }
    }

    /// <summary>
    /// Blocks for up to <paramref name="timeoutMs"/> waiting for one event.
    /// Returns null on timeout, which is normal and not an error.
    /// </summary>
    public Dictionary<string, object?>? WaitForEvent(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        void* obj = null;
        uint returned = 0;

        // IEnumWbemClassObject::Next is vtable slot 4.
        var next = (delegate* unmanaged<void*, int, uint, void**, uint*, int>)
            WbemMethodChannel.Vtbl(_enumerator)[4];

        var hr = next(_enumerator, timeoutMs, 1, &obj, &returned);

        if (hr == TimedOut || returned == 0) return null;
        if (hr < 0) throw new ComCallException("IEnumWbemClassObject::Next", hr);

        try { return WbemMethodChannel.ReadAllProperties(obj); }
        finally { WbemMethodChannel.Release(obj); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        WbemMethodChannel.Release(_enumerator); _enumerator = null;
        WbemMethodChannel.Release(_services); _services = null;
    }
}
