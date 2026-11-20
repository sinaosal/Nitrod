using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nitrox.Model.Logger;

namespace Nitrox.Model.ModSync;

/// <summary>
///     Computes SHA-256 hashes of mod files, optionally caching them on disk keyed by path, size and last write time so repeated
///     scans of large mod folders (hundreds of MB of assets) only hash files that changed.
/// </summary>
public sealed class ModFileHasher
{
    private readonly string? cacheFilePath;
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LockObject cacheLock = new();
    private bool isDirty;

    public ModFileHasher(string? cacheFilePath = null)
    {
        this.cacheFilePath = cacheFilePath;
        LoadCache();
    }

    public string GetSha256(string fullPath)
    {
        FileInfo info = new(fullPath);
        long size = info.Length;
        long lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        lock (cacheLock)
        {
            if (cache.TryGetValue(fullPath, out CacheEntry entry) && entry.Size == size && entry.LastWriteTicks == lastWriteTicks && ModPathSafety.IsValidSha256(entry.Sha256))
            {
                return entry.Sha256;
            }
        }

        string hash = ComputeSha256(fullPath);
        lock (cacheLock)
        {
            cache[fullPath] = new CacheEntry { Size = size, LastWriteTicks = lastWriteTicks, Sha256 = hash };
            isDirty = true;
        }
        return hash;
    }

    public static string ComputeSha256(string fullPath)
    {
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return ComputeSha256(stream);
    }

    public static string ComputeSha256(Stream stream)
    {
        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(stream));
    }

    public static string ComputeSha256(byte[] data)
    {
        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(data));
    }

    public static string ToHex(byte[] bytes)
    {
        StringBuilder builder = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    /// <summary>Writes the cache to disk if it changed. Failures are ignored: the cache is only an optimization.</summary>
    public void SaveCache()
    {
        if (cacheFilePath == null)
        {
            return;
        }
        Dictionary<string, CacheEntry> snapshot;
        lock (cacheLock)
        {
            if (!isDirty)
            {
                return;
            }
            // Forget files that no longer exist so the cache doesn't grow forever.
            snapshot = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, CacheEntry> pair in cache)
            {
                if (File.Exists(pair.Key))
                {
                    snapshot[pair.Key] = pair.Value;
                }
            }
            isDirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
            string temporaryPath = $"{cacheFilePath}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
            if (File.Exists(cacheFilePath))
            {
                File.Delete(cacheFilePath);
            }
            File.Move(temporaryPath, cacheFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Unable to save mod hash cache: {ex.Message}");
        }
    }

    private void LoadCache()
    {
        if (cacheFilePath == null || !File.Exists(cacheFilePath))
        {
            return;
        }
        try
        {
            Dictionary<string, CacheEntry>? loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(cacheFilePath));
            if (loaded == null)
            {
                return;
            }
            foreach (KeyValuePair<string, CacheEntry> pair in loaded)
            {
                cache[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug($"Ignoring unreadable mod hash cache: {ex.Message}");
        }
    }

    public sealed class CacheEntry
    {
        public long Size { get; set; }
        public long LastWriteTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
