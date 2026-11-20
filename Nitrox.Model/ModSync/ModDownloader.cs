using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nitrox.Model.Logger;

namespace Nitrox.Model.ModSync;

/// <summary>A piece of a host file, as answered by the host.</summary>
public sealed class ModChunk
{
    public string Sha256 { get; }
    public long Offset { get; }

    /// <summary>Size of the whole file on the host, used to detect files that changed after the manifest was sent.</summary>
    public long TotalSize { get; }

    public byte[] Data { get; }

    /// <summary>Empty on success, otherwise why the host refused the request.</summary>
    public string Error { get; }

    public ModChunk(string sha256, long offset, long totalSize, byte[] data, string error)
    {
        Sha256 = sha256;
        Offset = offset;
        TotalSize = totalSize;
        Data = data ?? [];
        Error = error ?? "";
    }
}

/// <summary>Where chunks come from: a packet connection to the host in-game, or an in-memory source in tests.</summary>
public interface IModChunkSource
{
    Task<ModChunk> RequestChunkAsync(string sha256, long offset, int length, CancellationToken cancellationToken);
}

/// <summary>Limits applied to everything a host offers, before anything is downloaded.</summary>
public sealed class ModSyncLimits
{
    public long MaxFileSize { get; set; } = 1024L * 1024 * 1024;
    public long MaxTotalSize { get; set; } = 4L * 1024 * 1024 * 1024;
    public int MaxFiles { get; set; } = 10_000;

    /// <summary>Native libraries (C/C++ code) need explicit approval on top of the normal consent.</summary>
    public bool AllowNativeLibraries { get; set; }

    /// <summary>Executables and scripts need explicit approval. Nitrod never runs them either way.</summary>
    public bool AllowExecutables { get; set; }

    /// <summary>Returns every reason the plan can't be accepted. Empty means the plan is acceptable.</summary>
    public List<string> Validate(ModSyncPlan plan)
    {
        List<string> problems = [];
        if (plan.Downloads.Count > MaxFiles)
        {
            problems.Add($"The host offers {plan.Downloads.Count} files, more than the limit of {MaxFiles}.");
        }
        if (plan.DownloadSize > MaxTotalSize)
        {
            problems.Add($"The download is {FormatSize(plan.DownloadSize)}, more than the limit of {FormatSize(MaxTotalSize)}.");
        }
        foreach (ModFile file in plan.Downloads)
        {
            if (!ModPathSafety.IsSafeRelativePath(file.RelativePath, out string reason))
            {
                problems.Add($"Unsafe path '{file.RelativePath}': {reason}");
            }
            if (!ModPathSafety.IsValidSha256(file.Sha256))
            {
                problems.Add($"Invalid hash for '{file.RelativePath}'.");
            }
            if (file.Size < 0 || file.Size > MaxFileSize)
            {
                problems.Add($"'{file.RelativePath}' is {FormatSize(file.Size)}, more than the per-file limit of {FormatSize(MaxFileSize)}.");
            }
            if (file.Kind == ModFileKind.NATIVE_LIBRARY && !AllowNativeLibraries)
            {
                problems.Add($"'{file.RelativePath}' is a native library and native code was not approved.");
            }
            if (file.Kind == ModFileKind.EXECUTABLE && !AllowExecutables)
            {
                problems.Add($"'{file.RelativePath}' is a program or script and executables were not approved.");
            }
        }
        foreach (string removal in plan.Removals)
        {
            if (!ModPathSafety.IsSafeRelativePath(removal, out string reason))
            {
                problems.Add($"Unsafe path '{removal}': {reason}");
            }
        }
        return problems;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };
}

public enum ModDownloadPhase
{
    PREPARING,
    DOWNLOADING,
    VERIFYING,
    COMPLETED,
    FAILED,
    CANCELLED
}

/// <summary>Snapshot of a download, for progress UI.</summary>
public sealed class ModDownloadProgress
{
    public ModDownloadPhase Phase { get; init; }
    public string CurrentFile { get; init; } = "";
    public string CurrentPackage { get; init; } = "";
    public int FilesCompleted { get; init; }
    public int FileCount { get; init; }
    public int PackagesRemaining { get; init; }
    public long BytesCompleted { get; init; }
    public long BytesTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public string Message { get; init; } = "";

    public double Fraction => BytesTotal <= 0 ? (Phase == ModDownloadPhase.COMPLETED ? 1 : 0) : Math.Min(1, (double)BytesCompleted / BytesTotal);
    public int FilesRemaining => Math.Max(0, FileCount - FilesCompleted);
}

public sealed class ModDownloadException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
///     Downloads host files into a staging folder named by content hash. Files are requested in chunks with several requests in
///     flight, written in order, resumed from partial files after interruptions, and verified with SHA-256 before they are
///     accepted. Files already present locally with the same hash are copied instead of downloaded.
/// </summary>
public sealed class ModDownloader
{
    public const int DEFAULT_CHUNK_SIZE = 64 * 1024;
    public const int MAX_CHUNK_SIZE = 256 * 1024;

    /// <summary>Requests in flight (1 MB with the default chunk size), enough to hide network latency.</summary>
    public const int DEFAULT_WINDOW = 16;

    private readonly string stagingDirectory;
    private readonly IModChunkSource source;
    private readonly int chunkSize;
    private readonly int window;
    private readonly int maxAttempts;
    private readonly IReadOnlyDictionary<string, string> localFilesByHash;

    private readonly Stopwatch clock = new();
    private readonly Queue<(double Seconds, long Bytes)> speedSamples = new();
    private long bytesCompleted;

    public ModDownloader(string stagingDirectory, IModChunkSource source, int chunkSize = DEFAULT_CHUNK_SIZE, int window = DEFAULT_WINDOW, int maxAttempts = 3,
                         IReadOnlyDictionary<string, string>? localFilesByHash = null)
    {
        if (chunkSize is <= 0 or > MAX_CHUNK_SIZE)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }
        this.stagingDirectory = stagingDirectory;
        this.source = source;
        this.chunkSize = chunkSize;
        this.window = Math.Max(1, window);
        this.maxAttempts = Math.Max(1, maxAttempts);
        this.localFilesByHash = localFilesByHash ?? new Dictionary<string, string>();
    }

    public static string GetBlobPath(string stagingDirectory, string sha256) => Path.Combine(stagingDirectory, $"{sha256}.blob");

    public static string GetPartialPath(string stagingDirectory, string sha256) => Path.Combine(stagingDirectory, $"{sha256}.part");

    /// <summary>
    ///     Downloads all distinct files. Throws <see cref="ModDownloadException" /> when a file can't be fetched or verified,
    ///     and <see cref="OperationCanceledException" /> when cancelled. Partial files are kept so a later call resumes them.
    /// </summary>
    public async Task DownloadAsync(IReadOnlyList<ModFile> files, IProgress<ModDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stagingDirectory);
        // Several paths can share the same content; download it once.
        List<ModFile> distinct = files.GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        long bytesTotal = distinct.Sum(f => f.Size);
        Dictionary<string, int> remainingFilesPerPackage = files.GroupBy(GetPackageId).ToDictionary(g => g.Key, g => g.Count());
        bytesCompleted = 0;
        clock.Restart();
        speedSamples.Clear();

        int filesCompleted = 0;
        foreach (ModFile file in distinct)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageId = GetPackageId(file);
            void Report(ModDownloadPhase phase, string message = "") => progress?.Report(new ModDownloadProgress
            {
                Phase = phase,
                CurrentFile = file.RelativePath,
                CurrentPackage = packageId,
                FilesCompleted = filesCompleted,
                FileCount = distinct.Count,
                PackagesRemaining = remainingFilesPerPackage.Count(p => p.Value > 0),
                BytesCompleted = bytesCompleted,
                BytesTotal = bytesTotal,
                BytesPerSecond = GetSpeed(),
                Message = message
            });

            await DownloadFileAsync(file, Report, cancellationToken);
            filesCompleted++;
            foreach (ModFile sameContent in files.Where(f => f.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                string id = GetPackageId(sameContent);
                remainingFilesPerPackage[id]--;
            }
        }

        progress?.Report(new ModDownloadProgress
        {
            Phase = ModDownloadPhase.COMPLETED,
            FilesCompleted = filesCompleted,
            FileCount = distinct.Count,
            BytesCompleted = bytesTotal,
            BytesTotal = bytesTotal,
            BytesPerSecond = GetSpeed(),
            Message = "All files downloaded and verified."
        });
    }

    private async Task DownloadFileAsync(ModFile file, Action<ModDownloadPhase, string> report, CancellationToken cancellationToken)
    {
        if (!ModPathSafety.IsValidSha256(file.Sha256))
        {
            throw new ModDownloadException($"Refusing '{file.RelativePath}': invalid hash.");
        }
        string blobPath = GetBlobPath(stagingDirectory, file.Sha256);
        string partialPath = GetPartialPath(stagingDirectory, file.Sha256);
        long startBytes = bytesCompleted;

        // Already downloaded earlier (e.g. an interrupted sync that got further)?
        if (File.Exists(blobPath))
        {
            if (new FileInfo(blobPath).Length == file.Size && await HashAsync(blobPath) == file.Sha256)
            {
                AddBytes(file.Size);
                return;
            }
            File.Delete(blobPath);
        }

        // Same content already on disk (e.g. the same mod installed under another folder name)?
        if (localFilesByHash.TryGetValue(file.Sha256, out string? localCopy) && File.Exists(localCopy))
        {
            report(ModDownloadPhase.VERIFYING, "Copying from local files");
            File.Copy(localCopy, partialPath, true);
            if (await HashAsync(partialPath) == file.Sha256)
            {
                MoveIntoPlace(partialPath, blobPath);
                AddBytes(file.Size);
                return;
            }
            File.Delete(partialPath);
        }

        Exception? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                if (offset > file.Size)
                {
                    File.Delete(partialPath);
                    offset = 0;
                }
                bytesCompleted = startBytes + offset;
                report(ModDownloadPhase.DOWNLOADING, offset > 0 ? $"Resuming at {ModSyncLimits.FormatSize(offset)}" : "");

                await FetchRangeAsync(file, partialPath, offset, report, cancellationToken);

                report(ModDownloadPhase.VERIFYING, "Verifying SHA-256");
                string actualHash = await HashAsync(partialPath);
                if (actualHash != file.Sha256)
                {
                    // Corrupt or tampered data: start this file over.
                    File.Delete(partialPath);
                    bytesCompleted = startBytes;
                    throw new ModDownloadException($"'{file.RelativePath}' failed verification (expected {file.Sha256}, got {actualHash}).");
                }
                MoveIntoPlace(partialPath, blobPath);
                bytesCompleted = startBytes + file.Size;
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ModDownloadException or IOException or TimeoutException or OperationCanceledException)
            {
                lastError = ex;
                Log.Warn($"Mod download attempt {attempt}/{maxAttempts} for '{file.RelativePath}' failed: {ex.Message}");
                report(ModDownloadPhase.DOWNLOADING, $"Retrying ({attempt}/{maxAttempts})");
            }
        }
        throw new ModDownloadException($"Could not download '{file.RelativePath}' after {maxAttempts} attempts: {lastError?.Message}", lastError);
    }

    private async Task FetchRangeAsync(ModFile file, string partialPath, long offset, Action<ModDownloadPhase, string> report, CancellationToken cancellationToken)
    {
        using FileStream output = new(partialPath, FileMode.Append, FileAccess.Write, FileShare.None);
        Queue<(long Offset, int Length, Task<ModChunk> Task)> inFlight = new();
        long nextOffset = offset;
        DateTime lastReport = DateTime.MinValue;

        void FillWindow()
        {
            while (inFlight.Count < window && nextOffset < file.Size)
            {
                int length = (int)Math.Min(chunkSize, file.Size - nextOffset);
                inFlight.Enqueue((nextOffset, length, source.RequestChunkAsync(file.Sha256, nextOffset, length, cancellationToken)));
                nextOffset += length;
            }
        }

        try
        {
            FillWindow();
            while (inFlight.Count > 0)
            {
                (long expectedOffset, int expectedLength, Task<ModChunk> task) = inFlight.Dequeue();
                ModChunk chunk = await task;
                if (chunk.Error.Length > 0)
                {
                    throw new ModDownloadException($"The host refused '{file.RelativePath}': {chunk.Error}");
                }
                if (!chunk.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) || chunk.Offset != expectedOffset || chunk.Data.Length != expectedLength)
                {
                    throw new ModDownloadException($"The host sent an unexpected chunk for '{file.RelativePath}'.");
                }
                if (chunk.TotalSize != file.Size)
                {
                    throw new ModDownloadException($"'{file.RelativePath}' changed on the host since the mod list was sent. Reconnect to get the current list.");
                }
                await output.WriteAsync(chunk.Data, 0, chunk.Data.Length, cancellationToken);
                AddBytes(chunk.Data.Length);
                FillWindow();

                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                {
                    lastReport = DateTime.UtcNow;
                    report(ModDownloadPhase.DOWNLOADING, "");
                }
            }
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            // Don't leave unobserved faulted tasks behind when aborting mid-window.
            foreach ((long _, int _, Task<ModChunk> pending) in inFlight)
            {
                _ = pending.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    private static Task<string> HashAsync(string path) => Task.Run(() => ModFileHasher.ComputeSha256(path));

    private static void MoveIntoPlace(string from, string to)
    {
        if (File.Exists(to))
        {
            File.Delete(to);
        }
        File.Move(from, to);
    }

    private void AddBytes(long bytes)
    {
        bytesCompleted += bytes;
        double now = clock.Elapsed.TotalSeconds;
        speedSamples.Enqueue((now, bytesCompleted));
        while (speedSamples.Count > 2 && now - speedSamples.Peek().Seconds > 3)
        {
            speedSamples.Dequeue();
        }
    }

    private double GetSpeed()
    {
        if (speedSamples.Count < 2)
        {
            return 0;
        }
        (double firstSeconds, long firstBytes) = speedSamples.Peek();
        (double lastSeconds, long lastBytes) = speedSamples.Last();
        double elapsed = lastSeconds - firstSeconds;
        return elapsed <= 0 ? 0 : (lastBytes - firstBytes) / elapsed;
    }

    /// <summary>"plugins/Nautilus/Nautilus.dll" -> "plugins/Nautilus"</summary>
    public static string GetPackageId(ModFile file)
    {
        string[] segments = file.RelativePath.Split('/');
        return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : file.RelativePath;
    }
}
