using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nitrox.Model.ModSync;

namespace Nitrox.Test.Model.ModSync;

/// <summary>Builds throw-away game folders and in-memory manifests for mod sync tests.</summary>
internal sealed class TemporaryGameFolder : IDisposable
{
    public string Root { get; }
    public string BepInEx => Path.Combine(Root, "BepInEx");

    public TemporaryGameFolder(bool installLoader = true)
    {
        Root = Path.Combine(Path.GetTempPath(), $"NitrodModSyncTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(BepInEx, "plugins"));
        if (installLoader)
        {
            Directory.CreateDirectory(Path.Combine(BepInEx, "core"));
            // Any managed assembly works: only its assembly version is read.
            File.Copy(typeof(ModManifest).Assembly.Location, Path.Combine(BepInEx, "core", "BepInEx.dll"));
        }
    }

    public string WriteFile(string bepInExRelativePath, string content) => WriteFile(bepInExRelativePath, Encoding.UTF8.GetBytes(content));

    public string WriteFile(string bepInExRelativePath, byte[] content)
    {
        string path = Path.Combine(BepInEx, bepInExRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Copies the test assembly, which carries fake [BepInPlugin] attributes, as a plugin DLL.</summary>
    public string WritePluginAssembly(string bepInExRelativePath)
    {
        string path = Path.Combine(BepInEx, bepInExRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(typeof(TemporaryGameFolder).Assembly.Location, path, true);
        return path;
    }

    public string ReadFile(string bepInExRelativePath) => File.ReadAllText(Path.Combine(BepInEx, bepInExRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool Exists(string bepInExRelativePath) => File.Exists(Path.Combine(BepInEx, bepInExRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public ModManifest Scan(ModScanOptions? options = null) => new ModScanner(AssemblyMetadataPluginReader.Instance).Scan(Root, options);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

internal static class ManifestBuilder
{
    public static ModFile File(string path, string content, ModFileKind kind = ModFileKind.DATA) =>
        new(path, Encoding.UTF8.GetByteCount(content), Sha(content), kind);

    public static ModPluginInfo Plugin(string guid, string version, params ModDependencyInfo[] dependencies) =>
        new(guid, guid, version, "", dependencies.ToList(), []);

    public static ModPackage Package(string id, ModPluginInfo[] plugins, params ModFile[] files) =>
        new(id, id.Split('/').Last(), ModRequirement.REQUIRED, plugins.ToList(), files.ToList());

    public static ModManifest Manifest(params ModPackage[] packages) => new(ModManifest.CURRENT_FORMAT_VERSION, "83031", "5.4.23.2", packages.ToList(), []);

    public static string Sha(string content) => Sha(Encoding.UTF8.GetBytes(content));

    public static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>Serves chunks of files from a host folder, like the server does, with knobs to simulate failures.</summary>
internal sealed class FolderChunkSource(string bepInExRoot, ModManifest manifest) : IModChunkSource
{
    private readonly Dictionary<string, string> filesByHash = manifest.Packages.SelectMany(p => p.Files)
                                                                      .GroupBy(f => f.Sha256)
                                                                      .ToDictionary(g => g.Key, g => Path.Combine(bepInExRoot, g.First().RelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public int RequestCount;

    /// <summary>Called for every chunk before it is returned; may change it (corrupt, delay, throw).</summary>
    public Func<ModChunk, int, Task<ModChunk>>? Intercept { get; set; }

    public async Task<ModChunk> RequestChunkAsync(string sha256, long offset, int length, CancellationToken cancellationToken)
    {
        int requestNumber = Interlocked.Increment(ref RequestCount);
        ModChunk chunk;
        if (!filesByHash.TryGetValue(sha256, out string? path))
        {
            chunk = new ModChunk(sha256, offset, 0, [], "File is not published by this host.");
        }
        else
        {
            byte[] all = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
            int count = (int)Math.Max(0, Math.Min(length, all.Length - offset));
            chunk = new ModChunk(sha256, offset, all.Length, all.AsSpan((int)offset, count).ToArray(), "");
        }
        return Intercept == null ? chunk : await Intercept(chunk, requestNumber);
    }
}
