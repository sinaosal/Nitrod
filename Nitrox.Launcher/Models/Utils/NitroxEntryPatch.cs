using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Nitrox.Model.Logger;
using Nitrox.Model.Platforms.OS.Shared;

namespace Nitrox.Launcher.Models.Utils;

public static class NitroxEntryPatch
{
    public const string GAME_ASSEMBLY_NAME = "Assembly-CSharp.dll";
    public const string NITROX_ASSEMBLY_NAME = "NitroxPatcher.dll";
    public const string GAME_ASSEMBLY_MODIFIED_NAME = "Assembly-CSharp-Nitrox.dll";

    /// <summary>
    /// Command-line flag handled by <c>Program.Main</c> to run <see cref="Apply"/> as an isolated worker process.
    /// </summary>
    public const string APPLY_PATCH_ARG = "--apply-patch";

    private const string NITROX_ENTRY_TYPE_NAME = "Main";
    private const string NITROX_ENTRY_METHOD_NAME = "Execute";

    private const string TARGET_TYPE_NAME = "PlatformUtils";
    private const string TARGET_METHOD_NAME = "Awake";

    private const string NITROX_EXECUTE_INSTRUCTION = "System.Void NitroxPatcher.Main::Execute()";

    // Written synchronously (bypassing the buffered log sink) so the last step is on disk even if the process dies instantly.
    internal static void TraceStep(string step)
    {
        try
        {
            File.AppendAllText(Path.Combine(Log.LogDirectory, "nitrox-patch-trace.log"), $"{DateTime.Now:O} {step}{Environment.NewLine}");
        }
        catch
        {
            // Tracing must never be the cause of a failure.
        }
    }

    /// <summary>
    /// Runs <see cref="Apply"/> in a separate child process so a fatal/uncatchable crash while dnlib parses or
    /// rewrites a non-standard Assembly-CSharp.dll (e.g. obfuscated or hand-modified) cannot take down the launcher.
    /// </summary>
    /// <returns>The worker process exit code: 0 on success, 1 on a normal caught exception, anything else indicates a fatal/native crash in the worker.</returns>
    public static async Task<int> ApplyInIsolatedProcessAsync(string subnauticaBasePath)
    {
        string launcherExe = Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Unable to determine launcher executable path.");

        TraceStep($"Spawning isolated patch worker for '{subnauticaBasePath}'");
        ProcessStartInfo startInfo = new(launcherExe) { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add(APPLY_PATCH_ARG);
        startInfo.ArgumentList.Add(subnauticaBasePath);

        using Process worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start isolated patch worker process.");
        await worker.WaitForExitAsync();
        TraceStep($"Isolated patch worker exited with code {worker.ExitCode}");
        return worker.ExitCode;
    }

    /// <summary>
    /// Inject Nitrox entry point into Subnautica's Assembly-CSharp.dll
    /// </summary>
    public static async Task Apply(string subnauticaBasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(subnauticaBasePath, nameof(subnauticaBasePath));
        TraceStep("Apply() entered");

        string subnauticaManagedPath = Path.Combine(subnauticaBasePath, GameInfo.Subnautica.DataFolder, "Managed");
        string assemblyCSharp = Path.Combine(subnauticaManagedPath, GAME_ASSEMBLY_NAME);
        string nitroxPatcherPath = Path.Combine(subnauticaManagedPath, NITROX_ASSEMBLY_NAME);
        string modifiedAssemblyCSharp = Path.Combine(subnauticaManagedPath, GAME_ASSEMBLY_MODIFIED_NAME);

        Log.Debug("Checking Subnautica code exists");

        if (File.Exists(modifiedAssemblyCSharp))
        {
            // Avoid the case where AssemblyCSharp.dll get wiped and the only file left is AssemblyCSharp-Nitrox.dll
            if (!File.Exists(assemblyCSharp))
            {
                Log.Error($"Invalid state, {GAME_ASSEMBLY_NAME} not found, but {GAME_ASSEMBLY_MODIFIED_NAME} exists. Please verify your installation.");
                FileSystem.Instance.ReplaceFile(modifiedAssemblyCSharp, assemblyCSharp);
            }
            else
            {
                Log.Debug($"{GAME_ASSEMBLY_MODIFIED_NAME} already exists, removing it");
                Exception copyError = RetryWait(() => TryDeleteFile(modifiedAssemblyCSharp), 100, 5);
                if (copyError != null)
                {
                    throw copyError;
                }
            }
        }

        byte[] cachedSha256ForFile = await Hashing.GetCachedSha256ByFilePath(assemblyCSharp);
        byte[] currentCodeFileSha256 = await Hashing.GetSha256(assemblyCSharp);
        if (cachedSha256ForFile.SequenceEqual(currentCodeFileSha256))
        {
            Log.Info("Subnautica already has Nitrox entry patch");
            return;
        }

        Log.Debug($"Adding Nitrox entry point to Subnautica because code file hash mismatch [{Convert.ToHexStringLower(cachedSha256ForFile)}] != [{Convert.ToHexStringLower(currentCodeFileSha256)}]");

        /*
         * private void Awake()
         * {
         *     NitroxPatcher.Main.Execute(); <--- [INSERTED LINE]
         *     if (PlatformUtils._main != null)
         *     {
         *         Debug.LogError("Multiple PlatformUtils instances found in scene!", this);
         *         Debug.Break();
         *         global::UnityEngine.Object.DestroyImmediate(base.gameObject);
         *         return;
         *     }
         *     PlatformUtils._main = this;
         *     global::UnityEngine.Object.DontDestroyOnLoad(base.gameObject);
         *     base.StartCoroutine(this.PlatformInitAsync());
         * }
        */
        // TODO: Find a better way to inject Nitrox entrypoint instead of using file swapping
        TraceStep("Before ModuleDefMD.Load(assemblyCSharp)");
        using (ModuleDefMD module = ModuleDefMD.Load(assemblyCSharp))
        {
            TraceStep("Before ModuleDefMD.Load(nitroxPatcherPath)");
            using ModuleDefMD nitroxPatcherAssembly = ModuleDefMD.Load(nitroxPatcherPath);
            TraceStep("Loaded both modules");

            TypeDef nitroxMainDefinition = nitroxPatcherAssembly.GetTypes().FirstOrDefault(x => x.Name == NITROX_ENTRY_TYPE_NAME);
            MethodDef executeMethodDefinition = nitroxMainDefinition.Methods.FirstOrDefault(x => x.Name == NITROX_ENTRY_METHOD_NAME);

            TraceStep("Before module.Import(executeMethodDefinition)");
            MemberRef executeMethodReference = module.Import(executeMethodDefinition);

            TraceStep("Before resolving target type/method");
            TypeDef gameInputType = module.GetTypes().First(x => x.FullName == TARGET_TYPE_NAME);
            MethodDef awakeMethod = gameInputType.Methods.First(x => x.Name == TARGET_METHOD_NAME);

            Instruction callNitroxExecuteInstruction = OpCodes.Call.ToInstruction(executeMethodReference);

            if (awakeMethod.Body.Instructions[0].Operand is MemberRef refA && callNitroxExecuteInstruction.Operand is MemberRef refB && refA.FullName == refB.FullName)
            {
                Log.Warn("Nitrox entry point already patched.");
                TraceStep("Already patched, returning");
                return;
            }

            TraceStep("Before Instructions.Insert");
            awakeMethod.Body.Instructions.Insert(0, callNitroxExecuteInstruction);

            TraceStep("Before module.Write (highest-risk dnlib step)");
            module.Write(modifiedAssemblyCSharp);
            TraceStep("After module.Write");

            Log.Debug($"Writing assembly to {GAME_ASSEMBLY_MODIFIED_NAME}");
            File.SetAttributes(assemblyCSharp, System.IO.FileAttributes.Normal);
        }
        TraceStep("After using-block disposed modules");

        // The assembly might be used by other code or some other program might work in it. Retry to be on the safe side.
        Log.Debug($"Deleting {GAME_ASSEMBLY_NAME}");
        Exception? error = RetryWait(() => TryDeleteFile(assemblyCSharp), 100, 5);
        if (error != null)
        {
            throw error;
        }

        FileSystem.Instance.ReplaceFile(modifiedAssemblyCSharp, assemblyCSharp);
        Log.Debug("Added Nitrox entry point to Subnautica");

        Log.Debug("Storing SHA256 of Nitrox-mutated code file in cache");
        Log.Debug($"Code file SHA256: {Convert.ToHexStringLower(await Hashing.GetAndStoreSha256ForFile(assemblyCSharp))}");
    }

    /// <summary>
    /// Remote Nitrox entry point from Subnautica's Assembly-CSharp.dll
    /// </summary>
    public static void Remove(string subnauticaBasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(subnauticaBasePath, nameof(subnauticaBasePath));

        Log.Debug("Removing Nitrox entry point from Subnautica");

        string subnauticaManagedPath = Path.Combine(subnauticaBasePath, GameInfo.Subnautica.DataFolder, "Managed");
        string assemblyCSharp = Path.Combine(subnauticaManagedPath, GAME_ASSEMBLY_NAME);
        string modifiedAssemblyCSharp = Path.Combine(subnauticaManagedPath, GAME_ASSEMBLY_MODIFIED_NAME);

        using (ModuleDefMD module = ModuleDefMD.Load(assemblyCSharp))
        {
            TypeDef gameInputType = module.GetTypes().First(x => x.FullName == TARGET_TYPE_NAME);
            MethodDef awakeMethod = gameInputType.Methods.First(x => x.Name == TARGET_METHOD_NAME);

            IList<Instruction> methodInstructions = awakeMethod.Body.Instructions;
            int nitroxExecuteInstructionIndex = FindNitroxExecuteInstructionIndex(methodInstructions);
            if (nitroxExecuteInstructionIndex == -1)
            {
                Log.Debug($"Nitrox entry point not found in {TARGET_TYPE_NAME}:{TARGET_METHOD_NAME}");
                return;
            }
            do
            {
                methodInstructions.RemoveAt(nitroxExecuteInstructionIndex);
            } while ((nitroxExecuteInstructionIndex = FindNitroxExecuteInstructionIndex(methodInstructions)) >= 0);
            module.Write(modifiedAssemblyCSharp);

            File.SetAttributes(assemblyCSharp, System.IO.FileAttributes.Normal);
        }

        FileSystem.Instance.ReplaceFile(modifiedAssemblyCSharp, assemblyCSharp);
        Log.Debug("Removed Nitrox entry point from Subnautica");
    }

    private static int FindNitroxExecuteInstructionIndex(IList<Instruction> methodInstructions)
    {
        for (int instructionIndex = 0; instructionIndex < methodInstructions.Count; instructionIndex++)
        {
            string instruction = methodInstructions[instructionIndex].Operand?.ToString();

            if (instruction == NITROX_EXECUTE_INSTRUCTION)
            {
                return instructionIndex;
            }
        }

        return -1;
    }

    private static Exception? RetryWait(Action action, int interval, int retries = 0)
    {
        Exception lastException = null;
        while (retries >= 0)
        {
            try
            {
                retries--;
                action();
                return null;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Task.Delay(interval).Wait();
            }
        }
        return lastException;
    }

    public static bool IsPatchApplied(string subnauticaBasePath)
    {
        string subnauticaManagedPath = Path.Combine(subnauticaBasePath, GameInfo.Subnautica.DataFolder, "Managed");
        string gameInputPath = Path.Combine(subnauticaManagedPath, GAME_ASSEMBLY_NAME);

        using (ModuleDefMD module = ModuleDefMD.Load(gameInputPath))
        {
            TypeDef gameInputType = module.GetTypes().First(x => x.FullName == TARGET_TYPE_NAME);
            MethodDef awakeMethod = gameInputType.Methods.First(x => x.Name == TARGET_METHOD_NAME);

            return awakeMethod.Body.Instructions[0]?.ToString() == NITROX_EXECUTE_INSTRUCTION;
        }
    }
}
