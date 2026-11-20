namespace Nitrox.Model.Packets;

[TestClass]
public class ModMessageTest
{
    [TestMethod]
    public void IsValid_AcceptsNormalMessages()
    {
        new ModMessage("com.example.mod", "state", new byte[100], ModMessageTarget.OTHERS, 0, 0).IsValid(out string reason).Should().BeTrue(reason);
        new ModMessage("com.example.mod", "state", new byte[ModMessage.MAX_PAYLOAD_BYTES], ModMessageTarget.PLAYER, 3, 0).IsValid(out _).Should().BeTrue();
    }

    [TestMethod]
    public void IsValid_RejectsOversizedOrMalformedMessages()
    {
        new ModMessage("com.example.mod", "state", new byte[ModMessage.MAX_PAYLOAD_BYTES + 1], ModMessageTarget.OTHERS, 0, 0).IsValid(out _).Should().BeFalse();
        new ModMessage("", "state", [], ModMessageTarget.OTHERS, 0, 0).IsValid(out _).Should().BeFalse();
        new ModMessage("com.example.mod", new string('x', ModMessage.MAX_ID_LENGTH + 1), [], ModMessageTarget.OTHERS, 0, 0).IsValid(out _).Should().BeFalse();
        new ModMessage("com.example.mod", "state", [], (ModMessageTarget)42, 0, 0).IsValid(out _).Should().BeFalse();
    }

    [TestMethod]
    public void WithSender_OverwritesClaimedSender()
    {
        ModMessage spoofed = new("com.example.mod", "state", [1, 2, 3], ModMessageTarget.EVERYONE, 0, senderPlayerId: 1);

        ModMessage relayed = spoofed.WithSender(7);

        relayed.SenderPlayerId.Should().Be(7);
        relayed.Payload.Should().Equal(1, 2, 3);
        relayed.Target.Should().Be(ModMessageTarget.EVERYONE);
    }
}
