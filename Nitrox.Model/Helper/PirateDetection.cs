using System;
using System.IO;
using Nitrox.Model.Platforms.OS.Shared;
using Nitrox.Model.Platforms.Store;

namespace Nitrox.Model.Helper
{
    public static class PirateDetection
    {
        public static bool HasTriggered { get; private set; }

        /// <summary>
        ///     Event that calls subscribers if the pirate detection triggered successfully.
        ///     New subscribers are immediately invoked if the pirate flag has been set at the time of subscription.
        /// </summary>
        public static event EventHandler PirateDetected
        {
            add
            {
                pirateDetected += value;

                if (HasTriggered)
                {
                    value?.Invoke(null, EventArgs.Empty);
                }
            }
            remove => pirateDetected -= value;
        }

        public static bool TriggerOnDirectory(string subnauticaRoot)
        {
#if DEBUG
            return false;
#else
            if (!IsPirateByDirectory(subnauticaRoot))
            {
                return false;
            }

            OnPirateDetected();
            return true;
#endif
        }

        private static event EventHandler pirateDetected;

        private static bool IsPirateByDirectory(string subnauticaRoot)
        {
            if (GamePlatforms.GetPlatformByGameDir(subnauticaRoot) is { } platform && platform is not Steam)
            {
                return false;
            }

            string subdirDll = Path.Combine(subnauticaRoot, GameInfo.Subnautica.DataFolder, "Plugins", "x86_64", "steam_api64.dll");
            if (File.Exists(subdirDll) && !FileSystem.Instance.IsTrustedFile(subdirDll))
            {
                return true;
            }

            string rootDll = Path.Combine(subnauticaRoot, "steam_api64.dll");
            if (File.Exists(rootDll) && !FileSystem.Instance.IsTrustedFile(rootDll))
            {
                return true;
            }

            return false;
        }

        private static void OnPirateDetected()
        {
            pirateDetected?.Invoke(null, EventArgs.Empty);
            HasTriggered = true;
        }
    }
}