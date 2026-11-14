using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using MeaMod.DNS.Multicast;
using Sefirah.Data.EventArguments;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Mac;

/// <summary>
/// mDNS through the system Bonjour stack (dns_sd in libSystem) instead of a second responder on UDP 5353.
/// A MeaMod MulticastService started at login, before Wi-Fi is ready, never recovers: the Mac is invisible
/// to peers and never sees them. mDNSResponder follows interface, sleep and wake changes itself.
/// Every DNSServiceRef is created, used and deallocated on one serial dispatch queue.
/// </summary>
public sealed class MacBonjourService : IMdnsService, IDisposable
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string ServiceType = "_sefirah._udp";
    private const string DeviceNameProperty = "deviceName";
    private const string ServerPortProperty = "serverPort";
    private const uint FlagsMoreComing = 0x1;
    private const uint FlagsAdd = 0x2;
    private const uint ProtocolIPv4 = 0x1;
    private const byte AfInet = 2;
    private static readonly TimeSpan BrowseRefresh = TimeSpan.FromMinutes(2);

    private readonly ILogger logger;
    private readonly IntPtr queue;
    // Native callbacks must stay alive while any DNSServiceRef can still invoke them.
    private readonly RegisterReply registerReply;
    private readonly BrowseReply browseReply;
    private readonly ResolveReply resolveReply;
    private readonly AddrInfoReply addrInfoReply;
    private static readonly DispatchFunction dispatchWork = RunDispatched;
    private static readonly IntPtr dispatchWorkPointer = Marshal.GetFunctionPointerForDelegate(dispatchWork);

    // Queue-confined state.
    private readonly Dictionary<IntPtr, PendingResolve> pending = [];
    private IntPtr registration;
    private IntPtr browse;
    private string? localInstance;
    private Timer? refreshTimer;
    private Timer? cleanupTimer;

    public event EventHandler<DiscoveredMdnsServiceArgs>? DiscoveredMdnsService;

    public MacBonjourService(ILogger logger)
    {
        this.logger = logger;
        registerReply = OnRegisterReply;
        browseReply = OnBrowseReply;
        resolveReply = OnResolveReply;
        addrInfoReply = OnAddrInfoReply;
        queue = dispatch_queue_create("com.castle.sefirah.bonjour", IntPtr.Zero);
    }

    private sealed record PendingResolve(string Instance, string? DeviceName, int Port, uint InterfaceIndex, long ExpiresAt);

    public void AdvertiseService(UdpBroadcast broadcast, int port) => Post(() =>
    {
        Deallocate(ref registration);
        localInstance = broadcast.DeviceId;
        var txt = BuildTxt((DeviceNameProperty, broadcast.DeviceName), (ServerPortProperty, broadcast.Port.ToString()));
        var error = DNSServiceRegister(out var sdRef, 0, 0, broadcast.DeviceId, ServiceType, null, null,
            (ushort)IPAddress.HostToNetworkOrder((short)broadcast.Port), (ushort)txt.Length, txt, registerReply, IntPtr.Zero);
        if (error != 0 || !Attach(sdRef))
        {
            logger.Error($"Bonjour could not advertise {broadcast.DeviceId}: {error}");
            return;
        }
        registration = sdRef;
        logger.Info($"Advertising service for {broadcast.DeviceId} via Bonjour");
    });

    public void UnAdvertiseService() => Post(() =>
    {
        refreshTimer?.Dispose();
        refreshTimer = null;
        cleanupTimer?.Dispose();
        cleanupTimer = null;
        Deallocate(ref browse);
        Deallocate(ref registration);
        foreach (var sdRef in pending.Keys) DNSServiceRefDeallocate(sdRef);
        pending.Clear();
    });

    public void StartDiscovery() => Post(() =>
    {
        if (!StartBrowse()) return;
        // Re-browsing replays cached instances, so a peer whose service restarted without a goodbye
        // (killed process, reboot) is offered to the session manager again.
        cleanupTimer ??= new Timer(_ => Post(ExpirePending), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        refreshTimer ??= new Timer(_ => Post(RefreshBrowse), null, BrowseRefresh, BrowseRefresh);
        logger.Info("Started mDNS discovery service via Bonjour");
    });

    public void Dispose() => UnAdvertiseService();

    private void ExpirePending()
    {
        foreach (var (reference, resolve) in pending.ToArray())
        {
            if (resolve.ExpiresAt > Environment.TickCount64) continue;
            pending.Remove(reference);
            DNSServiceRefDeallocate(reference);
        }
    }

    private void RefreshBrowse()
    {
        if (browse == IntPtr.Zero) return;
        Deallocate(ref browse);
        StartBrowse();
    }

    private bool StartBrowse()
    {
        if (browse != IntPtr.Zero) return true;
        var error = DNSServiceBrowse(out var sdRef, 0, 0, ServiceType, null, browseReply, IntPtr.Zero);
        if (error != 0 || !Attach(sdRef))
        {
            logger.Error($"Bonjour browse failed: {error}");
            return false;
        }
        browse = sdRef;
        return true;
    }

    private bool Attach(IntPtr sdRef)
    {
        if (sdRef == IntPtr.Zero) return false;
        var error = DNSServiceSetDispatchQueue(sdRef, queue);
        if (error == 0) return true;
        DNSServiceRefDeallocate(sdRef);
        logger.Error($"Bonjour could not attach to its dispatch queue: {error}");
        return false;
    }

    private static void Deallocate(ref IntPtr sdRef)
    {
        if (sdRef == IntPtr.Zero) return;
        DNSServiceRefDeallocate(sdRef);
        sdRef = IntPtr.Zero;
    }

    private void Post(Action action)
    {
        var handle = GCHandle.Alloc((Action)(() =>
        {
            try { action(); }
            catch (Exception ex) { logger.Warn($"Bonjour operation failed: {ex.Message}"); }
        }));
        dispatch_async_f(queue, GCHandle.ToIntPtr(handle), dispatchWorkPointer);
    }

    private static void RunDispatched(IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        try { ((Action)handle.Target!)(); }
        finally { handle.Free(); }
    }

    private void OnBrowseReply(IntPtr sdRef, uint flags, uint interfaceIndex, int error, IntPtr serviceName,
        IntPtr regType, IntPtr domain, IntPtr context)
    {
        try
        {
            if (error != 0 || sdRef != browse) return;
            var instance = Marshal.PtrToStringUTF8(serviceName);
            if (string.IsNullOrEmpty(instance) || instance == localInstance) return;

            if ((flags & FlagsAdd) == 0)
            {
                foreach (var (reference, value) in pending.ToArray())
                    if (value.Instance == instance && value.InterfaceIndex == interfaceIndex)
                    { pending.Remove(reference); DNSServiceRefDeallocate(reference); }
                return;
            }
            if (pending.Values.Any(value => value.Instance == instance && value.InterfaceIndex == interfaceIndex)) return;
            var resolveError = DNSServiceResolve(out var resolveRef, 0, interfaceIndex, instance,
                Marshal.PtrToStringUTF8(regType) ?? ServiceType, Marshal.PtrToStringUTF8(domain) ?? "local.",
                resolveReply, IntPtr.Zero);
            if (resolveError != 0 || !Attach(resolveRef)) return;
            pending[resolveRef] = new PendingResolve(instance, null, 0, interfaceIndex, Environment.TickCount64 + 10_000);
        }
        catch (Exception ex)
        {
            logger.Warn($"Bonjour browse reply failed: {ex.Message}");
        }
    }

    private void OnResolveReply(IntPtr sdRef, uint flags, uint interfaceIndex, int error, IntPtr fullName,
        IntPtr hostTarget, ushort port, ushort txtLength, IntPtr txtRecord, IntPtr context)
    {
        try
        {
            if (!pending.Remove(sdRef, out var resolve)) return;
            DNSServiceRefDeallocate(sdRef);
            if (error != 0) return;

            var txt = ParseTxt(txtRecord, txtLength);
            var deviceName = txt.GetValueOrDefault(DeviceNameProperty);
            var host = Marshal.PtrToStringUTF8(hostTarget);
            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(host)) return;
            var serverPort = int.TryParse(txt.GetValueOrDefault(ServerPortProperty), out var parsed)
                ? parsed
                : (ushort)IPAddress.NetworkToHostOrder((short)port);

            if (serverPort is < 1 or > 65535) return;
            var addrError = DNSServiceGetAddrInfo(out var addrRef, 0, interfaceIndex, ProtocolIPv4, host,
                addrInfoReply, IntPtr.Zero);
            if (addrError != 0 || !Attach(addrRef)) return;
            pending[addrRef] = resolve with { DeviceName = deviceName, Port = serverPort, ExpiresAt = Environment.TickCount64 + 10_000 };
        }
        catch (Exception ex)
        {
            logger.Warn($"Bonjour resolve reply failed: {ex.Message}");
        }
    }

    private void OnAddrInfoReply(IntPtr sdRef, uint flags, uint interfaceIndex, int error, IntPtr hostName,
        IntPtr address, uint ttl, IntPtr context)
    {
        try
        {
            if (!pending.TryGetValue(sdRef, out var resolve)) return;
            if (error == 0 && (flags & FlagsAdd) != 0 && TryReadIPv4(address, out var ip))
            {
                DiscoveredMdnsService?.Invoke(this,
                    new DiscoveredMdnsServiceArgs(resolve.Instance, resolve.DeviceName!, ip, resolve.Port));
            }
            // GetAddrInfo keeps running for changes; one batch of answers is enough here.
            if ((flags & FlagsMoreComing) != 0) return;
            pending.Remove(sdRef);
            DNSServiceRefDeallocate(sdRef);
        }
        catch (Exception ex)
        {
            logger.Warn($"Bonjour address reply failed: {ex.Message}");
        }
    }

    private void OnRegisterReply(IntPtr sdRef, uint flags, int error, IntPtr name, IntPtr regType,
        IntPtr domain, IntPtr context)
    {
        if (error != 0) logger.Warn($"Bonjour registration reported error {error}");
    }

    /// <summary>Reads a struct sockaddr_in (BSD layout: len, family, port, address).</summary>
    private static bool TryReadIPv4(IntPtr sockaddr, out string address)
    {
        address = string.Empty;
        if (sockaddr == IntPtr.Zero || Marshal.ReadByte(sockaddr, 1) != AfInet) return false;
        var bytes = new byte[4];
        Marshal.Copy(sockaddr + 4, bytes, 0, 4);
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        address = new IPAddress(bytes).ToString();
        return true;
    }

    internal static byte[] BuildTxt(params (string Key, string Value)[] entries)
    {
        using var stream = new MemoryStream();
        foreach (var (key, value) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes($"{key}={value}");
            if (bytes.Length > 255) continue;
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static Dictionary<string, string> ParseTxt(IntPtr txtRecord, int length)
    {
        var bytes = new byte[length];
        if (length > 0 && txtRecord != IntPtr.Zero) Marshal.Copy(txtRecord, bytes, 0, length);
        return ParseTxt(bytes);
    }

    internal static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < bytes.Length)
        {
            int size = bytes[offset++];
            if (offset + size > bytes.Length) break;
            var entry = Encoding.UTF8.GetString(bytes.Slice(offset, size));
            offset += size;
            var separator = entry.IndexOf('=');
            if (separator > 0) result.TryAdd(entry[..separator], entry[(separator + 1)..]);
        }
        return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DispatchFunction(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RegisterReply(IntPtr sdRef, uint flags, int error, IntPtr name, IntPtr regType,
        IntPtr domain, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BrowseReply(IntPtr sdRef, uint flags, uint interfaceIndex, int error, IntPtr serviceName,
        IntPtr regType, IntPtr domain, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResolveReply(IntPtr sdRef, uint flags, uint interfaceIndex, int error, IntPtr fullName,
        IntPtr hostTarget, ushort port, ushort txtLength, IntPtr txtRecord, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AddrInfoReply(IntPtr sdRef, uint flags, uint interfaceIndex, int error, IntPtr hostName,
        IntPtr address, uint ttl, IntPtr context);

    [DllImport(LibSystem)]
    private static extern int DNSServiceRegister(out IntPtr sdRef, uint flags, uint interfaceIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string regType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string? host,
        ushort port, ushort txtLength, byte[] txtRecord, RegisterReply callback, IntPtr context);

    [DllImport(LibSystem)]
    private static extern int DNSServiceBrowse(out IntPtr sdRef, uint flags, uint interfaceIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string regType, [MarshalAs(UnmanagedType.LPUTF8Str)] string? domain,
        BrowseReply callback, IntPtr context);

    [DllImport(LibSystem)]
    private static extern int DNSServiceResolve(out IntPtr sdRef, uint flags, uint interfaceIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string regType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string domain, ResolveReply callback, IntPtr context);

    [DllImport(LibSystem)]
    private static extern int DNSServiceGetAddrInfo(out IntPtr sdRef, uint flags, uint interfaceIndex,
        uint protocol, [MarshalAs(UnmanagedType.LPUTF8Str)] string hostName, AddrInfoReply callback, IntPtr context);

    [DllImport(LibSystem)]
    private static extern int DNSServiceSetDispatchQueue(IntPtr sdRef, IntPtr queue);

    [DllImport(LibSystem)]
    private static extern void DNSServiceRefDeallocate(IntPtr sdRef);

    [DllImport(LibSystem)]
    private static extern IntPtr dispatch_queue_create([MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr attr);

    [DllImport(LibSystem)]
    private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);
}
