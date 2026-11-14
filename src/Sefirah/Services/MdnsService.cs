using System.Net.Sockets;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using Sefirah.Data.Contracts;
using Sefirah.Data.EventArguments;
using Sefirah.Data.Models;

namespace Sefirah.Services;

public class MdnsService(ILogger logger) : IMdnsService
{
    private const string ServiceType = "_sefirah._udp";
    private const string DeviceNameProperty = "deviceName";
    private const string ServerPortProperty = "serverPort";

    private MulticastService? multicastService;
    private ServiceProfile? serviceProfile;
    private ServiceDiscovery? serviceDiscovery;

    public event EventHandler<DiscoveredMdnsServiceArgs>? DiscoveredMdnsService;
    private readonly object cacheLock = new();
    private readonly Dictionary<string, (ResourceRecord Record, DateTime Expires)> records = [];
    private Timer? queryTimer;

    /// <inheritdoc />
    public void AdvertiseService(UdpBroadcast broadcast, int port)
    {
        try
        {
            // Set up the service profile
            serviceProfile = new ServiceProfile(broadcast.DeviceId, ServiceType, (ushort)broadcast.Port);
            serviceProfile.AddProperty("deviceName", broadcast.DeviceName);
            serviceProfile.AddProperty("serverPort", broadcast.Port.ToString());

            // Advertise the service
            multicastService = new MulticastService();
            serviceDiscovery = new ServiceDiscovery(multicastService);
            serviceDiscovery.Advertise(serviceProfile);

            logger.Info($"Advertising service for {serviceProfile.InstanceName}");
        }
        catch (Exception ex)
        {
            logger.Error("Failed to advertise service", ex);
        }
    }

    /// <inheritdoc />
    public void UnAdvertiseService()
    {
        queryTimer?.Dispose();
        queryTimer = null;
        lock (cacheLock) records.Clear();
        try
        {
            if (serviceDiscovery is not null && serviceProfile is not null && multicastService is not null)
            {
                logger.Info($"Un-advertising service for {serviceProfile.InstanceName}");

                // Validate service instance name format
                if (string.IsNullOrWhiteSpace(serviceProfile.QualifiedServiceName.ToString()))
                {
                    logger.Warn("Service profile has invalid name, skipping unadvertise");
                    return;
                }
                serviceDiscovery.Unadvertise(serviceProfile);
                multicastService.Stop();
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            logger.Error("Service already unadvertised or invalid state", ex);
        }
        finally
        {
            serviceDiscovery?.Dispose();
            multicastService?.Dispose();
            serviceDiscovery = null;
            multicastService = null;
            serviceProfile = null;
        }
    }


    /// <inheritdoc />
    public void StartDiscovery()
    {
        try
        {
            if (serviceDiscovery is null || multicastService is null) return;

            serviceDiscovery.ServiceInstanceDiscovered += (sender, args) =>
            {
                // Ignore our own service instance
                if (serviceProfile is not null && args.ServiceInstanceName == serviceProfile.FullyQualifiedName) return;
                
                // Only process _sefirah._udp services
                if (!args.ServiceInstanceName.ToCanonical().ToString().Contains(ServiceType)) return;
                // Queries
                multicastService.SendQuery(args.ServiceInstanceName, type: DnsType.TXT);
                multicastService.SendQuery(args.ServiceInstanceName, type: DnsType.SRV);
                multicastService.SendQuery(args.ServiceInstanceName, type: DnsType.A);

            };

            // Add handler for answers
            multicastService.AnswerReceived += (sender, args) => {
                var incoming = args.Message.Answers.Concat(args.Message.AdditionalRecords).ToList();
                List<DiscoveredMdnsServiceArgs> found = [];
                lock (cacheLock)
                {
                    var now = DateTime.UtcNow;
                    foreach (var key in records.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray())
                        records.Remove(key);
                    foreach (var record in incoming.Where(record => record is TXTRecord or SRVRecord or AddressRecord))
                    {
                        var key = $"{record.GetType().Name}:{record.CanonicalName}";
                        if (record is AddressRecord address) key += $":{address.Address}";
                        if (record.TTL <= TimeSpan.Zero) records.Remove(key);
                        else records[key] = (record, now + record.TTL);
                    }
                    if (records.Count > 2048) records.Clear();
                    var cached = records.Values.Select(value => value.Record).ToList();
                    foreach (var txt in cached.OfType<TXTRecord>().Where(txt => txt.CanonicalName.Contains(ServiceType)))
                    {
                        var name = txt.CanonicalName.ToString();
                        if (name.Equals(serviceProfile?.FullyQualifiedName.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                        var attributes = txt.Strings.Select(value => value.Split('=', 2)).Where(parts => parts.Length == 2)
                            .GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
                        if (!attributes.TryGetValue(DeviceNameProperty, out var deviceName)) continue;
                        var srv = cached.OfType<SRVRecord>().FirstOrDefault(record => record.CanonicalName.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (srv?.Target is null) continue;
                        var port = attributes.TryGetValue(ServerPortProperty, out var value) && int.TryParse(value, out var parsed)
                            ? parsed : srv.Port;
                        if (port is < 1 or > 65535) continue;
                        foreach (var address in cached.OfType<AddressRecord>().Where(record =>
                            record.Address?.AddressFamily == AddressFamily.InterNetwork &&
                            record.CanonicalName.ToString().Equals(srv.Target.ToString(), StringComparison.OrdinalIgnoreCase)))
                            found.Add(new(name.Split('.')[0], deviceName, address.Address.ToString(), port));
                    }
                }
                foreach (var srv in incoming.OfType<SRVRecord>()) multicastService?.SendQuery(srv.Target, type: DnsType.A);
                foreach (var peer in found) DiscoveredMdnsService?.Invoke(this, peer);
            };

            multicastService.Start();
            serviceDiscovery.QueryServiceInstances(ServiceType);
            queryTimer ??= new Timer(_ =>
            {
                try { serviceDiscovery?.QueryServiceInstances(ServiceType); }
                catch (Exception ex) { logger.Debug("mDNS refresh failed", ex); }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            logger.Info("Started mDNS discovery service");
        }
        catch (Exception ex)
        {
            logger.Error("Failed to start discovery service", ex);
        }
    }
}
