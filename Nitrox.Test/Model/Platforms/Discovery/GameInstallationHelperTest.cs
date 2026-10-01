using Nitrox.Model.Helper;
using Nitrox.Model.Platforms.Discovery.Models;
using Nitrox.Model.Platforms.Store;
using Nitrox.Test.Model.Platforms;

namespace Nitrox.Model.Platforms.Discovery;

[TestClass]
public class GameInstallationHelperTest
{
    private string tempRoot = null!;
    private string gameExecutable = null!;

    [TestInitialize]
    public void Setup()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), $"NitroxGameInstallationTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        gameExecutable = Path.Combine(tempRoot, GameInfo.Subnautica.ExeName);
        File.WriteAllText(gameExecutable, "");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(tempRoot, true);
    }

    [TestMethod]
    public void GetGameDirectory_AcceptsDirectoryAndExecutablePath()
    {
        string expectedGameDirectory = Path.GetFullPath(tempRoot);

        GameInstallationHelper.GetGameDirectory(tempRoot, GameInfo.Subnautica).Should().Be(expectedGameDirectory);
        GameInstallationHelper.GetGameDirectory(gameExecutable, GameInfo.Subnautica).Should().Be(expectedGameDirectory);
    }

    [TestMethod]
    public void DirectLaunch_OverridesSteamPlatformAndRemainsCachedAsStandalone()
    {
        NitroxUser.SetGamePathAndPlatform(tempRoot, new Steam(), true);

        NitroxUser.GamePlatform.Should().BeNull();
        GameInstallationFinder.FindGameCached(GameInfo.Subnautica).Origin.Should().Be(GameLibraries.CONFIG);
    }

    [OSTestMethod(OperatingSystems.Windows)]
    public void EpicInstallation_WithSteamApiPlugin_IsNotDetectedAsPiratedOrSteam()
    {
        string epicStoreDirectory = Path.Combine(tempRoot, ".egstore");
        Directory.CreateDirectory(epicStoreDirectory);
        File.WriteAllText(Path.Combine(epicStoreDirectory, "Subnautica.manifest"), "");

        string pluginDirectory = Path.Combine(tempRoot, GameInfo.Subnautica.DataFolder, "Plugins", "x86_64");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "steam_api64.dll"), "not a signed Steam DLL");

        GamePlatforms.GetPlatformByGameDir(tempRoot).Should().BeOfType<EpicGames>();
        PirateDetection.TriggerOnDirectory(tempRoot).Should().BeFalse();
        PirateDetection.HasTriggered.Should().BeFalse();
    }

    [OSTestMethod(OperatingSystems.Windows)]
    public void MicrosoftStoreInstallation_WithSteamApiPlugin_IsNotDetectedAsPiratedOrSteam()
    {
        File.WriteAllText(Path.Combine(tempRoot, "appxmanifest.xml"), "");

        string pluginDirectory = Path.Combine(tempRoot, GameInfo.Subnautica.DataFolder, "Plugins", "x86_64");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "steam_api64.dll"), "not a signed Steam DLL");

        GamePlatforms.GetPlatformByGameDir(tempRoot).Should().BeOfType<MSStore>();
        PirateDetection.TriggerOnDirectory(tempRoot).Should().BeFalse();
        PirateDetection.HasTriggered.Should().BeFalse();
    }
}