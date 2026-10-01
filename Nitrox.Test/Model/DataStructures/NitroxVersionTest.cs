namespace Nitrox.Model.DataStructures;

[TestClass]
public class NitroxVersionTest
{
    [TestMethod]
    public void Equals()
    {
        NitroxVersion a = new(2, 1);
        NitroxVersion b = new(1, 15);

        NitroxVersion source = new(2, 1);
        source.Equals(a).Should().BeTrue();
        source.Equals(b).Should().BeFalse();
    }

    [TestMethod]
    public void Compare()
    {
        NitroxVersion source = new(2, 1);
        source.CompareTo(new(2, 1)).Should().Be(0);
        source.CompareTo(new(1, 15)).Should().Be(1);
        source.CompareTo(new (2, 2)).Should().Be(-1);
        source.CompareTo(new (3, 1)).Should().Be(-1);
    }

    [TestMethod]
    [DataRow(1, 8, true)]
    [DataRow(1, 9, true)]
    [DataRow(1, 10, false)]
    [DataRow(2, 8, false)]
    public void NetworkCompatibility(int major, int minor, bool expected)
    {
        NitroxVersion source = new(1, 9);

        source.IsNetworkCompatibleWith(new(major, minor)).Should().Be(expected);
    }
}
