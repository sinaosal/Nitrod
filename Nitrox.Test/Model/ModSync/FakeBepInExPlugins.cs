// Minimal stand-ins for BepInEx's attributes so the test assembly itself looks like a BepInEx plugin assembly to
// AssemblyMetadataPluginReader (which matches attributes by namespace + name, without loading BepInEx).
// ReSharper disable All
namespace BepInEx
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    internal sealed class BepInPlugin(string GUID, string Name, string Version) : System.Attribute
    {
        public string GUID { get; } = GUID;
        public string Name { get; } = Name;
        public string Version { get; } = Version;
    }

    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    internal sealed class BepInDependency : System.Attribute
    {
        public enum DependencyFlags
        {
            HardDependency = 1,
            SoftDependency = 2
        }

        public BepInDependency(string DependencyGUID, DependencyFlags Flags = DependencyFlags.HardDependency)
        {
        }

        public BepInDependency(string DependencyGUID, string MinimumDependencyVersion)
        {
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    internal sealed class BepInIncompatibility(string IncompatibilityGUID) : System.Attribute
    {
        public string IncompatibilityGUID { get; } = IncompatibilityGUID;
    }
}

namespace Nitrox.Test.Model.ModSync.FakePlugins
{
    [BepInEx.BepInPlugin("com.nitrod.test.alpha", "Alpha Test Plugin", "1.2.3")]
    [BepInEx.BepInDependency("com.snmodding.nautilus", "1.0.0.50")]
    [BepInEx.BepInDependency("com.nitrod.test.soft", BepInEx.BepInDependency.DependencyFlags.SoftDependency)]
    [BepInEx.BepInDependency("com.nitrod.test.hard")]
    [BepInEx.BepInIncompatibility("com.nitrod.test.enemy")]
    internal sealed class AlphaPlugin;
}
