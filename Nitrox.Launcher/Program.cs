using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Svg.Skia;

namespace Nitrox.Launcher;

internal static class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code before AppMain is called
    // Things aren't initialized yet and stuff might break
    [STAThread]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolver.Handler;
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += AssemblyResolver.Handler;

        if (TryRunApplyPatchWorker(args, out int workerExitCode))
        {
            Environment.Exit(workerExitCode);
            return;
        }

        LoadAvalonia(args);
    }

    /// <summary>
    /// Handles "--apply-patch &lt;subnauticaPath&gt;" by running <see cref="Models.Utils.NitroxEntryPatch.Apply" /> directly
    /// and exiting, without starting the UI. Used so a fatal dnlib crash only kills this throwaway process.
    /// </summary>
    private static bool TryRunApplyPatchWorker(string[] args, out int exitCode)
    {
        exitCode = 0;
        int argIndex = Array.IndexOf(args, Models.Utils.NitroxEntryPatch.APPLY_PATCH_ARG);
        if (argIndex < 0 || argIndex + 1 >= args.Length)
        {
            return false;
        }

        try
        {
            Models.Utils.NitroxEntryPatch.Apply(args[argIndex + 1]).GetAwaiter().GetResult();
        }
        catch
        {
            exitCode = 1;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadAvalonia(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            App.HandleUnhandledException(ex);
        }
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        // https://github.com/wieslawsoltes/Svg.Skia?tab=readme-ov-file#avalonia-previewer
        GC.KeepAlive(typeof(SvgImageExtension).Assembly);
        GC.KeepAlive(typeof(Avalonia.Svg.Skia.Svg).Assembly);

        return App.Create();
    }

    private static class AssemblyResolver
    {
        private static string? currentExecutableDirectory;
        private static readonly Dictionary<string, Assembly> cache = [];

        public static Assembly? Handler(object sender, ResolveEventArgs args)
        {
            static Assembly? ResolveFromLib(ReadOnlySpan<char> dllName)
            {
                dllName = dllName.Slice(0, dllName.IndexOf(','));
                if (!dllName.EndsWith(".dll"))
                {
                    dllName = string.Concat(dllName, ".dll");
                }

                if (dllName.EndsWith(".resources.dll"))
                {
                    return null;
                }

                string dllNameStr = dllName.ToString();

                string dllPath = Path.Combine(GetExecutableDirectory(), "lib", dllNameStr);
                if (!File.Exists(dllPath))
                {
                    dllPath = Path.Combine(GetExecutableDirectory(), dllNameStr);
                }

                try
                {
                    return Assembly.LoadFile(dllPath);
                }
                catch
                {
                    return null;
                }
            }

            if (!cache.TryGetValue(args.Name, out Assembly assembly))
            {
                cache[args.Name] = assembly = ResolveFromLib(args.Name);
                if (assembly == null && !args.Name.Contains(".resources"))
                {
                    cache[args.Name] = assembly = Assembly.Load(args.Name);
                }
            }

            return assembly;
        }

        private static string GetExecutableDirectory()
        {
            if (currentExecutableDirectory != null)
            {
                return currentExecutableDirectory;
            }
            string pathAttempt = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(pathAttempt))
            {
                using Process proc = Process.GetCurrentProcess();
                pathAttempt = proc.MainModule?.FileName;
            }
            return currentExecutableDirectory = new Uri(Path.GetDirectoryName(pathAttempt ?? ".") ?? Directory.GetCurrentDirectory()).LocalPath;
        }
    }
}
