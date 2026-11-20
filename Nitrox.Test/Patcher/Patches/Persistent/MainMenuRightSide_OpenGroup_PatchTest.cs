using NitroxClient.MonoBehaviours.Gui.MainMenu;
using NitroxClient.MonoBehaviours.Gui.MainMenu.ModSync;
using NitroxClient.MonoBehaviours.Gui.MainMenu.ServerJoin;
using NitroxClient.MonoBehaviours.Gui.MainMenu.ServersList;

namespace NitroxPatcher.Patches.Persistent;

[TestClass]
public class MainMenuRightSide_OpenGroup_PatchTest
{
    [TestMethod]
    [DataRow(MainMenuJoinServerPanel.NAME)]
    [DataRow(MainMenuEnterPasswordPanel.NAME)]
    [DataRow(MainMenuNotificationPanel.NAME)]
    // Regression: the mod check opens this panel right after the session policy arrives. When it was missing here, opening it
    // stopped the multiplayer client, disposed the join's services and the mod check crashed (no mod list was ever requested).
    [DataRow(MainMenuModSyncPanel.NAME)]
    public void JoinFlowPanels_DoNotCancelTheJoin(string panel)
    {
        MainMenuRightSide_OpenGroup_Patch.IsJoinFlowPanel(panel).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(MainMenuServerListPanel.NAME)]
    [DataRow("Home")]
    [DataRow("SavedGames")]
    public void OtherPanels_CancelTheJoin(string panel)
    {
        MainMenuRightSide_OpenGroup_Patch.IsJoinFlowPanel(panel).Should().BeFalse();
    }
}
