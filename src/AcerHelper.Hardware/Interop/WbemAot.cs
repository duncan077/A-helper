// SPDX-License-Identifier: GPL-3.0-or-later
//
// Native-AOT-safe WMI method invocation.
//
// System.Management cannot be used under PublishAot: it instantiates types by
// reflection (WbemDefPath and friends), which the trimmer removes, producing
// MissingMethodException at runtime. IL2104/IL3053 warn about this at publish.
//
// This file talks to WMI through raw IWbem* COM vtables using function
// pointers and [LibraryImport]. No reflection, no built-in COM marshalling,
// so nothing here depends on runtime code generation.
//
// Only the slice needed to call ACPI-WMI methods is implemented: connect,
// locate an instance, spawn in-parameters, execute, read one out-parameter.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace AcerHelper.Hardware.Interop;

[SupportedOSPlatform("windows")]
internal static unsafe partial class Ole
{
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(void* reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeSecurity(
        void* sd, int cAuthSvc, void* asAuthSvc, void* reserved1,
        uint authnLevel, uint impLevel, void* authList, uint capabilities, void* reserved3);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        Guid* rclsid, void* pUnkOuter, uint dwClsContext, Guid* riid, void** ppv);

    [LibraryImport("ole32.dll")]
    internal static partial int CoSetProxyBlanket(
        void* pProxy, uint dwAuthnSvc, uint dwAuthzSvc, void* pServerPrincName,
        uint dwAuthnLevel, uint dwImpLevel, void* pAuthInfo, uint dwCapabilities);

    [LibraryImport("oleaut32.dll")]
    internal static partial void* SysAllocString(char* psz);

    [LibraryImport("oleaut32.dll")]
    internal static partial void SysFreeString(void* bstr);

    [LibraryImport("oleaut32.dll")]
    internal static partial int VariantClear(Variant* pvarg);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetLBound(void* psa, uint nDim, int* plLbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetUBound(void* psa, uint nDim, int* plUbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayAccessData(void* psa, void** ppvData);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayUnaccessData(void* psa);

    [LibraryImport("oleaut32.dll")]
    internal static partial void* SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayDestroy(void* psa);

    internal const uint COINIT_MULTITHREADED = 0;
    internal const uint CLSCTX_INPROC_SERVER = 1;
    internal const uint RPC_C_AUTHN_LEVEL_DEFAULT = 0;
    internal const uint RPC_C_AUTHN_LEVEL_CALL = 3;
    internal const uint RPC_C_IMP_LEVEL_IMPERSONATE = 3;
    internal const uint RPC_C_AUTHN_WINNT = 10;
    internal const uint RPC_C_AUTHZ_NONE = 0;
    internal const uint EOAC_NONE = 0;
}

/// <summary>OLE VARIANT. 24 bytes on x64; payload starts at offset 8.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public int lVal;
    [FieldOffset(8)] public long llVal;
    [FieldOffset(8)] public nint ptr;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_NULL = 1;
    public const ushort VT_I2 = 2;
    public const ushort VT_I4 = 3;
    public const ushort VT_BSTR = 8;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI1 = 17;
    public const ushort VT_UI2 = 18;
    public const ushort VT_UI4 = 19;
    public const ushort VT_I8 = 20;
    public const ushort VT_UI8 = 21;
    public const ushort VT_ARRAY = 0x2000;
}

/// <summary>CIM type codes as reported by IWbemClassObject::Get.</summary>
internal static class CimType
{
    public const int Uint8 = 17;
    public const int Uint16 = 18;
    public const int Uint32 = 19;
    public const int Uint64 = 21;
    public const int Sint32 = 3;
    public const int String = 8;
    public const int FlagArray = 0x2000;
}

/// <summary>
/// Thrown when a COM call returns a failing HRESULT. Public so callers can
/// report the HRESULT rather than a bare "operation failed".
/// </summary>
public sealed class ComCallException(string what, int hr)
    : Exception($"{what} failed with HRESULT 0x{hr:X8}.")
{
    public int HResult32 { get; } = hr;
}

/// <summary>
/// A connection to one WMI class, able to invoke its methods.
/// Not thread-safe; guard externally or open one channel per thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WbemMethodChannel : IDisposable
{
    // {4590F811-1D3A-11D0-891F-00AA004B2E24}
    private static Guid CLSID_WbemLocator = new(0x4590f811, 0x1d3a, 0x11d0,
        0x89, 0x1f, 0x00, 0xaa, 0x00, 0x4b, 0x2e, 0x24);

    // {DC12A687-737F-11CF-884D-00AA004B2E24}
    private static Guid IID_IWbemLocator = new(0xdc12a687, 0x737f, 0x11cf,
        0x88, 0x4d, 0x00, 0xaa, 0x00, 0x4b, 0x2e, 0x24);

    private void* _services;      // IWbemServices*
    private void* _classObject;   // IWbemClassObject* - the class definition
    private void* _instancePath;  // BSTR of the instance __PATH
    private bool _disposed;

    private static int _comInitialised;

    private WbemMethodChannel(void* services, void* classObject, void* instancePath)
    {
        _services = services;
        _classObject = classObject;
        _instancePath = instancePath;
    }

    // -------------------------------------------------------- vtable helpers
    // internal rather than private: WbemNotificationChannel reuses these and
    // ConnectServices below, so the proxy-blanket setup exists in one place.

    internal static void** Vtbl(void* obj) => *(void***)obj;

    internal static void Release(void* obj)
    {
        if (obj is null) return;
        ((delegate* unmanaged<void*, uint>)Vtbl(obj)[2])(obj);
    }

    internal static void Check(int hr, string what)
    {
        if (hr < 0) throw new ComCallException(what, hr);
    }

    internal static void* Bstr(string s)
    {
        fixed (char* p = s) return Ole.SysAllocString(p);
    }

    /// <summary>
    /// Connects to a WMI namespace and applies the proxy blanket. The caller
    /// owns the returned IWbemServices* and must Release it.
    /// </summary>
    internal static void* ConnectServices(string wmiNamespace)
    {
        EnsureComInitialised();

        void* locator = null;
        void* services = null;

        try
        {
            fixed (Guid* clsid = &CLSID_WbemLocator)
            fixed (Guid* iid = &IID_IWbemLocator)
                Check(Ole.CoCreateInstance(clsid, null, Ole.CLSCTX_INPROC_SERVER, iid, &locator),
                      "CoCreateInstance(WbemLocator)");

            var ns = Bstr(wmiNamespace);
            try
            {
                var connect = (delegate* unmanaged<void*, void*, void*, void*, void*, int, void*, void*, void**, int>)
                    Vtbl(locator)[3];
                Check(connect(locator, ns, null, null, null, 0, null, null, &services),
                      $"ConnectServer({wmiNamespace})");
            }
            finally { Ole.SysFreeString(ns); }

            // WMI proxies require an explicit blanket or calls fail with E_ACCESSDENIED.
            Check(Ole.CoSetProxyBlanket(services, Ole.RPC_C_AUTHN_WINNT, Ole.RPC_C_AUTHZ_NONE,
                    null, Ole.RPC_C_AUTHN_LEVEL_CALL, Ole.RPC_C_IMP_LEVEL_IMPERSONATE,
                    null, Ole.EOAC_NONE),
                  "CoSetProxyBlanket");

            var result = services;
            services = null;   // ownership transferred to caller
            return result;
        }
        finally
        {
            Release(locator);
            Release(services);
        }
    }

    // ------------------------------------------------------------- lifecycle

    public static WbemMethodChannel Open(string wmiNamespace, string className)
    {
        void* services = null;
        void* classObj = null;
        void* path = null;

        try
        {
            services = ConnectServices(wmiNamespace);
            classObj = GetClassObject(services, className);

            path = FindFirstInstancePath(services, className);
            if (path is null)
                throw new PlatformNotSupportedException(
                    $"{className} exists but has no instance in {wmiNamespace}.");

            var channel = new WbemMethodChannel(services, classObj, path);
            services = null; classObj = null; path = null;   // ownership transferred
            return channel;
        }
        finally
        {
            Release(classObj);
            Release(services);
            if (path is not null) Ole.SysFreeString(path);
        }
    }

    internal static void EnsureComInitialised()
    {
        if (Interlocked.CompareExchange(ref _comInitialised, 1, 0) != 0) return;

        var hr = Ole.CoInitializeEx(null, Ole.COINIT_MULTITHREADED);
        if (hr < 0 && hr != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE is benign
            throw new ComCallException("CoInitializeEx", hr);

        // Best-effort: fails with RPC_E_TOO_LATE if the host already set it.
        Ole.CoInitializeSecurity(null, -1, null, null,
            Ole.RPC_C_AUTHN_LEVEL_DEFAULT, Ole.RPC_C_IMP_LEVEL_IMPERSONATE,
            null, Ole.EOAC_NONE, null);
    }

    private static void* GetClassObject(void* services, string className)
    {
        void* obj = null;
        var path = Bstr(className);
        try
        {
            // IWbemServices::GetObject is slot 6.
            var getObject = (delegate* unmanaged<void*, void*, int, void*, void**, void**, int>)
                Vtbl(services)[6];
            var hr = getObject(services, path, 0, null, &obj, null);
            if (hr < 0)
                throw new PlatformNotSupportedException(
                    $"WMI class '{className}' not found (HRESULT 0x{hr:X8}). " +
                    "This machine does not expose the Acer gaming interface.");
        }
        finally { Ole.SysFreeString(path); }
        return obj;
    }

    private static void* FindFirstInstancePath(void* services, string className)
    {
        void* enumerator = null;
        var lang = Bstr("WQL");
        var query = Bstr($"SELECT __PATH FROM {className}");
        try
        {
            // IWbemServices::ExecQuery is slot 20.
            var execQuery = (delegate* unmanaged<void*, void*, void*, int, void*, void**, int>)
                Vtbl(services)[20];
            Check(execQuery(services, lang, query, 0x30 /* FORWARD_ONLY | RETURN_IMMEDIATELY */,
                            null, &enumerator), $"ExecQuery({className})");

            void* obj = null;
            uint returned = 0;

            // IEnumWbemClassObject::Next is slot 4.
            var next = (delegate* unmanaged<void*, int, uint, void**, uint*, int>)Vtbl(enumerator)[4];
            var hr = next(enumerator, -1 /* WBEM_INFINITE */, 1, &obj, &returned);
            if (hr < 0 || returned == 0) return null;

            try { return ReadBstrProperty(obj, "__PATH"); }
            finally { Release(obj); }
        }
        finally
        {
            Release(enumerator);
            Ole.SysFreeString(lang);
            Ole.SysFreeString(query);
        }
    }

    private static void* ReadBstrProperty(void* obj, string name)
    {
        var v = default(Variant);
        fixed (char* n = name)
        {
            // IWbemClassObject::Get is slot 4.
            var get = (delegate* unmanaged<void*, char*, int, Variant*, int*, int*, int>)Vtbl(obj)[4];
            Check(get(obj, n, 0, &v, null, null), $"Get({name})");
        }

        if (v.vt != Variant.VT_BSTR || v.ptr == 0) { Ole.VariantClear(&v); return null; }

        // Copy out before clearing the source variant.
        var copy = Ole.SysAllocString((char*)v.ptr);
        Ole.VariantClear(&v);
        return copy;
    }

    // -------------------------------------------------------------- invoking

    /// <summary>
    /// Executes a method taking at most one input and returning one output.
    /// Input width is chosen from the CIM type the BIOS declares, which is why
    /// this works where a fixed-width guess would throw.
    /// </summary>
    public ulong Invoke(string methodName, ulong? input, string outParameterName = "gmOutput")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        void* inSignature = null;
        void* inInstance = null;
        void* outParams = null;
        var methodBstr = Bstr(methodName);

        try
        {
            fixed (char* m = methodName)
            {
                // IWbemClassObject::GetMethod is slot 19.
                var getMethod = (delegate* unmanaged<void*, char*, int, void**, void**, int>)
                    Vtbl(_classObject)[19];
                Check(getMethod(_classObject, m, 0, &inSignature, null), $"GetMethod({methodName})");
            }

            if (inSignature is not null && input.HasValue)
            {
                // IWbemClassObject::SpawnInstance is slot 15.
                var spawn = (delegate* unmanaged<void*, int, void**, int>)Vtbl(inSignature)[15];
                Check(spawn(inSignature, 0, &inInstance), "SpawnInstance");

                var (paramName, cimType) = FirstInputParameter(inInstance);
                if (paramName is not null)
                    PutScalar(inInstance, paramName, cimType, input.Value);
            }

            // IWbemServices::ExecMethod is slot 24.
            var exec = (delegate* unmanaged<void*, void*, void*, int, void*, void*, void**, void**, int>)
                Vtbl(_services)[24];
            Check(exec(_services, _instancePath, methodBstr, 0, null, inInstance, &outParams, null),
                  $"ExecMethod({methodName})");

            return outParams is null ? 0UL : ReadScalar(outParams, outParameterName);
        }
        finally
        {
            Release(outParams);
            Release(inInstance);
            Release(inSignature);
            Ole.SysFreeString(methodBstr);
        }
    }

    /// <summary>
    /// Executes a method with any number of named parameters.
    ///
    /// Required for BatteryControl: the Linux driver passes one packed struct,
    /// but the Windows ACPI-WMI mapper decomposes that struct into separate
    /// named fields (uBatteryNo, uFunctionQuery, uReserved, ...). Passing a
    /// packed buffer to the first parameter fails with "invalid parameter".
    ///
    /// Values may be integers (any width) or byte[] for UInt8Array fields.
    /// Returns the requested outputs as ulong or byte[].
    /// </summary>
    public Dictionary<string, object?> InvokeNamed(
        string methodName,
        IReadOnlyDictionary<string, object>? inputs,
        IReadOnlyList<string> outputNames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        void* inSignature = null;
        void* inInstance = null;
        void* outParams = null;
        var methodBstr = Bstr(methodName);

        try
        {
            fixed (char* m = methodName)
            {
                var getMethod = (delegate* unmanaged<void*, char*, int, void**, void**, int>)
                    Vtbl(_classObject)[19];
                Check(getMethod(_classObject, m, 0, &inSignature, null), $"GetMethod({methodName})");
            }

            if (inSignature is not null && inputs is { Count: > 0 })
            {
                var spawn = (delegate* unmanaged<void*, int, void**, int>)Vtbl(inSignature)[15];
                Check(spawn(inSignature, 0, &inInstance), "SpawnInstance");

                foreach (var kv in inputs)
                    PutValue(inInstance, kv.Key, kv.Value);
            }

            var exec = (delegate* unmanaged<void*, void*, void*, int, void*, void*, void**, void**, int>)
                Vtbl(_services)[24];
            Check(exec(_services, _instancePath, methodBstr, 0, null, inInstance, &outParams, null),
                  $"ExecMethod({methodName})");

            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (outParams is not null)
                foreach (var name in outputNames)
                    result[name] = ReadValue(outParams, name);

            return result;
        }
        finally
        {
            Release(outParams);
            Release(inInstance);
            Release(inSignature);
            Ole.SysFreeString(methodBstr);
        }
    }

    /// <summary>Reads a property's declared CIM type, so the right VARIANT shape is used.</summary>
    private static int GetCimType(void* obj, string name)
    {
        var v = default(Variant);
        var cim = 0;
        fixed (char* n = name)
        {
            var get = (delegate* unmanaged<void*, char*, int, Variant*, int*, int*, int>)Vtbl(obj)[4];
            var hr = get(obj, n, 0, &v, &cim, null);
            Ole.VariantClear(&v);
            if (hr < 0) return 0;
        }
        return cim;
    }

    private static void PutValue(void* obj, string name, object value)
    {
        var cim = GetCimType(obj, name);
        var v = default(Variant);
        void* bstr = null;
        void* psa = null;

        try
        {
            if (value is byte[] bytes)
            {
                psa = Ole.SafeArrayCreateVector(Variant.VT_UI1, 0, (uint)bytes.Length);
                if (psa is null) throw new ComCallException($"SafeArrayCreateVector({name})", -1);

                void* data = null;
                Check(Ole.SafeArrayAccessData(psa, &data), $"SafeArrayAccessData({name})");
                for (var i = 0; i < bytes.Length; i++) ((byte*)data)[i] = bytes[i];
                Ole.SafeArrayUnaccessData(psa);

                v.vt = Variant.VT_ARRAY | Variant.VT_UI1;
                v.ptr = (nint)psa;
            }
            else
            {
                var scalar = Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                if (cim == CimType.Uint64)
                {
                    bstr = Bstr(scalar.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    v.vt = Variant.VT_BSTR;
                    v.ptr = (nint)bstr;
                }
                else
                {
                    v.vt = Variant.VT_I4;
                    v.lVal = unchecked((int)(uint)scalar);
                }
            }

            fixed (char* n = name)
            {
                var put = (delegate* unmanaged<void*, char*, int, Variant*, int, int>)Vtbl(obj)[5];
                Check(put(obj, n, 0, &v, cim), $"Put({name})");
            }
        }
        finally
        {
            if (bstr is not null) Ole.SysFreeString(bstr);
            if (psa is not null) Ole.SafeArrayDestroy(psa);
        }
    }

    /// <summary>
    /// Enumerates every non-system property of an object.
    ///
    /// Used for WMI event objects, whose property names are declared by the
    /// BIOS and are not documented anywhere - reading them generically is the
    /// only way to see what an event actually carries.
    /// </summary>
    internal static Dictionary<string, object?> ReadAllProperties(void* obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // BeginEnumeration=8, Next=9, EndEnumeration=10.
        var begin = (delegate* unmanaged<void*, int, int>)Vtbl(obj)[8];
        var next = (delegate* unmanaged<void*, int, void**, Variant*, int*, int*, int>)Vtbl(obj)[9];
        var end = (delegate* unmanaged<void*, int>)Vtbl(obj)[10];

        if (begin(obj, 0x40 /* WBEM_FLAG_NONSYSTEM_ONLY */) < 0) return result;

        try
        {
            while (true)
            {
                void* nameBstr = null;
                var v = default(Variant);

                if (next(obj, 0, &nameBstr, &v, null, null) != 0) break;
                if (nameBstr is null) break;

                var name = new string((char*)nameBstr);
                Ole.SysFreeString(nameBstr);

                result[name] = (v.vt & Variant.VT_ARRAY) != 0
                    ? ReadByteArray(v.ptr)
                    : VariantToObject(ref v);

                Ole.VariantClear(&v);
            }
        }
        finally { end(obj); }

        return result;
    }

    private static object? VariantToObject(ref Variant v) => v.vt switch
    {
        Variant.VT_I4 or Variant.VT_UI4 => (ulong)(uint)v.lVal,
        Variant.VT_I8 or Variant.VT_UI8 => (ulong)v.llVal,
        Variant.VT_I2 or Variant.VT_UI2 => (ulong)(ushort)v.lVal,
        Variant.VT_UI1 => (ulong)(byte)v.lVal,
        Variant.VT_BOOL => v.lVal == 0 ? 0UL : 1UL,
        Variant.VT_BSTR when v.ptr != 0 => new string((char*)v.ptr),
        _ => null,
    };

    /// <summary>Reads a property as byte[] (arrays) or ulong (scalars).</summary>
    private static object? ReadValue(void* obj, string name)
    {
        var v = default(Variant);
        fixed (char* n = name)
        {
            var get = (delegate* unmanaged<void*, char*, int, Variant*, int*, int*, int>)Vtbl(obj)[4];
            var hr = get(obj, n, 0, &v, null, null);
            if (hr < 0) { Ole.VariantClear(&v); return null; }
        }

        try
        {
            if ((v.vt & Variant.VT_ARRAY) != 0) return ReadByteArray(v.ptr);
            if (v.vt is Variant.VT_EMPTY or Variant.VT_NULL) return null;

            return v.vt switch
            {
                Variant.VT_I4 or Variant.VT_UI4 => (ulong)(uint)v.lVal,
                Variant.VT_I8 or Variant.VT_UI8 => (ulong)v.llVal,
                Variant.VT_I2 or Variant.VT_UI2 => (ulong)(ushort)v.lVal,
                Variant.VT_UI1 => (ulong)(byte)v.lVal,
                Variant.VT_BOOL => v.lVal == 0 ? 0UL : 1UL,
                Variant.VT_BSTR when v.ptr != 0 =>
                    ulong.TryParse(new string((char*)v.ptr),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                        ? parsed : (object?)null,
                _ => null,
            };
        }
        finally { Ole.VariantClear(&v); }
    }

    internal static byte[]? ReadByteArray(nint safeArray)
    {
        if (safeArray == 0) return null;

        var psa = (void*)safeArray;
        int lo = 0, hi = -1;
        if (Ole.SafeArrayGetLBound(psa, 1, &lo) < 0) return null;
        if (Ole.SafeArrayGetUBound(psa, 1, &hi) < 0) return null;

        var count = hi - lo + 1;
        if (count <= 0) return [];

        void* data = null;
        if (Ole.SafeArrayAccessData(psa, &data) < 0) return null;

        try
        {
            var result = new byte[count];
            for (var i = 0; i < count; i++) result[i] = ((byte*)data)[i];
            return result;
        }
        finally { Ole.SafeArrayUnaccessData(psa); }
    }

    /// <summary>Finds the single non-system input property and its CIM type.</summary>
    private static (string? Name, int CimType) FirstInputParameter(void* inInstance)
    {
        // BeginEnumeration=8, Next=9, EndEnumeration=10.
        var begin = (delegate* unmanaged<void*, int, int>)Vtbl(inInstance)[8];
        var next = (delegate* unmanaged<void*, int, void**, Variant*, int*, int*, int>)Vtbl(inInstance)[9];
        var end = (delegate* unmanaged<void*, int>)Vtbl(inInstance)[10];

        Check(begin(inInstance, 0x40 /* NONSYSTEM_ONLY */), "BeginEnumeration");
        try
        {
            void* nameBstr = null;
            var v = default(Variant);
            var cim = 0;

            var hr = next(inInstance, 0, &nameBstr, &v, &cim, null);
            Ole.VariantClear(&v);
            if (hr != 0 || nameBstr is null) return (null, 0);

            var name = new string((char*)nameBstr);
            Ole.SysFreeString(nameBstr);
            return (name, cim);
        }
        finally { end(inInstance); }
    }

    /// <summary>
    /// Writes a scalar in the representation WMI expects for that CIM type.
    /// CIM_UINT64 has no VARIANT equivalent, so WMI carries it as a BSTR -
    /// passing VT_I8 there is rejected.
    /// </summary>
    private static void PutScalar(void* obj, string name, int cimType, ulong value)
    {
        var v = default(Variant);
        void* bstr = null;

        try
        {
            if (cimType == CimType.Uint64)
            {
                bstr = Bstr(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                v.vt = Variant.VT_BSTR;
                v.ptr = (nint)bstr;
            }
            else
            {
                v.vt = Variant.VT_I4;
                v.lVal = unchecked((int)(uint)value);
            }

            fixed (char* n = name)
            {
                // IWbemClassObject::Put is slot 5.
                var put = (delegate* unmanaged<void*, char*, int, Variant*, int, int>)Vtbl(obj)[5];
                Check(put(obj, n, 0, &v, cimType), $"Put({name})");
            }
        }
        finally
        {
            if (bstr is not null) Ole.SysFreeString(bstr);
        }
    }

    /// <summary>Reads an out-parameter, normalising every WMI numeric shape to ulong.</summary>
    private static ulong ReadScalar(void* obj, string name)
    {
        var v = default(Variant);
        fixed (char* n = name)
        {
            var get = (delegate* unmanaged<void*, char*, int, Variant*, int*, int*, int>)Vtbl(obj)[4];
            var hr = get(obj, n, 0, &v, null, null);
            if (hr < 0) { Ole.VariantClear(&v); return 0; }
        }

        try
        {
            if ((v.vt & Variant.VT_ARRAY) != 0) return ReadByteArrayAsUInt64(v.ptr);

            return v.vt switch
            {
                Variant.VT_I4 or Variant.VT_UI4 => (uint)v.lVal,
                Variant.VT_I8 or Variant.VT_UI8 => (ulong)v.llVal,
                Variant.VT_I2 or Variant.VT_UI2 => (ushort)v.lVal,
                Variant.VT_UI1 => (byte)v.lVal,
                Variant.VT_BOOL => v.lVal == 0 ? 0UL : 1UL,
                // uint64 arrives as a decimal string.
                Variant.VT_BSTR when v.ptr != 0 =>
                    ulong.TryParse(new string((char*)v.ptr),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0UL,
                _ => 0UL,
            };
        }
        finally { Ole.VariantClear(&v); }
    }

    private static ulong ReadByteArrayAsUInt64(nint safeArray)
    {
        if (safeArray == 0) return 0;

        var psa = (void*)safeArray;
        int lo = 0, hi = -1;
        if (Ole.SafeArrayGetLBound(psa, 1, &lo) < 0) return 0;
        if (Ole.SafeArrayGetUBound(psa, 1, &hi) < 0) return 0;

        var count = hi - lo + 1;
        if (count <= 0) return 0;

        void* data = null;
        if (Ole.SafeArrayAccessData(psa, &data) < 0) return 0;

        try
        {
            ulong result = 0;
            var take = Math.Min(count, 8);
            for (var i = 0; i < take; i++)
                result |= (ulong)((byte*)data)[i] << (8 * i);
            return result;
        }
        finally { Ole.SafeArrayUnaccessData(psa); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release(_classObject); _classObject = null;
        Release(_services); _services = null;
        if (_instancePath is not null) { Ole.SysFreeString(_instancePath); _instancePath = null; }
    }
}
