using Nitrox.Model.ModSync;

namespace Nitrox.Test.Model.ModSync;

[TestClass]
public class ModScannerTest
{
    [TestMethod]
    public void Scan_GroupsFilesIntoPackagesAndReadsPluginMetadata()
    {
        using TemporaryGameFolder game = new();
        game.WritePluginAssembly("plugins/Alpha/Alpha.dll");
        game.WriteFile("plugins/Alpha/assets/bundle", "asset-data");
        game.WriteFile("plugins/Loose.txt", "loose");

        ModManifest manifest = game.Scan();

        manifest.IsLoaderInstalled.Should().BeTrue();
        manifest.Packages.Select(p => p.Id).Should().BeEquivalentTo("plugins/Alpha", "plugins/Loose.txt");

        ModPackage alpha = manifest.Packages.Single(p => p.Id == "plugins/Alpha");
        alpha.Files.Select(f => f.RelativePath).Should().BeEquivalentTo("plugins/Alpha/Alpha.dll", "plugins/Alpha/assets/bundle");
        alpha.Files.Single(f => f.RelativePath.EndsWith(".dll")).Kind.Should().Be(ModFileKind.MANAGED_ASSEMBLY);
        alpha.Files.Single(f => f.RelativePath.EndsWith("bundle")).Sha256.Should().Be(ManifestBuilder.Sha("asset-data"));

        ModPluginInfo plugin = alpha.Plugins.Single(p => p.Guid == "com.nitrod.test.alpha");
        plugin.Name.Should().Be("Alpha Test Plugin");
        plugin.Version.Should().Be("1.2.3");
        plugin.AssemblyPath.Should().Be("plugins/Alpha/Alpha.dll");
        plugin.Dependencies.Should().ContainEquivalentOf(new ModDependencyInfo("com.snmodding.nautilus", "1.0.0.50", true));
        plugin.Dependencies.Should().ContainEquivalentOf(new ModDependencyInfo("com.nitrod.test.soft", "", false));
        plugin.Dependencies.Should().ContainEquivalentOf(new ModDependencyInfo("com.nitrod.test.hard", "", true));
        plugin.Incompatibilities.Should().Equal("com.nitrod.test.enemy");
        alpha.DisplayName.Should().Be("Alpha Test Plugin");
    }

    [TestMethod]
    public void Scan_SkipsArchivesUserSettingsAndLoaderHelpers()
    {
        using TemporaryGameFolder game = new();
        game.WriteFile("plugins/Nautilus.zip", "zip");
        game.WriteFile("plugins/Mod/config.json", "{ \"volume\": 1 }");
        game.WriteFile("plugins/Mod/data.json", "{}");
        game.WritePluginAssembly("plugins/.tobey.bepinex.pack/Helper.dll");

        ModManifest manifest = game.Scan();

        manifest.Packages.Select(p => p.Id).Should().Equal("plugins/Mod");
        manifest.Packages[0].Files.Select(f => f.RelativePath).Should().Equal("plugins/Mod/data.json");
        manifest.ProvidedPluginGuids.Should().Contain("com.nitrod.test.alpha", "plugins in hidden loader folders still satisfy dependencies");
    }

    [TestMethod]
    public void Scan_AppliesOptionalAndExcludedPackages()
    {
        using TemporaryGameFolder game = new();
        game.WritePluginAssembly("plugins/Alpha/Alpha.dll");
        game.WriteFile("plugins/HostTool/tool.txt", "host only");
        game.WriteFile("plugins/MiniMap/map.txt", "ui");

        ModManifest manifest = game.Scan(new ModScanOptions
        {
            OptionalPackages = ModScanOptions.ParseList("MiniMap; com.nitrod.test.alpha"),
            ExcludedPackages = ModScanOptions.ParseList("plugins/HostTool")
        });

        manifest.Packages.Select(p => p.Id).Should().BeEquivalentTo("plugins/Alpha", "plugins/MiniMap");
        manifest.Packages.Should().OnlyContain(p => p.Requirement == ModRequirement.OPTIONAL);
    }

    [TestMethod]
    public void Scan_WithoutBepInEx_ReturnsEmptyManifest()
    {
        using TemporaryGameFolder game = new(installLoader: false);
        Directory.Delete(game.BepInEx, true);

        ModManifest manifest = game.Scan();

        manifest.Packages.Should().BeEmpty();
        manifest.IsLoaderInstalled.Should().BeFalse();
    }

    [TestMethod]
    public void HashCache_ReusesHashesUntilFileChanges()
    {
        using TemporaryGameFolder game = new();
        string file = game.WriteFile("plugins/Mod/data.bin", "one");
        string cachePath = Path.Combine(game.Root, "cache.json");

        new ModFileHasher(cachePath).GetSha256(file).Should().Be(ManifestBuilder.Sha("one"));
        File.Exists(cachePath).Should().BeFalse("the cache is only written when saved");

        ModFileHasher hasher = new(cachePath);
        hasher.GetSha256(file);
        hasher.SaveCache();
        File.Exists(cachePath).Should().BeTrue();

        File.WriteAllText(file, "two, longer");
        new ModFileHasher(cachePath).GetSha256(file).Should().Be(ManifestBuilder.Sha("two, longer"), "a changed size/timestamp must invalidate the cached hash");
    }
}
