using System.IO.Compression;
using Nitrox.Launcher.Models.Utils;

namespace Nitrox.Test.Launcher.Models.Utils;

[TestClass]
public class BepInExModInstallerTest
{
    [TestMethod]
    public void Install_FlatModArchive_InstallsIntoDedicatedPluginDirectory()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            string archivePath = Path.Combine(temporaryDirectory, "ExampleMod.zip");
            CreateArchive(archivePath, "ExampleMod.dll", "config.json");

            int installedFiles = BepInExModInstaller.Install(archivePath, temporaryDirectory);

            Assert.AreEqual(2, installedFiles);
            Assert.IsTrue(File.Exists(Path.Combine(temporaryDirectory, "BepInEx", "plugins", "ExampleMod", "ExampleMod.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(temporaryDirectory, "BepInEx", "plugins", "ExampleMod", "config.json")));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Install_ArchiveWithModFolder_PreservesFolderWithoutZipNameWrapper()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            string archivePath = Path.Combine(temporaryDirectory, "Vehicle Framework.zip");
            CreateArchive(archivePath, "VehicleFramework/VehicleFramework.dll", "VehicleFramework/config.json");

            int installedFiles = BepInExModInstaller.Install(archivePath, temporaryDirectory);

            Assert.AreEqual(2, installedFiles);
            Assert.IsTrue(File.Exists(Path.Combine(temporaryDirectory, "BepInEx", "plugins", "VehicleFramework", "VehicleFramework.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(temporaryDirectory, "BepInEx", "plugins", "VehicleFramework", "config.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(temporaryDirectory, "BepInEx", "plugins", "Vehicle Framework")));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Install_ArchiveWithTraversalPath_RejectsWithoutWritingOutsideGameDirectory()
    {
        string temporaryDirectory = CreateTemporaryDirectory();
        string escapedFilePath = Path.Combine(Directory.GetParent(temporaryDirectory)!.FullName, "escaped-mod.dll");
        try
        {
            string archivePath = Path.Combine(temporaryDirectory, "UnsafeMod.zip");
            CreateArchive(archivePath, "../../escaped-mod.dll");

            Assert.Throws<InvalidDataException>(() => BepInExModInstaller.Install(archivePath, temporaryDirectory));
            Assert.IsFalse(File.Exists(escapedFilePath));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
            if (File.Exists(escapedFilePath))
            {
                File.Delete(escapedFilePath);
            }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"NitrodModInstallerTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateArchive(string archivePath, params string[] entryNames)
    {
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (string entryName in entryNames)
        {
            using StreamWriter writer = new(archive.CreateEntry(entryName).Open());
            writer.Write("test");
        }
    }
}