using System.Threading;
using System.Threading.Tasks;
using Nitrox.Model.ModSync;

namespace Nitrox.Test.Model.ModSync;

[TestClass]
public class ModDownloaderTest
{
    private TemporaryGameFolder host = null!;
    private string staging = null!;

    [TestInitialize]
    public void Setup()
    {
        host = new TemporaryGameFolder();
        staging = Path.Combine(host.Root, "staging");
    }

    [TestCleanup]
    public void Cleanup() => host.Dispose();

    [TestMethod]
    public async Task Download_FetchesAllFilesInChunksAndVerifiesThem()
    {
        byte[] big = RandomBytes(300_000);
        host.WriteFile("plugins/Mod/big.bundle", big);
        host.WriteFile("plugins/Mod/small.txt", "small");
        ModManifest manifest = host.Scan();
        FolderChunkSource source = new(host.BepInEx, manifest);
        List<ModDownloadProgress> reports = [];

        await new ModDownloader(staging, source, chunkSize: 32 * 1024, window: 3).DownloadAsync(AllFiles(manifest), new SynchronousProgress(reports.Add));

        File.ReadAllBytes(ModDownloader.GetBlobPath(staging, ManifestBuilder.Sha(big))).Should().Equal(big);
        File.ReadAllText(ModDownloader.GetBlobPath(staging, ManifestBuilder.Sha("small"))).Should().Be("small");
        source.RequestCount.Should().Be(10 + 1, "300 000 bytes in 32 KiB chunks is 10 requests, plus 1 for the small file");
        reports.Last().Phase.Should().Be(ModDownloadPhase.COMPLETED);
        reports.Last().Fraction.Should().Be(1);
        reports.Should().Contain(r => r.Phase == ModDownloadPhase.VERIFYING);
        reports.Select(r => r.BytesCompleted).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public async Task Download_WritesChunksInOrderEvenWhenResponsesArriveOutOfOrder()
    {
        byte[] data = RandomBytes(200_000);
        host.WriteFile("plugins/Mod/data.bin", data);
        ModManifest manifest = host.Scan();
        FolderChunkSource source = new(host.BepInEx, manifest)
        {
            // Earlier requests answer later than later requests.
            Intercept = async (chunk, number) =>
            {
                await Task.Delay(Math.Max(0, 40 - number * 5));
                return chunk;
            }
        };

        await new ModDownloader(staging, source, chunkSize: 16 * 1024, window: 6).DownloadAsync(AllFiles(manifest));

        File.ReadAllBytes(ModDownloader.GetBlobPath(staging, ManifestBuilder.Sha(data))).Should().Equal(data);
    }

    [TestMethod]
    public async Task Download_ResumesFromPartialFile()
    {
        byte[] data = RandomBytes(100_000);
        host.WriteFile("plugins/Mod/data.bin", data);
        ModManifest manifest = host.Scan();
        string sha = ManifestBuilder.Sha(data);
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(ModDownloader.GetPartialPath(staging, sha), data.AsSpan(0, 60_000).ToArray());
        FolderChunkSource source = new(host.BepInEx, manifest);
        List<long> requestedOffsets = [];
        source.Intercept = (chunk, _) =>
        {
            lock (requestedOffsets)
            {
                requestedOffsets.Add(chunk.Offset);
            }
            return Task.FromResult(chunk);
        };

        await new ModDownloader(staging, source, chunkSize: 20_000).DownloadAsync(AllFiles(manifest));

        requestedOffsets.Should().BeEquivalentTo([60_000L, 80_000L], "only the missing tail is requested");
        File.ReadAllBytes(ModDownloader.GetBlobPath(staging, sha)).Should().Equal(data);
    }

    [TestMethod]
    public async Task Download_RetriesAfterCorruptChunkThenSucceeds()
    {
        byte[] data = RandomBytes(50_000);
        host.WriteFile("plugins/Mod/data.bin", data);
        ModManifest manifest = host.Scan();
        bool corrupted = false;
        FolderChunkSource source = new(host.BepInEx, manifest)
        {
            Intercept = (chunk, _) =>
            {
                if (!corrupted && chunk.Offset == 0)
                {
                    corrupted = true;
                    byte[] bad = chunk.Data.ToArray();
                    bad[10] ^= 0xFF;
                    return Task.FromResult(new ModChunk(chunk.Sha256, chunk.Offset, chunk.TotalSize, bad, ""));
                }
                return Task.FromResult(chunk);
            }
        };

        await new ModDownloader(staging, source, chunkSize: 16 * 1024).DownloadAsync(AllFiles(manifest));

        corrupted.Should().BeTrue();
        File.ReadAllBytes(ModDownloader.GetBlobPath(staging, ManifestBuilder.Sha(data))).Should().Equal(data);
        File.Exists(ModDownloader.GetPartialPath(staging, ManifestBuilder.Sha(data))).Should().BeFalse();
    }

    [TestMethod]
    public async Task Download_FailsWhenDataNeverVerifies()
    {
        host.WriteFile("plugins/Mod/data.bin", RandomBytes(10_000));
        ModManifest manifest = host.Scan();
        FolderChunkSource source = new(host.BepInEx, manifest)
        {
            Intercept = (chunk, _) => Task.FromResult(new ModChunk(chunk.Sha256, chunk.Offset, chunk.TotalSize, new byte[chunk.Data.Length], ""))
        };

        Func<Task> download = () => new ModDownloader(staging, source, maxAttempts: 2).DownloadAsync(AllFiles(manifest));

        await download.Should().ThrowAsync<ModDownloadException>().WithMessage("*after 2 attempts*");
        Directory.GetFiles(staging, "*.blob").Should().BeEmpty("unverified data must never become a staged blob");
    }

    [TestMethod]
    public async Task Download_FailsWhenHostFileChanged()
    {
        host.WriteFile("plugins/Mod/data.bin", RandomBytes(10_000));
        ModManifest manifest = host.Scan();
        FolderChunkSource source = new(host.BepInEx, manifest)
        {
            Intercept = (chunk, _) => Task.FromResult(new ModChunk(chunk.Sha256, chunk.Offset, chunk.TotalSize + 1, chunk.Data, ""))
        };

        Func<Task> download = () => new ModDownloader(staging, source, maxAttempts: 1).DownloadAsync(AllFiles(manifest));

        await download.Should().ThrowAsync<ModDownloadException>().WithMessage("*changed on the host*");
    }

    [TestMethod]
    public async Task Download_SurfacesHostRefusal()
    {
        host.WriteFile("plugins/Mod/data.bin", "x");
        ModManifest manifest = host.Scan();
        FolderChunkSource source = new(host.BepInEx, manifest)
        {
            Intercept = (chunk, _) => Task.FromResult(new ModChunk(chunk.Sha256, chunk.Offset, 0, [], "Downloads are disabled on this server."))
        };

        Func<Task> download = () => new ModDownloader(staging, source, maxAttempts: 1).DownloadAsync(AllFiles(manifest));

        await download.Should().ThrowAsync<ModDownloadException>().WithMessage("*Downloads are disabled*");
    }

    [TestMethod]
    public async Task Download_CopiesIdenticalLocalFilesInsteadOfDownloading()
    {
        host.WriteFile("plugins/Mod/data.bin", "shared content");
        ModManifest manifest = host.Scan();
        string localCopy = Path.Combine(host.Root, "local-copy.bin");
        File.WriteAllText(localCopy, "shared content");
        FolderChunkSource source = new(host.BepInEx, manifest);

        await new ModDownloader(staging, source, localFilesByHash: new Dictionary<string, string> { [ManifestBuilder.Sha("shared content")] = localCopy })
            .DownloadAsync(AllFiles(manifest));

        source.RequestCount.Should().Be(0);
        File.ReadAllText(ModDownloader.GetBlobPath(staging, ManifestBuilder.Sha("shared content"))).Should().Be("shared content");
    }

    [TestMethod]
    public async Task Download_SkipsFilesAlreadyStaged()
    {
        host.WriteFile("plugins/Mod/data.bin", "already here");
        ModManifest manifest = host.Scan();
        Directory.CreateDirectory(staging);
        File.WriteAllText(ModDownloader.GetBlobPath(staging, ManifestBuilder.Sha("already here")), "already here");
        FolderChunkSource source = new(host.BepInEx, manifest);

        await new ModDownloader(staging, source).DownloadAsync(AllFiles(manifest));

        source.RequestCount.Should().Be(0);
    }

    [TestMethod]
    public async Task Download_CancellationKeepsPartialFileForResume()
    {
        byte[] data = RandomBytes(100_000);
        host.WriteFile("plugins/Mod/data.bin", data);
        ModManifest manifest = host.Scan();
        using CancellationTokenSource cancellation = new();
        FolderChunkSource source = new(host.BepInEx, manifest)
        {
            Intercept = (chunk, number) =>
            {
                if (number == 3)
                {
                    cancellation.Cancel();
                }
                return Task.FromResult(chunk);
            }
        };

        Func<Task> download = () => new ModDownloader(staging, source, chunkSize: 10_000, window: 1).DownloadAsync(AllFiles(manifest), null, cancellation.Token);

        await download.Should().ThrowAsync<OperationCanceledException>();
        new FileInfo(ModDownloader.GetPartialPath(staging, ManifestBuilder.Sha(data))).Length.Should().BeGreaterThan(0);
    }

    private static List<ModFile> AllFiles(ModManifest manifest) => manifest.Packages.SelectMany(p => p.Files).ToList();

    private static byte[] RandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need reports in order.</summary>
    private sealed class SynchronousProgress(Action<ModDownloadProgress> handler) : IProgress<ModDownloadProgress>
    {
        public void Report(ModDownloadProgress value) => handler(value);
    }
}
