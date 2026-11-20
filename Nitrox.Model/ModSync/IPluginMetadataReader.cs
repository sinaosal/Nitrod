using System.Collections.Generic;

namespace Nitrox.Model.ModSync;

/// <summary>
///     Reads BepInEx plugin metadata (GUID, name, version, dependencies) for an assembly.
/// </summary>
/// <remarks>
///     Desktop processes (server, launcher) read the attributes from assembly metadata without loading the assembly
///     (<see cref="AssemblyMetadataPluginReader" />). Inside the game, the client asks BepInEx's chainloader instead, which
///     reflects what actually loaded.
/// </remarks>
public interface IPluginMetadataReader
{
    /// <param name="assemblyFullPath">Absolute path of a .NET assembly.</param>
    /// <param name="assemblyRelativePath">Path relative to the BepInEx folder (forward slashes), stored in the result.</param>
    IReadOnlyList<ModPluginInfo> ReadPlugins(string assemblyFullPath, string assemblyRelativePath);
}

/// <summary>Reader that reports no plugins. Packages are then compared by their files only.</summary>
public sealed class NullPluginMetadataReader : IPluginMetadataReader
{
    public static readonly NullPluginMetadataReader Instance = new();

    public IReadOnlyList<ModPluginInfo> ReadPlugins(string assemblyFullPath, string assemblyRelativePath) => [];
}
