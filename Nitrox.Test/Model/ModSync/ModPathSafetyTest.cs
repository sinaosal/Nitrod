using Nitrox.Model.ModSync;
using Nitrox.Test.Model.Platforms;

namespace Nitrox.Test.Model.ModSync;

[TestClass]
public class ModPathSafetyTest
{
    [TestMethod]
    [DataRow("plugins/Nautilus/Nautilus.dll")]
    [DataRow("plugins/MyMod.dll")]
    [DataRow("patchers/Tool/Tool.dll")]
    [DataRow("plugins/R5Submersible/AutoPilotVoices/R5-English/Goodbye.ogg")]
    [DataRow("PLUGINS/Upper Case/file name with spaces.txt")]
    public void SafePaths_AreAccepted(string path)
    {
        ModPathSafety.IsSafeRelativePath(path, out string reason).Should().BeTrue(reason);
    }

    [TestMethod]
    [DataRow("", DisplayName = "empty")]
    [DataRow("plugins", DisplayName = "root only")]
    [DataRow("../plugins/x.dll", DisplayName = "leading traversal")]
    [DataRow("plugins/../core/BepInEx.dll", DisplayName = "inner traversal")]
    [DataRow("plugins/./x.dll", DisplayName = "dot segment")]
    [DataRow("plugins//x.dll", DisplayName = "empty segment")]
    [DataRow("/plugins/x.dll", DisplayName = "absolute unix")]
    [DataRow("C:/Windows/x.dll", DisplayName = "drive letter")]
    [DataRow("plugins\\..\\core\\x.dll", DisplayName = "backslashes")]
    [DataRow("plugins/x.dll:evil", DisplayName = "alternate data stream")]
    [DataRow("core/BepInEx.dll", DisplayName = "BepInEx core")]
    [DataRow("config/x.cfg", DisplayName = "config folder")]
    [DataRow("NitrodSync/pending.json", DisplayName = "own folder root")]
    [DataRow("plugins/NitrodSync/x.dll", DisplayName = "own folder nested")]
    [DataRow("plugins/CON/x.dll", DisplayName = "reserved device")]
    [DataRow("plugins/nul.txt", DisplayName = "reserved device with extension")]
    [DataRow("plugins/mod./x.dll", DisplayName = "trailing dot")]
    [DataRow("plugins/mod /x.dll", DisplayName = "trailing space")]
    [DataRow("plugins/x\u0001.dll", DisplayName = "control character")]
    [DataRow("plugins/a*b.dll", DisplayName = "wildcard")]
    public void UnsafePaths_AreRejected(string path)
    {
        ModPathSafety.IsSafeRelativePath(path, out string reason).Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void TooLongPath_IsRejected()
    {
        string path = $"plugins/{new string('a', 100)}/{new string('b', 100)}/{new string('c', 100)}.dll";
        ModPathSafety.IsSafeRelativePath(path, out _).Should().BeFalse();
    }

    [TestMethod]
    public void ResolveInside_StaysInsideBepInEx()
    {
        using TemporaryGameFolder game = new();
        string resolved = ModPathSafety.ResolveInside(game.BepInEx, "plugins/Mod/Mod.dll");
        resolved.Should().Be(Path.Combine(game.BepInEx, "plugins", "Mod", "Mod.dll"));

        Invoking(() => ModPathSafety.ResolveInside(game.BepInEx, "plugins/../../escape.dll")).Should().Throw<InvalidDataException>();
    }

    [OSTestMethod(OperatingSystems.Windows)]
    public void ResolveInside_RejectsJunctionEscapes()
    {
        using TemporaryGameFolder game = new();
        string outside = Path.Combine(Path.GetTempPath(), $"NitrodJunctionTarget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            string link = Path.Combine(game.BepInEx, "plugins", "Linked");
            // Junctions don't need admin rights or developer mode, unlike symbolic links.
            using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            {
                process.WaitForExit();
            }
            Directory.Exists(link).Should().BeTrue("the junction should have been created for this test");

            Invoking(() => ModPathSafety.ResolveInside(game.BepInEx, "plugins/Linked/payload.dll")).Should().Throw<InvalidDataException>().WithMessage("*symbolic link or junction*");
            Directory.Delete(link);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [TestMethod]
    public void ClassifyFile_DetectsManagedNativeExecutableAndData()
    {
        using TemporaryGameFolder game = new();
        string managed = game.WritePluginAssembly("plugins/Managed/Managed.dll");
        string notAPeFile = game.WriteFile("plugins/Fake/Fake.dll", "this is not a portable executable");
        string executable = game.WriteFile("plugins/Tool/run.exe", "MZ");
        string script = game.WriteFile("plugins/Tool/install.ps1", "Remove-Item C:\\");
        string data = game.WriteFile("plugins/Mod/assets/readme.txt", "hello");
        string nativeSharedObject = game.WriteFile("plugins/Mod/libnative.so", "\u007fELF");

        ModPathSafety.ClassifyFile(managed).Should().Be(ModFileKind.MANAGED_ASSEMBLY);
        ModPathSafety.ClassifyFile(notAPeFile).Should().Be(ModFileKind.NATIVE_LIBRARY, "unrecognized DLLs must be treated as native code, never as safe data");
        ModPathSafety.ClassifyFile(executable).Should().Be(ModFileKind.EXECUTABLE);
        ModPathSafety.ClassifyFile(script).Should().Be(ModFileKind.EXECUTABLE);
        ModPathSafety.ClassifyFile(data).Should().Be(ModFileKind.DATA);
        ModPathSafety.ClassifyFile(nativeSharedObject).Should().Be(ModFileKind.NATIVE_LIBRARY);
    }

    [OSTestMethod(OperatingSystems.Windows)]
    public void ClassifyFile_DetectsRealNativeDll()
    {
        string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        ModPathSafety.ClassifyFile(kernel32).Should().Be(ModFileKind.NATIVE_LIBRARY);
    }

    [TestMethod]
    [DataRow("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", true)]
    [DataRow("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", false)]
    [DataRow("../../../../etc/passwd", false)]
    [DataRow("", false)]
    public void IsValidSha256(string value, bool expected)
    {
        ModPathSafety.IsValidSha256(value).Should().Be(expected);
    }
}
