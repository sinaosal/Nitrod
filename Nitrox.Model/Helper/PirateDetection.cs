using System;

namespace Nitrox.Model.Helper
{
    public static class PirateDetection
    {
        public static bool HasTriggered { get; private set; } = false;

        /// <summary>
        /// Event that calls subscribers if the pirate detection is triggered.
        /// Detection is currently disabled.
        /// </summary>
        public static event EventHandler PirateDetected
        {
            add
            {
                // Detection disabled.
            }
            remove
            {
                // Detection disabled.
            }
        }

        public static bool TriggerOnDirectory(string subnauticaRoot)
        {
            // Pirate detection disabled.
            return false;
        }
    }
}