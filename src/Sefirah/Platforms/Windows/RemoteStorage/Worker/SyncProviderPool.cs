using System.Runtime.InteropServices.WindowsRuntime;
using Sefirah.Platforms.Windows.Abstractions;
using Sefirah.Platforms.Windows.RemoteStorage.Abstractions;
using Sefirah.Platforms.Windows.RemoteStorage.Commands;
using Sefirah.Platforms.Windows.RemoteStorage.RemoteAbstractions;
using Vanara.PInvoke;
using Windows.Storage.Provider;

namespace Sefirah.Platforms.Windows.RemoteStorage.Worker;

public partial class SyncProviderPool(
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    private readonly AsyncSessionPool<string> _sessions = new(
        (id, exception) => logger.Error($"Sync provider {id} stopped unexpectedly", exception));

    /// <summary>
    /// Starts the sync loop for a root, replacing any existing one with the same Id. 
    /// Call this after registering a sync root.
    /// </summary>
    public async Task StartAsync(StorageProviderSyncRootInfo syncRootInfo)
    {
        if (_sessions.Has(syncRootInfo.Id))
        {
            logger.Debug($"Stopping existing sync provider for {syncRootInfo.Id}");
        }

        await _sessions.StartAsync(
            syncRootInfo.Id,
            cancellation => Run(syncRootInfo, cancellation));
        logger.Debug($"Started new sync provider for {syncRootInfo.Id}");
    }

    public bool Has(string id) => _sessions.Has(id);

    public Task StopAll() => _sessions.StopAllAsync();

    public async Task StopSyncRoot(StorageProviderSyncRootInfo syncRootInfo)
    {
        try
        {
            if (_sessions.Has(syncRootInfo.Id))
            {
                logger.Debug($"Stopping existing sync provider for {syncRootInfo.Id}");
                await _sessions.StopAsync(syncRootInfo.Id);
            }
        }
        catch (Exception ex)
        {
            logger.Error("Failed to stop sync root", ex);
        }
    }

    public Task Stop(string id) => _sessions.StopAsync(id);

    private async Task Run(StorageProviderSyncRootInfo syncRootInfo, CancellationToken cancellation)
    {
        using var scope = scopeFactory.CreateScope();
        var contextAccessor = scope.ServiceProvider.GetRequiredService<SyncProviderContextAccessor>();
        contextAccessor.Context = new SyncProviderContext
        {
            Id = syncRootInfo.Id,
            RootDirectory = syncRootInfo.Path.Path,
            PopulationPolicy = (PopulationPolicy)syncRootInfo.PopulationPolicy,
        };
        var remoteContextSetter = scope.ServiceProvider.GetServices<IRemoteContextSetter>()
            .Single((setter) => setter.RemoteKind == contextAccessor.Context.RemoteKind);
        remoteContextSetter.SetRemoteContext(syncRootInfo.Context.ToArray());

        var syncProvider = scope.ServiceProvider.GetRequiredService<SyncProvider>();
        await syncProvider.Run(cancellation);
    }
}
