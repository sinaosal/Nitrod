using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autofac;
using HarmonyLib;
using HarmonyLib.Tools;
using NitroxClient;
using NitroxClient.MonoBehaviours;
using Nitrox.Model.Core;
using Nitrox.Model.DataStructures;
using NitroxPatcher.Modules;
using NitroxPatcher.Patches;
using UnityEngine;

namespace NitroxPatcher;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "DIMA001:Dependency Injection container is used directly")]
internal static class Patcher
{
    /// <summary>
    ///     Dependency Injection container used by NitroxPatcher only.
    /// </summary>
    private static IContainer? container;

    private static readonly Harmony harmony = new("com.nitroxmod.harmony");
    private static bool isApplied;

    /// <summary>
    ///     Applies all the dynamic patches defined in Patches namespace.
    ///     Persistent patches are applied when the game initializes (i.e. before main menu).
    ///     See <see cref="Patcher.Initialize"/>.
    /// </summary>
    public static void Apply()
    {
        Validate.NotNull(container, "No patches have been discovered yet! Run Execute() first.");
        if (isApplied)
        {
            return;
        }

        foreach (IDynamicPatch patch in container.Resolve<IDynamicPatch[]>())
        {
            Log.Debug($"Applying dynamic patch {patch.GetType().Name}");
            try
            {
                patch.Patch(harmony);
            }
            catch (HarmonyException e)
            {
                Exception innerMost = e;
                while (innerMost.InnerException != null)
                {
                    innerMost = innerMost.InnerException;
                }
                Log.Error($"Error patching {patch.GetType().Name}{Environment.NewLine}{innerMost}");
            }
            catch (Exception e)
            {
                Log.Error($"Error patching {patch.GetType().Name}{Environment.NewLine}{e}");
            }
        }

        isApplied = true;
        ReportPatchOverlaps();
    }

    public const string HARMONY_OVERLAP_REPORT_FILE_NAME = "nitrod-harmony-overlaps.txt";

    /// <summary>
    ///     Lists game methods patched both by Nitrod and by other mods. These are the first places to look when a mod misbehaves in
    ///     multiplayer: another mod's prefix can skip the code Nitrod relies on to sync, or a transpiler can change it.
    ///     Written next to the logs so the launcher's Diagnostics page can show it.
    /// </summary>
    private static void ReportPatchOverlaps()
    {
        try
        {
            List<string> lines = [];
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
                if (info == null || !info.Owners.Contains(harmony.Id))
                {
                    continue;
                }
                string[] otherOwners = info.Owners.Where(owner => owner != harmony.Id).Distinct().ToArray();
                if (otherOwners.Length == 0)
                {
                    continue;
                }
                List<string> kinds = [];
                if (info.Prefixes.Any(p => p.owner != harmony.Id))
                {
                    kinds.Add("prefix (may skip the original)");
                }
                if (info.Transpilers.Any(p => p.owner != harmony.Id))
                {
                    kinds.Add("transpiler (rewrites the method)");
                }
                if (info.Postfixes.Any(p => p.owner != harmony.Id))
                {
                    kinds.Add("postfix");
                }
                lines.Add($"{method.DeclaringType?.FullName}.{method.Name}: {string.Join(", ", otherOwners)} [{string.Join(", ", kinds)}]");
            }
            lines.Sort(StringComparer.Ordinal);

            string header = $"Methods patched by Nitrod and other mods ({lines.Count}), generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            File.WriteAllLines(Path.Combine(Log.LogDirectory, HARMONY_OVERLAP_REPORT_FILE_NAME), [header, "", ..lines]);
            if (lines.Count > 0)
            {
                Log.Info($"{header}. Details: {HARMONY_OVERLAP_REPORT_FILE_NAME}");
                foreach (string line in lines)
                {
                    Log.Debug(line);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Unable to report Harmony patch overlaps: {ex.Message}");
        }
    }

    /// <summary>
    ///     Removes all the dynamic patches defined by <see cref="NitroxPatcher"/>.
    ///     <p/>
    ///     If the player starts the main menu for the first time, or returns from a (multiplayer) session, get rid of all the
    ///     patches if applicable.
    /// </summary>
    public static void Restore()
    {
        Validate.NotNull(container, "No patches have been discovered yet! Run Execute() first.");
        if (!isApplied)
        {
            return;
        }

        foreach (IDynamicPatch patch in container.Resolve<IDynamicPatch[]>())
        {
            Log.Debug($"Restoring dynamic patch {patch.GetType().Name}");
            patch.Restore(harmony);
        }

        isApplied = false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Initialize()
    {
        if (container != null)
        {
            throw new Exception($"Patches have already been detected! Call {nameof(Apply)} or {nameof(Restore)} instead.");
        }

        Optional.ApplyHasValueCondition<UnityEngine.Object>(o => (bool)o);

        Log.Info("Registering dependencies");
        container = CreatePatchingContainer();
        try
        {
            NitroxServiceLocator.InitializeDependencyContainer(new ClientAutoFacRegistrar());
        }
        catch (ReflectionTypeLoadException ex)
        {
            Log.Error($"Failed to load one or more dependency types for Nitrox. Assembly: {ex.Types.FirstOrDefault()?.Assembly.FullName ?? "unknown"}");
            foreach (Exception loaderEx in ex.LoaderExceptions)
            {
                Log.Error(loaderEx);
            }
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while initializing and loading dependencies.");
            throw;
        }

        InitPatches();
        ApplyNitroxBehaviours();
    }

    private static void InitPatches()
    {
        Log.Info("Patching Subnautica...");

        // Enabling this creates a log file on your desktop (why there?), showing the emitted IL instructions.
        HarmonyFileLog.Enabled = false;

        foreach (IPersistentPatch patch in container.Resolve<IEnumerable<IPersistentPatch>>())
        {
            Log.Debug($"Applying persistent patch {patch.GetType().Name}");
            patch.Patch(harmony);
        }

        Multiplayer.OnBeforeMultiplayerStart += Apply;
        Multiplayer.OnAfterMultiplayerEnd += Restore;
        Log.Info("Completed patching");
    }

    private static IContainer CreatePatchingContainer()
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new NitroxPatchesModule());
        return builder.Build();
    }

    private static void ApplyNitroxBehaviours()
    {
        Log.Info("Applying Nitrox behaviours..");

        GameObject nitroxRoot = new();
        nitroxRoot.name = "Nitrox";
        nitroxRoot.AddComponent<NitroxBootstrapper>();

        Log.Info("Behaviours applied.");
    }
}
