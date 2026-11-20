using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nitrox.Model.Core;
using Nitrox.Model.ModSync;

namespace Nitrox.Server.Subnautica.Services;

/// <summary>
///     Publishes the host's BepInEx mods to joining players: builds the mod manifest from the game folder the server runs
///     against, and serves file contents by content hash. Only files listed in the manifest can ever be read.
/// </summary>
internal sealed class ModSyncService(IOptions<ServerStartOptions> startOptions, IOptions<SubnauticaServerOptions> serverOptions, ILogger<ModSyncService> logger) : IHostedService
{
    /// <summary>Requests that would wait longer than this for upload budget are refused instead of queued.</summary>
    private static readonly TimeSpan maxThrottleDelay = TimeSpan.FromSeconds(10);

    private readonly IOptions<ServerStartOptions> startOptions = startOptions;
    private readonly IOptions<SubnauticaServerOptions> serverOptions = serverOptions;
    private readonly ILogger<ModSyncService> logger = logger;
    private readonly ConcurrentDictionary<SessionId, UploadBudget> uploadBudgets = new();
    private readonly Lock scanLock = new();
    private Task<Snapshot>? scanTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Scanning hashes every mod file; don't hold up server startup for it.
        RequestRescan();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<ModManifest> GetManifestAsync() => (await GetSnapshotAsync()).Manifest;

    public async Task<PublishedFile?> FindFileAsync(string sha256)
    {
        Snapshot snapshot = await GetSnapshotAsync();
        return snapshot.FilesByHash.TryGetValue(sha256, out PublishedFile? file) ? file : null;
    }

    /// <summary>Rescans in the background, e.g. after the host changed mod files while the server was running.</summary>
    public void RequestRescan()
    {
        lock (scanLock)
        {
            if (scanTask is { IsCompleted: false })
            {
                return;
            }
            scanTask = Task.Run(Scan);
        }
    }

    /// <summary>
    ///     Waits until <paramref name="session" /> may upload <paramref name="bytes" /> more bytes under the configured rate
    ///     limit. Returns false if the client is requesting much faster than the limit allows.
    /// </summary>
    public async Task<bool> WaitForUploadBudgetAsync(SessionId session, int bytes)
    {
        int megabytesPerSecond = serverOptions.Value.ModSyncMaxUploadMegabytesPerSecond;
        if (megabytesPerSecond <= 0)
        {
            return true;
        }
        PruneBudgets();
        UploadBudget budget = uploadBudgets.GetOrAdd(session, _ => new UploadBudget());
        TimeSpan delay = budget.Reserve(bytes, megabytesPerSecond * 1024d * 1024d);
        if (delay > maxThrottleDelay)
        {
            return false;
        }
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }
        return true;
    }

    private Task<Snapshot> GetSnapshotAsync()
    {
        lock (scanLock)
        {
            return scanTask ?? Task.FromResult(Snapshot.Empty);
        }
    }

    private Snapshot Scan()
    {
        SubnauticaServerOptions options = serverOptions.Value;
        string? gamePath = startOptions.Value.GamePath;
        if (!options.ModSyncEnabled || string.IsNullOrWhiteSpace(gamePath))
        {
            logger.ZLogInformation($"Mod sync is disabled; joining players won't receive this server's mod list.");
            return Snapshot.Empty;
        }

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string cachePath = Path.Combine(startOptions.Value.NitroxAppDataPath ?? Path.GetTempPath(), "cache", "modsync-hashcache.json");
            ModScanner scanner = new(AssemblyMetadataPluginReader.Instance, new ModFileHasher(cachePath));
            ModManifest manifest = scanner.Scan(gamePath, new ModScanOptions
            {
                OptionalPackages = ModScanOptions.ParseList(options.ModSyncOptionalMods),
                ExcludedPackages = ModScanOptions.ParseList(options.ModSyncExcludedMods)
            });

            string bepInExRoot = ModScanner.GetBepInExRoot(gamePath);
            Dictionary<string, PublishedFile> filesByHash = new(StringComparer.OrdinalIgnoreCase);
            foreach (ModFile file in manifest.Packages.SelectMany(p => p.Files))
            {
                filesByHash.TryAdd(file.Sha256, new PublishedFile(Path.Combine(bepInExRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), file.Size));
            }

            if (manifest.Packages.Count == 0)
            {
                logger.ZLogInformation($"Mod sync: no BepInEx mods found in the game folder; players can join without mods.");
            }
            else
            {
                logger.ZLogInformation($"Mod sync: sharing {manifest.Packages.Count} mod package(s), {ModSyncLimits.FormatSize(manifest.TotalSize)} (scanned in {stopwatch.ElapsedMilliseconds}ms)");
                foreach (ModPackage package in manifest.Packages)
                {
                    logger.ZLogDebug($"Mod sync: {package.DisplayName} {package.PrimaryVersion} [{package.Id}] {package.Requirement}, {package.Files.Count} file(s)");
                }
            }
            return new Snapshot(manifest, filesByHash);
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"Mod sync: failed to scan the game folder's BepInEx mods; players won't receive a mod list.");
            return Snapshot.Empty;
        }
    }

    private void PruneBudgets()
    {
        if (uploadBudgets.Count < 64)
        {
            return;
        }
        foreach (KeyValuePair<SessionId, UploadBudget> pair in uploadBudgets)
        {
            if (pair.Value.IsIdle)
            {
                uploadBudgets.TryRemove(pair.Key, out _);
            }
        }
    }

    public sealed record PublishedFile(string FullPath, long Size);

    private sealed record Snapshot(ModManifest Manifest, Dictionary<string, PublishedFile> FilesByHash)
    {
        public static readonly Snapshot Empty = new(ModManifest.Empty(), []);
    }

    /// <summary>Token bucket: tracks when the session's already-reserved bytes will have been "sent" at the allowed rate.</summary>
    private sealed class UploadBudget
    {
        private readonly Lock budgetLock = new();
        private DateTime availableAtUtc = DateTime.MinValue;

        public bool IsIdle
        {
            get
            {
                lock (budgetLock)
                {
                    return availableAtUtc < DateTime.UtcNow.AddMinutes(-1);
                }
            }
        }

        public TimeSpan Reserve(int bytes, double bytesPerSecond)
        {
            lock (budgetLock)
            {
                DateTime now = DateTime.UtcNow;
                DateTime start = availableAtUtc > now ? availableAtUtc : now;
                availableAtUtc = start + TimeSpan.FromSeconds(bytes / bytesPerSecond);
                return start - now;
            }
        }
    }
}
