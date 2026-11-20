using System.Reflection;
using NitroxClient.MonoBehaviours.Gui.MainMenu;
using NitroxClient.MonoBehaviours.Gui.MainMenu.ModSync;
using NitroxClient.MonoBehaviours.Gui.MainMenu.ServerJoin;

namespace NitroxPatcher.Patches.Persistent;

public partial class MainMenuRightSide_OpenGroup_Patch : NitroxPatch, IPersistentPatch
{
    private static readonly MethodInfo TARGET_METHOD = Reflect.Method((MainMenuRightSide t) => t.OpenGroup(default(string)));

    public static void Prefix(string target)
    {
        // Stopping the client if we leave the joining process.
        if (!IsJoinFlowPanel(target))
        {
            JoinServerBackend.StopMultiplayerClient();
        }
    }

    /// <summary>
    ///     Panels shown while joining a server. Opening any other main menu panel cancels the join (and disposes its services),
    ///     so every panel used during joining must be listed here, including the mod sync panel (mod check before the name input).
    /// </summary>
    public static bool IsJoinFlowPanel(string target) =>
        target is MainMenuJoinServerPanel.NAME or MainMenuEnterPasswordPanel.NAME or MainMenuNotificationPanel.NAME or MainMenuModSyncPanel.NAME;
}
