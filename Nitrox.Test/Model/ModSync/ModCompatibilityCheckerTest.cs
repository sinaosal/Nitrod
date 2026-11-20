using Nitrox.Model.ModSync;
using static Nitrox.Test.Model.ModSync.ManifestBuilder;

namespace Nitrox.Test.Model.ModSync;

[TestClass]
public class ModCompatibilityCheckerTest
{
    private static readonly ModFile nautilusDll = File("plugins/Nautilus/Nautilus.dll", "nautilus-1.0.50", ModFileKind.MANAGED_ASSEMBLY);
    private static readonly ModPluginInfo nautilus = Plugin("com.snmodding.nautilus", "1.0.0.50");

    [TestMethod]
    public void IdenticalManifests_AreFullyCompatible()
    {
        ModManifest manifest = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll));

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(manifest, manifest);

        report.IsFullyCompatible.Should().BeTrue();
        report.RequiresSync.Should().BeFalse();
        report.CreatePlan(includeOptional: true).IsEmpty.Should().BeTrue();
    }

    [TestMethod]
    public void MissingMod_RequiresDownloadOfAllFiles()
    {
        ModManifest host = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll, File("plugins/Nautilus/Nautilus.xml", "docs")));

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(host, Manifest());

        report.Packages.Single().Status.Should().Be(PackageSyncStatus.MISSING);
        report.Issues.Should().Contain(i => i.Kind == ModIssueKind.MISSING_MOD && i.Severity == ModIssueSeverity.ERROR && i.ResolvedBySync);
        report.BlockingIssuesAfterSync.Should().BeEmpty();
        ModSyncPlan plan = report.CreatePlan(includeOptional: false);
        plan.Downloads.Select(d => d.RelativePath).Should().BeEquivalentTo("plugins/Nautilus/Nautilus.dll", "plugins/Nautilus/Nautilus.xml");
        plan.Removals.Should().BeEmpty();
    }

    [TestMethod]
    public void DifferentVersion_DownloadsChangedFilesAndRemovesStaleOnes()
    {
        ModManifest host = Manifest(Package("plugins/Nautilus", [Plugin("com.snmodding.nautilus", "1.0.0.51")],
                                            File("plugins/Nautilus/Nautilus.dll", "nautilus-1.0.51", ModFileKind.MANAGED_ASSEMBLY),
                                            File("plugins/Nautilus/Nautilus.xml", "docs")));
        ModManifest local = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll, File("plugins/Nautilus/Nautilus.xml", "docs"),
                                             File("plugins/Nautilus/Removed.dll", "old", ModFileKind.MANAGED_ASSEMBLY)));

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(host, local);

        PackageComparison package = report.Packages.Single();
        package.Status.Should().Be(PackageSyncStatus.DIFFERENT_VERSION);
        package.FilesToDownload.Select(f => f.RelativePath).Should().Equal("plugins/Nautilus/Nautilus.dll");
        package.FilesToRemove.Should().Equal("plugins/Nautilus/Removed.dll");
        report.Issues.Single(i => i.Kind == ModIssueKind.DIFFERENT_VERSION).Detail.Should().Contain("1.0.0.51").And.Contain("1.0.0.50");
    }

    [TestMethod]
    public void SameVersionDifferentFiles_IsReportedAsDifferentFiles()
    {
        ModManifest host = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll, File("plugins/Nautilus/assets/a.bundle", "patched")));
        ModManifest local = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll, File("plugins/Nautilus/assets/a.bundle", "original")));

        ModCompatibilityChecker.Compare(host, local).Packages.Single().Status.Should().Be(PackageSyncStatus.DIFFERENT_FILES);
    }

    [TestMethod]
    public void SameModInDifferentFolder_IsRelocatedNotDuplicated()
    {
        ModManifest host = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll));
        ModManifest local = Manifest(Package("plugins/Nautilus-1.0.0-pre.50", [nautilus], File("plugins/Nautilus-1.0.0-pre.50/Nautilus.dll", "nautilus-1.0.50", ModFileKind.MANAGED_ASSEMBLY)));

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(host, local);

        report.ExtraLocalPackages.Should().BeEmpty("the local copy is the same mod, matched by plugin GUID");
        ModSyncPlan plan = report.CreatePlan(includeOptional: false);
        plan.Downloads.Select(d => d.RelativePath).Should().Equal("plugins/Nautilus/Nautilus.dll");
        plan.Removals.Should().Equal("plugins/Nautilus-1.0.0-pre.50/Nautilus.dll");
        report.Issues.Should().NotContain(i => i.Kind == ModIssueKind.DUPLICATE_PLUGIN);
    }

    [TestMethod]
    public void ExtraLocalMod_IsAWarningOnly()
    {
        ModManifest local = Manifest(Package("plugins/MiniMap", [Plugin("com.example.minimap", "2.0")], File("plugins/MiniMap/MiniMap.dll", "map", ModFileKind.MANAGED_ASSEMBLY)));

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(Manifest(), local);

        report.ExtraLocalPackages.Should().ContainSingle();
        report.Issues.Should().ContainSingle(i => i.Kind == ModIssueKind.EXTRA_MOD && i.Severity == ModIssueSeverity.WARNING);
        report.RequiresSync.Should().BeFalse();
    }

    [TestMethod]
    public void HostMissingDependency_IsBlockingAndNotFixedBySync()
    {
        ModPluginInfo vehicleMod = Plugin("com.example.sub", "1.0", new ModDependencyInfo("com.snmodding.nautilus", "", true));
        ModManifest host = Manifest(Package("plugins/Sub", [vehicleMod], File("plugins/Sub/Sub.dll", "sub", ModFileKind.MANAGED_ASSEMBLY)));

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(host, Manifest());

        ModIssue issue = report.BlockingIssuesAfterSync.Single();
        issue.Kind.Should().Be(ModIssueKind.MISSING_DEPENDENCY);
        issue.Recommendation.Should().Contain("host");
    }

    [TestMethod]
    public void DependencyBelowMinimumVersion_IsReported()
    {
        ModPluginInfo vehicleMod = Plugin("com.example.sub", "1.0", new ModDependencyInfo("com.snmodding.nautilus", "1.0.0.60", true));
        ModManifest host = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll),
                                    Package("plugins/Sub", [vehicleMod], File("plugins/Sub/Sub.dll", "sub", ModFileKind.MANAGED_ASSEMBLY)));

        ModCompatibilityChecker.Compare(host, host).Issues.Should().Contain(i => i.Kind == ModIssueKind.MISSING_DEPENDENCY && i.Detail.Contains("1.0.0.60"));
    }

    [TestMethod]
    public void SoftDependencyAndLoaderProvidedDependency_AreNotErrors()
    {
        ModPluginInfo mod = Plugin("com.example.mod", "1.0", new ModDependencyInfo("com.example.optional", "", false), new ModDependencyInfo("Tobey.BepInEx.Timestamp", "", true));
        ModManifest host = Manifest(Package("plugins/Mod", [mod], File("plugins/Mod/Mod.dll", "m", ModFileKind.MANAGED_ASSEMBLY)));
        ModManifest local = new(ModManifest.CURRENT_FORMAT_VERSION, "83031", "5.4.23.2", [], ["Tobey.BepInEx.Timestamp"]);

        ModCompatibilityChecker.Compare(host, local).Issues.Should().NotContain(i => i.Kind == ModIssueKind.MISSING_DEPENDENCY);
    }

    [TestMethod]
    public void DeclaredIncompatibility_IsBlocking()
    {
        ModPluginInfo a = new("com.example.a", "A", "1", "", [], ["com.example.b"]);
        ModPluginInfo b = Plugin("com.example.b", "1");
        ModManifest host = Manifest(Package("plugins/A", [a], File("plugins/A/A.dll", "a", ModFileKind.MANAGED_ASSEMBLY)),
                                    Package("plugins/B", [b], File("plugins/B/B.dll", "b", ModFileKind.MANAGED_ASSEMBLY)));

        ModCompatibilityChecker.Compare(host, host).BlockingIssuesAfterSync.Should().ContainSingle(i => i.Kind == ModIssueKind.INCOMPATIBLE_MODS);
    }

    [TestMethod]
    public void KnownIncompatibility_IsReportedWithItsReason()
    {
        ModManifest host = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll));
        KnownIncompatibility known = new() { Guid = "com.snmodding.nautilus", Reason = "Test reason", Severity = ModIssueSeverity.WARNING };

        ModCompatibilityChecker.Compare(host, host, [known]).Issues.Should().ContainSingle(i => i.Kind == ModIssueKind.KNOWN_INCOMPATIBILITY && i.Detail == "Test reason");
    }

    [TestMethod]
    public void OptionalMods_AreOnlyIncludedWhenRequested()
    {
        ModPackage optional = Package("plugins/MiniMap", [Plugin("com.example.minimap", "2.0")], File("plugins/MiniMap/MiniMap.dll", "map", ModFileKind.MANAGED_ASSEMBLY)).WithRequirement(ModRequirement.OPTIONAL);
        ModCompatibilityReport report = ModCompatibilityChecker.Compare(Manifest(optional), Manifest());

        report.RequiresSync.Should().BeFalse();
        report.HasOptionalUpdates.Should().BeTrue();
        report.Issues.Single().Severity.Should().Be(ModIssueSeverity.INFO);
        report.CreatePlan(includeOptional: false).IsEmpty.Should().BeTrue();
        report.CreatePlan(includeOptional: true).Downloads.Should().ContainSingle();
    }

    [TestMethod]
    public void GameAndLoaderDifferences_AreExplained()
    {
        ModManifest host = Manifest(Package("plugins/Nautilus", [nautilus], nautilusDll));
        ModManifest local = new(ModManifest.CURRENT_FORMAT_VERSION, "71288", "", [], []);

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(host, local);

        report.Issues.Should().Contain(i => i.Kind == ModIssueKind.GAME_VERSION_MISMATCH && i.Detail.Contains("83031") && i.Detail.Contains("71288"));
        report.BlockingIssuesAfterSync.Should().Contain(i => i.Kind == ModIssueKind.LOADER_MISSING);
    }

    [TestMethod]
    public void NativeCode_NeedsApproval_AndExecutablesAreNeverSynced()
    {
        // Real-world case: VehicleFramework ships Localization/Fix-VFLocalizationKeys.cmd and .ps1 helper scripts.
        ModManifest host = Manifest(Package("plugins/Mod", [], File("plugins/Mod/native.dll", "n", ModFileKind.NATIVE_LIBRARY), File("plugins/Mod/Fix.ps1", "e", ModFileKind.EXECUTABLE)));

        ModCompatibilityReport report = ModCompatibilityChecker.Compare(host, Manifest());

        report.Issues.Should().Contain(i => i.Kind == ModIssueKind.NATIVE_CODE && i.Severity == ModIssueSeverity.WARNING);
        report.Issues.Should().Contain(i => i.Kind == ModIssueKind.EXECUTABLE_FILE && i.Severity == ModIssueSeverity.INFO);
        ModSyncPlan plan = report.CreatePlan(includeOptional: false);
        plan.Downloads.Select(d => d.RelativePath).Should().Equal("plugins/Mod/native.dll");
        plan.ContainsExecutables.Should().BeFalse();
        new ModSyncLimits().Validate(plan).Should().ContainSingle("native code needs explicit approval");
        new ModSyncLimits { AllowNativeLibraries = true }.Validate(plan).Should().BeEmpty();
    }

    [TestMethod]
    public void ExecutablesOnlyOnOneSide_DoNotMakePackagesDiffer()
    {
        ModManifest host = Manifest(Package("plugins/VF", [], File("plugins/VF/VF.dll", "vf", ModFileKind.MANAGED_ASSEMBLY), File("plugins/VF/Fix.cmd", "e", ModFileKind.EXECUTABLE)));
        ModManifest local = Manifest(Package("plugins/VF", [], File("plugins/VF/VF.dll", "vf", ModFileKind.MANAGED_ASSEMBLY)));

        ModCompatibilityChecker.Compare(host, local).Packages.Single().Status.Should().Be(PackageSyncStatus.UP_TO_DATE);
        ModCompatibilityChecker.Compare(local, host).Packages.Single().Status.Should().Be(PackageSyncStatus.UP_TO_DATE, "a player's own scripts are never deleted");
    }

    [TestMethod]
    public void Limits_RejectOversizedAndUnsafePlans()
    {
        ModSyncPlan plan = new()
        {
            Downloads = [new ModFile("plugins/../core/BepInEx.dll", 10, Sha("x"), ModFileKind.DATA), new ModFile("plugins/Big/big.bundle", 2048, Sha("y"), ModFileKind.DATA)],
            Removals = ["config/BepInEx.cfg"]
        };

        List<string> problems = new ModSyncLimits { MaxFileSize = 1024, MaxTotalSize = 1500 }.Validate(plan);

        problems.Should().Contain(p => p.Contains("Unsafe path 'plugins/../core/BepInEx.dll'"));
        problems.Should().Contain(p => p.Contains("Unsafe path 'config/BepInEx.cfg'"));
        problems.Should().Contain(p => p.Contains("per-file limit"));
        problems.Should().Contain(p => p.Contains("more than the limit"));
    }

    [TestMethod]
    [DataRow("1.0.0.50", "1.0.0.50", 0)]
    [DataRow("1.0.0.51", "1.0.0.50", 1)]
    [DataRow("1.0", "1.0.0.1", -1)]
    [DataRow("2.1.0-pre.3", "2.1.0", 0)]
    [DataRow("1.10", "1.9", 1)]
    public void CompareVersions(string a, string b, int expected)
    {
        Math.Sign(ModCompatibilityChecker.CompareVersions(a, b)).Should().Be(expected);
    }
}
