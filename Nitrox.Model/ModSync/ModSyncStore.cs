using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nitrox.Model.Logger;

namespace Nitrox.Model.ModSync;

/// <summary>Downloaded and verified files waiting to be installed the next time the game is not running.</summary>
public sealed class PendingModSync
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<string> PackageIds { get; set; } = [];
    public List<PendingModFile> Files { get; set; } = [];
    public List<string> Removals { get; set; } = [];
    public bool AllowNativeLibraries { get; set; }
    public bool AllowExecutables { get; set; }

    public long TotalSize => Files.Sum(f => f.Size);
}

public sealed class PendingModFile
{
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public ModFileKind Kind { get; set; }
}

/// <summary>One applied synchronization, with everything needed to undo it.</summary>
public sealed class ModSyncRecord
{
    public string Id { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public DateTime InstalledUtc { get; set; }
    public List<string> PackageIds { get; set; } = [];
    public List<InstalledModFile> Files { get; set; } = [];
    public List<RemovedModFile> Removed { get; set; } = [];
    public bool Uninstalled { get; set; }
}

public sealed class InstalledModFile
{
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";

    /// <summary>Backup of the file this one replaced (relative to the sync folder), or empty if the file was new.</summary>
    public string BackupPath { get; set; } = "";
}

public sealed class RemovedModFile
{
    public string RelativePath { get; set; } = "";
    public string BackupPath { get; set; } = "";
}

public sealed class ModSyncLedger
{
    public List<ModSyncRecord> Records { get; set; } = [];
}

public sealed class ModInstallException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
///     Manages Nitrod's folder inside BepInEx (<c>BepInEx/NitrodSync</c>): staged downloads, the pending install, backups and the
///     ledger of synchronized installs. Files are only ever written inside BepInEx/plugins and BepInEx/patchers, every replaced or
///     removed file is backed up first, and a failed install is rolled back.
/// </summary>
/// <remarks>
///     Installing must happen while the game is closed: BepInEx keeps loaded plugin DLLs open, and replacing code under a running
///     game is unsafe. Callers are expected to check that the game is not running; a locked file also makes the install fail and
///     roll back instead of half-applying.
/// </remarks>
public sealed class ModSyncStore
{
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    public string GameRoot { get; }
    public string BepInExRoot { get; }
    public string SyncRoot { get; }
    public string StagingRoot => Path.Combine(SyncRoot, "staging");
    public string BackupRoot => Path.Combine(SyncRoot, "backups");
    public string PendingFilePath => Path.Combine(SyncRoot, "pending.json");
    public string LedgerFilePath => Path.Combine(SyncRoot, "ledger.json");
    public string HashCacheFilePath => Path.Combine(SyncRoot, "hashcache.json");

    public ModSyncStore(string gameRoot)
    {
        GameRoot = Path.GetFullPath(gameRoot);
        BepInExRoot = ModScanner.GetBepInExRoot(GameRoot);
        SyncRoot = ModScanner.GetSyncRoot(GameRoot);
    }

    public bool HasPending => File.Exists(PendingFilePath);

    /// <summary>Records a downloaded plan as ready to install. All files must already be staged and verified.</summary>
    public PendingModSync SavePending(ModSyncPlan plan, ModSyncLimits limits)
    {
        List<string> problems = limits.Validate(plan);
        if (problems.Count > 0)
        {
            throw new ModInstallException(string.Join(Environment.NewLine, problems));
        }
        foreach (ModFile file in plan.Downloads)
        {
            if (!File.Exists(ModDownloader.GetBlobPath(StagingRoot, file.Sha256)))
            {
                throw new ModInstallException($"'{file.RelativePath}' has not been downloaded.");
            }
        }

        PendingModSync pending = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ServerName = plan.ServerName,
            ServerAddress = plan.ServerAddress,
            CreatedUtc = DateTime.UtcNow,
            PackageIds = plan.PackageIds.ToList(),
            Files = plan.Downloads.Select(d => new PendingModFile { RelativePath = d.RelativePath, Sha256 = d.Sha256, Size = d.Size, Kind = d.Kind }).ToList(),
            Removals = plan.Removals.ToList(),
            AllowNativeLibraries = limits.AllowNativeLibraries,
            AllowExecutables = limits.AllowExecutables
        };
        WriteJson(PendingFilePath, pending);
        return pending;
    }

    public PendingModSync? LoadPending() => ReadJson<PendingModSync>(PendingFilePath);

    public void DiscardPending()
    {
        PendingModSync? pending = LoadPending();
        TryDelete(PendingFilePath);
        if (pending != null)
        {
            foreach (PendingModFile file in pending.Files)
            {
                TryDelete(ModDownloader.GetBlobPath(StagingRoot, file.Sha256));
            }
        }
    }

    public ModSyncLedger LoadLedger() => ReadJson<ModSyncLedger>(LedgerFilePath) ?? new ModSyncLedger();

    /// <summary>
    ///     Installs the pending sync: re-verifies every staged file, backs up and removes outdated files, then moves the new files
    ///     into place. On any failure all changes made so far are undone.
    /// </summary>
    public ModSyncRecord ApplyPending()
    {
        PendingModSync pending = LoadPending() ?? throw new ModInstallException("There is nothing to install.");
        ModSyncRecord record = new()
        {
            Id = pending.Id.Length > 0 ? pending.Id : Guid.NewGuid().ToString("N"),
            ServerName = pending.ServerName,
            ServerAddress = pending.ServerAddress,
            PackageIds = pending.PackageIds,
            InstalledUtc = DateTime.UtcNow
        };
        string backupFolder = Path.Combine(BackupRoot, record.Id);

        // 1. Validate everything before touching the game folder.
        List<(PendingModFile File, string Destination, string Blob)> installs = [];
        foreach (PendingModFile file in pending.Files)
        {
            if (file.Kind == ModFileKind.NATIVE_LIBRARY && !pending.AllowNativeLibraries || file.Kind == ModFileKind.EXECUTABLE && !pending.AllowExecutables)
            {
                throw new ModInstallException($"'{file.RelativePath}' was not approved for installation.");
            }
            string destination = ModPathSafety.ResolveInside(BepInExRoot, file.RelativePath);
            string blob = ModDownloader.GetBlobPath(StagingRoot, file.Sha256);
            if (!File.Exists(blob) || ModFileHasher.ComputeSha256(blob) != file.Sha256)
            {
                throw new ModInstallException($"The downloaded copy of '{file.RelativePath}' is missing or was modified. Join the server again to re-download it.");
            }
            installs.Add((file, destination, blob));
        }
        List<(string RelativePath, string Destination)> removals = pending.Removals.Select(r => (r, ModPathSafety.ResolveInside(BepInExRoot, r))).ToList();

        // 2. Apply, journaling every step so a failure can be undone.
        Stack<Action> undo = new();
        try
        {
            foreach ((string relativePath, string destination) in removals)
            {
                if (!File.Exists(destination))
                {
                    continue;
                }
                string backup = BackupFile(backupFolder, relativePath, destination);
                undo.Push(() => MoveFile(backup, destination));
                record.Removed.Add(new RemovedModFile { RelativePath = relativePath, BackupPath = ToSyncRelative(backup) });
            }

            foreach ((PendingModFile file, string destination, string blob) in installs)
            {
                string backupRelative = "";
                if (File.Exists(destination))
                {
                    if (ModFileHasher.ComputeSha256(destination) == file.Sha256)
                    {
                        continue; // Already identical. Not ours to track or uninstall.
                    }
                    string backup = BackupFile(backupFolder, file.RelativePath, destination);
                    undo.Push(() => MoveFile(backup, destination));
                    backupRelative = ToSyncRelative(backup);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = $"{destination}.nitrodtmp";
                File.Copy(blob, temporary, true);
                MoveFile(temporary, destination);
                undo.Push(() => TryDelete(destination));
                record.Files.Add(new InstalledModFile { RelativePath = file.RelativePath, Sha256 = file.Sha256, BackupPath = backupRelative });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            while (undo.Count > 0)
            {
                try
                {
                    undo.Pop()();
                }
                catch (Exception undoException)
                {
                    Log.Error(undoException, "Failed to undo part of a mod install");
                }
            }
            throw new ModInstallException($"Installing synchronized mods failed and was rolled back: {ex.Message} If Subnautica is running, close it and try again.", ex);
        }

        // 3. Commit.
        ModSyncLedger ledger = LoadLedger();
        ledger.Records.Add(record);
        WriteJson(LedgerFilePath, ledger);
        TryDelete(PendingFilePath);
        foreach ((PendingModFile _, string _, string blob) in installs)
        {
            TryDelete(blob);
        }
        return record;
    }

    /// <summary>
    ///     Undoes a synchronized install: deletes files Nitrod installed (only if they are still the exact files it installed) and
    ///     restores the backups of files they replaced or removed. Returns paths that were left alone because they changed since.
    /// </summary>
    public List<string> Uninstall(string recordId)
    {
        ModSyncLedger ledger = LoadLedger();
        ModSyncRecord record = ledger.Records.FirstOrDefault(r => r.Id == recordId) ?? throw new ModInstallException("Unknown synchronized install.");
        if (record.Uninstalled)
        {
            return [];
        }

        List<string> skipped = [];
        foreach (InstalledModFile file in Enumerable.Reverse(record.Files))
        {
            string destination = ModPathSafety.ResolveInside(BepInExRoot, file.RelativePath);
            if (File.Exists(destination))
            {
                if (ModFileHasher.ComputeSha256(destination) != file.Sha256)
                {
                    skipped.Add(file.RelativePath);
                    continue;
                }
                File.Delete(destination);
            }
            if (file.BackupPath.Length > 0 && File.Exists(FromSyncRelative(file.BackupPath)))
            {
                MoveFile(FromSyncRelative(file.BackupPath), destination);
            }
            DeleteEmptyParents(destination);
        }
        foreach (RemovedModFile file in record.Removed)
        {
            string destination = ModPathSafety.ResolveInside(BepInExRoot, file.RelativePath);
            string backup = FromSyncRelative(file.BackupPath);
            if (File.Exists(backup) && !File.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                MoveFile(backup, destination);
            }
        }

        record.Uninstalled = true;
        WriteJson(LedgerFilePath, ledger);
        try
        {
            string backupFolder = Path.Combine(BackupRoot, record.Id);
            if (Directory.Exists(backupFolder) && !Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(backupFolder, true);
            }
        }
        catch (IOException)
        {
            // Leftover empty backup folders are harmless.
        }
        return skipped;
    }

    /// <summary>Map of content hash to local file, used to copy files instead of downloading them again.</summary>
    public static Dictionary<string, string> IndexLocalFiles(string bepInExRoot, ModManifest localManifest)
    {
        Dictionary<string, string> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModFile file in localManifest.Packages.SelectMany(p => p.Files))
        {
            string fullPath = Path.Combine(bepInExRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!index.ContainsKey(file.Sha256) && File.Exists(fullPath))
            {
                index[file.Sha256] = fullPath;
            }
        }
        return index;
    }

    private string BackupFile(string backupFolder, string relativePath, string source)
    {
        string backup = Path.Combine(backupFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        MoveFile(source, backup);
        DeleteEmptyParents(source);
        return backup;
    }

    /// <summary>Deletes directories left empty by a removal, stopping at plugins/patchers (never deleting them or anything above).</summary>
    private void DeleteEmptyParents(string filePath)
    {
        string root = BepInExRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? directory = Path.GetDirectoryName(filePath);
        while (directory != null)
        {
            string? parent = Path.GetDirectoryName(directory);
            if (parent == null || parent.TrimEnd(Path.DirectorySeparatorChar).Length <= root.Length)
            {
                return;
            }
            try
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }
                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            directory = parent;
        }
    }

    private string ToSyncRelative(string fullPath) => ModPathSafety.ToRelativePath(SyncRoot, fullPath);

    private string FromSyncRelative(string relative) => Path.Combine(SyncRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void MoveFile(string from, string to)
    {
        if (File.Exists(to))
        {
            File.Delete(to);
        }
        File.Move(from, to);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Unable to delete '{path}': {ex.Message}");
        }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            Log.Error(ex, $"Ignoring corrupt mod sync file '{path}'");
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, jsonOptions));
        MoveFile(temporary, path);
    }
}
