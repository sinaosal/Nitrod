using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Nitrox.Model.ModSync;

namespace NitroxClient.ModSync;

/// <summary>
///     Reads plugin metadata from BepInEx's chainloader (<c>BepInEx.Bootstrap.Chainloader.PluginInfos</c>), i.e. from the
///     plugins that BepInEx actually discovered in this game session. Uses reflection so Nitrod has no compile-time dependency on
///     BepInEx and keeps working when BepInEx isn't installed.
/// </summary>
public sealed class ChainloaderPluginReader : IPluginMetadataReader
{
    private readonly Dictionary<string, List<ModPluginInfo>> pluginsByAssemblyPath = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBepInExLoaded { get; }
    public string LoaderVersion { get; } = "";
    public IReadOnlyCollection<string> LoadedPluginGuids { get; }

    public ChainloaderPluginReader()
    {
        List<string> guids = [];
        LoadedPluginGuids = guids;
        Type chainloader = FindType("BepInEx.Bootstrap.Chainloader");
        if (chainloader?.GetProperty("PluginInfos", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not IDictionary pluginInfos)
        {
            return;
        }
        IsBepInExLoaded = true;
        LoaderVersion = chainloader.Assembly.GetName().Version?.ToString() ?? "";

        foreach (object pluginInfo in pluginInfos.Values)
        {
            try
            {
                ModPluginInfo plugin = ToPluginInfo(pluginInfo, out string location);
                guids.Add(plugin.Guid);
                if (string.IsNullOrEmpty(location))
                {
                    continue;
                }
                string fullPath = Path.GetFullPath(location);
                if (!pluginsByAssemblyPath.TryGetValue(fullPath, out List<ModPluginInfo> list))
                {
                    pluginsByAssemblyPath[fullPath] = list = [];
                }
                list.Add(plugin);
            }
            catch (Exception ex)
            {
                Log.Warn($"Unable to read BepInEx plugin info: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<ModPluginInfo> ReadPlugins(string assemblyFullPath, string assemblyRelativePath)
    {
        if (!pluginsByAssemblyPath.TryGetValue(Path.GetFullPath(assemblyFullPath), out List<ModPluginInfo> plugins))
        {
            return [];
        }
        List<ModPluginInfo> result = [];
        foreach (ModPluginInfo plugin in plugins)
        {
            result.Add(new ModPluginInfo(plugin.Guid, plugin.Name, plugin.Version, assemblyRelativePath, plugin.Dependencies, plugin.Incompatibilities));
        }
        return result;
    }

    private static ModPluginInfo ToPluginInfo(object pluginInfo, out string location)
    {
        Type type = pluginInfo.GetType();
        object metadata = type.GetProperty("Metadata")?.GetValue(pluginInfo);
        string guid = Get<string>(metadata, "GUID") ?? "";
        string name = Get<string>(metadata, "Name") ?? guid;
        string version = Get<object>(metadata, "Version")?.ToString() ?? "";
        location = Get<string>(pluginInfo, "Location") ?? "";

        List<ModDependencyInfo> dependencies = [];
        if (Get<IEnumerable>(pluginInfo, "Dependencies") is { } dependencyList)
        {
            foreach (object dependency in dependencyList)
            {
                string dependencyGuid = Get<string>(dependency, "DependencyGUID") ?? "";
                // BepInDependency.DependencyFlags.HardDependency = 1
                bool isHard = (Convert.ToInt32(Get<object>(dependency, "Flags") ?? 1) & 1) != 0;
                string minimumVersion = Get<object>(dependency, "MinimumVersion")?.ToString() ?? "";
                if (minimumVersion == "0.0")
                {
                    minimumVersion = "";
                }
                dependencies.Add(new ModDependencyInfo(dependencyGuid, minimumVersion, isHard));
            }
        }

        List<string> incompatibilities = [];
        if (Get<IEnumerable>(pluginInfo, "Incompatibilities") is { } incompatibilityList)
        {
            foreach (object incompatibility in incompatibilityList)
            {
                if (Get<string>(incompatibility, "IncompatibilityGUID") is { } incompatibleGuid)
                {
                    incompatibilities.Add(incompatibleGuid);
                }
            }
        }
        return new ModPluginInfo(guid, name, version, "", dependencies, incompatibilities);
    }

    private static T Get<T>(object instance, string propertyName) where T : class =>
        instance?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as T;

    private static Type FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == "BepInEx" && assembly.GetType(fullName) is { } type)
            {
                return type;
            }
        }
        return null;
    }
}
