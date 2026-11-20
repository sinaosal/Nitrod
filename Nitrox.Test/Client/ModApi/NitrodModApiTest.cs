using Nitrox.Model.Packets;
using NitroxClient.ModApi;

namespace Nitrox.Test.Client.ModApi;

[TestClass]
public class NitrodModApiTest
{
    [TestMethod]
    public void Bridge_FindsApiAndReceivesDispatchedMessages()
    {
        _ = typeof(NitrodModApi).Assembly; // Make sure NitroxClient is loaded, as it is in the game.
        List<(string Name, ushort Id, byte[] Payload)> received = [];

        NitrodBridge.IsAvailable.Should().BeTrue();
        NitrodBridge.RegisterHandler("com.example.bridge", "state", (name, id, payload) => received.Add((name, id, payload))).Should().BeTrue();

        NitrodModApi.Dispatch(new ModMessage("com.example.bridge", "state", [4, 2], ModMessageTarget.OTHERS, 0, 9));
        NitrodModApi.Dispatch(new ModMessage("com.example.bridge", "other-channel", [1], ModMessageTarget.OTHERS, 0, 9));
        NitrodModApi.Dispatch(new ModMessage("com.example.other-mod", "state", [1], ModMessageTarget.OTHERS, 0, 9));

        received.Should().ContainSingle();
        received[0].Id.Should().Be(9);
        received[0].Payload.Should().Equal(4, 2);
        NitrodModApi.UnregisterHandler("com.example.bridge", "state").Should().BeTrue();
    }

    [TestMethod]
    public void Send_OutsideOfSession_ReturnsFalse()
    {
        NitrodBridge.IsInSession.Should().BeFalse();
        NitrodBridge.SendToOthers("com.example.bridge", "state", [1]).Should().BeFalse();
        NitrodModApi.Send("com.example.bridge", "state", new byte[ModMessage.MAX_PAYLOAD_BYTES + 1]).Should().BeFalse();
    }

    [TestMethod]
    public void Dispatch_IsolatesFailingHandlers()
    {
        NitrodModApi.RegisterHandler("com.example.crashy", "state", _ => throw new InvalidOperationException("mod bug"));

        Invoking(() => NitrodModApi.Dispatch(new ModMessage("com.example.crashy", "state", [], ModMessageTarget.OTHERS, 0, 1))).Should().NotThrow();

        NitrodModApi.UnregisterHandler("com.example.crashy", "state");
    }
}
