using System.Threading.Tasks;
using Nitrox.Model.ModSync;
using Nitrox.Test.Model.Platforms;

namespace Nitrox.Test.Model.ModSync;

[TestClass]
public class ModSyncStoreTest
{
    [TestMethod]
    public async Task EndToEnd_HostAndClientEndUpWithIdenticalMods()
    {
        using TemporaryGameFolder host = new();
        using TemporaryGameFolder client = new();
        host.WritePluginAssembly("plugins/Alpha/Alpha.dll");
        host.WriteFile("plugins/Alpha/assets/new.bundle", "new asset");
        host.WriteFile("plugins/Shared/readme.txt", "same on both sides");
        client.WriteFile("plugins/Alpha/assets/old.bundle", "old asset");
        client.WriteFile("plugins/Shared/readme.txt", "same on both sides");

        ModManifest hostManifest = host.Scan();
        ModCompatibilityReport before = ModCompatibilityChecker.Compare(hostManifest, client.Scan());
        before.RequiresSync.Should().BeTrue();

        ModSyncStore store = new(client.Root);
        ModSyncPlan plan = before.CreatePlan(includeOptional: false, "Test World", "127.0.0.1:11000");
        await new ModDownloader(store.StagingRoot, new FolderChunkSource(host.BepInEx, hostManifest)).DownloadAsync(plan.Downloads);
        store.SavePending(plan, new ModSyncLimits());
        store.HasPending.Should().BeTrue();

        ModSyncRecord record = store.ApplyPending();

        ModCompatibilityReport after = ModCompatibilityChecker.Compare(hostManifest, client.Scan());
        after.RequiresSync.Should().BeFalse(after.ToText());
        after.Packages.Should().OnlyContain(p => p.Status == PackageSyncStatus.UP_TO_DATE);
        after.ExtraLocalPackages.Should().BeEmpty();
        // The fake test plugin declares hard dependencies neither side has; that is reported identically on both sides.
        after.Issues.Should().OnlyContain(i => i.Kind == ModIssueKind.MISSING_DEPENDENCY);
        store.HasPending.Should().BeFalse();
        record.ServerName.Should().Be("Test World");
        record.Files.Select(f => f.RelativePath).Should().BeEquivalentTo("plugins/Alpha/Alpha.dll", "plugins/Alpha/assets/new.bundle");
        record.Removed.Select(f => f.RelativePath).Should().Equal("plugins/Alpha/assets/old.bundle");
        Directory.GetFiles(store.StagingRoot, "*.blob").Should().BeEmpty("installed blobs are cleaned up");
        store.LoadLedger().Records.Should().ContainSingle();
    }

    [TestMethod]
    public void Apply_BacksUpReplacedFilesAndUninstallRestoresThem()
    {
        using TemporaryGameFolder client = new();
        client.WriteFile("plugins/Mod/data.txt", "player's version");
        client.WriteFile("plugins/Mod/stale.txt", "stale");
        ModSyncStore store = new(client.Root);
        ModSyncPlan plan = Stage(store, ("plugins/Mod/data.txt", "host's version"), ("plugins/Mod/extra.txt", "extra"));
        plan.Removals.Add("plugins/Mod/stale.txt");
        store.SavePending(plan, new ModSyncLimits());

        ModSyncRecord record = store.ApplyPending();

        client.ReadFile("plugins/Mod/data.txt").Should().Be("host's version");
        client.Exists("plugins/Mod/stale.txt").Should().BeFalse();
        record.Files.Single(f => f.RelativePath == "plugins/Mod/data.txt").BackupPath.Should().NotBeEmpty();

        store.Uninstall(record.Id).Should().BeEmpty();

        client.ReadFile("plugins/Mod/data.txt").Should().Be("player's version");
        client.ReadFile("plugins/Mod/stale.txt").Should().Be("stale");
        client.Exists("plugins/Mod/extra.txt").Should().BeFalse();
        store.LoadLedger().Records.Single().Uninstalled.Should().BeTrue();
    }

    [TestMethod]
    public void Uninstall_LeavesFilesThePlayerChangedAfterwards()
    {
        using TemporaryGameFolder client = new();
        ModSyncStore store = new(client.Root);
        store.SavePending(Stage(store, ("plugins/NewMod/NewMod.txt", "synced")), new ModSyncLimits());
        ModSyncRecord record = store.ApplyPending();
        client.WriteFile("plugins/NewMod/NewMod.txt", "edited by the player");

        store.Uninstall(record.Id).Should().Equal("plugins/NewMod/NewMod.txt");

        client.ReadFile("plugins/NewMod/NewMod.txt").Should().Be("edited by the player");
    }

    [TestMethod]
    public void Uninstall_RemovesFoldersItCreated()
    {
        using TemporaryGameFolder client = new();
        ModSyncStore store = new(client.Root);
        store.SavePending(Stage(store, ("plugins/NewMod/sub/file.txt", "synced")), new ModSyncLimits());
        ModSyncRecord record = store.ApplyPending();

        store.Uninstall(record.Id);

        Directory.Exists(Path.Combine(client.BepInEx, "plugins", "NewMod")).Should().BeFalse();
        Directory.Exists(Path.Combine(client.BepInEx, "plugins")).Should().BeTrue("plugins itself must never be deleted");
    }

    [TestMethod]
    public void Apply_RefusesTamperedStagedFiles()
    {
        using TemporaryGameFolder client = new();
        ModSyncStore store = new(client.Root);
        store.SavePending(Stage(store, ("plugins/Mod/Mod.txt", "genuine")), new ModSyncLimits());
        File.WriteAllText(ModDownloader.GetBlobPath(store.StagingRoot, ManifestBuilder.Sha("genuine")), "tampered");

        Invoking(store.ApplyPending).Should().Throw<ModInstallException>().WithMessage("*modified*");
        client.Exists("plugins/Mod/Mod.txt").Should().BeFalse();
    }

    [TestMethod]
    public void Apply_RejectsHandEditedPendingFileWithTraversal()
    {
        using TemporaryGameFolder client = new();
        ModSyncStore store = new(client.Root);
        store.SavePending(Stage(store, ("plugins/Mod/Mod.txt", "genuine")), new ModSyncLimits());
        File.WriteAllText(store.PendingFilePath, File.ReadAllText(store.PendingFilePath).Replace("plugins/Mod/Mod.txt", "plugins/../../evil.txt"));

        Invoking(store.ApplyPending).Should().Throw<InvalidDataException>();
        File.Exists(Path.Combine(client.Root, "evil.txt")).Should().BeFalse();
    }

    [TestMethod]
    public void SavePending_RequiresApprovalForExecutables()
    {
        using TemporaryGameFolder client = new();
        ModSyncStore store = new(client.Root);
        ModSyncPlan plan = Stage(store, ("plugins/Mod/setup.exe", "MZ"));
        plan.Downloads[0] = new ModFile(plan.Downloads[0].RelativePath, plan.Downloads[0].Size, plan.Downloads[0].Sha256, ModFileKind.EXECUTABLE);

        Invoking(() => store.SavePending(plan, new ModSyncLimits())).Should().Throw<ModInstallException>().WithMessage("*not approved*");
        store.SavePending(plan, new ModSyncLimits { AllowExecutables = true });
        store.ApplyPending().Files.Should().ContainSingle();
    }

    [OSTestMethod(OperatingSystems.Windows)]
    public void Apply_RollsBackWhenAFileIsInUse()
    {
        using TemporaryGameFolder client = new();
        client.WriteFile("plugins/Mod/a.txt", "old a");
        string lockedPath = client.WriteFile("plugins/Mod/b.dll", "old b");
        ModSyncStore store = new(client.Root);
        store.SavePending(Stage(store, ("plugins/Mod/a.txt", "new a"), ("plugins/Mod/b.dll", "new b")), new ModSyncLimits());

        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Invoking(store.ApplyPending).Should().Throw<ModInstallException>().WithMessage("*rolled back*");
        }

        client.ReadFile("plugins/Mod/a.txt").Should().Be("old a", "the already-replaced file must be restored");
        client.ReadFile("plugins/Mod/b.dll").Should().Be("old b");
        store.HasPending.Should().BeTrue("the install can be retried once the game is closed");
        store.LoadLedger().Records.Should().BeEmpty();
    }

    [TestMethod]
    public void DiscardPending_RemovesPlanAndStagedFiles()
    {
        using TemporaryGameFolder client = new();
        ModSyncStore store = new(client.Root);
        store.SavePending(Stage(store, ("plugins/Mod/Mod.txt", "x")), new ModSyncLimits());

        store.DiscardPending();

        store.HasPending.Should().BeFalse();
        Directory.GetFiles(store.StagingRoot).Should().BeEmpty();
    }

    /// <summary>Writes blobs into the staging folder as if they had been downloaded and returns the matching plan.</summary>
    private static ModSyncPlan Stage(ModSyncStore store, params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(store.StagingRoot);
        ModSyncPlan plan = new() { ServerName = "Test", ServerAddress = "localhost" };
        foreach ((string path, string content) in files)
        {
            ModFile file = ManifestBuilder.File(path, content);
            File.WriteAllText(ModDownloader.GetBlobPath(store.StagingRoot, file.Sha256), content);
            plan.Downloads.Add(file);
        }
        return plan;
    }
}
