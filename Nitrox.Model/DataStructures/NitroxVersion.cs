using System;

namespace Nitrox.Model.DataStructures;

/// <summary>
///     Serializable version of <see cref="Version" /> with only major and minor properties.
/// </summary>
[Serializable]
public readonly struct NitroxVersion : IComparable<NitroxVersion>
{
    public ushort Major { get; init; }

    public ushort Minor { get; init; }


    public NitroxVersion(ushort major, ushort minor)
    {
        Major = major;
        Minor = minor;
    }

    public NitroxVersion(int major, int minor) : this((ushort)major, (ushort)minor)
    {
        if (major is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }
        if (minor is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minor));
        }
    }

    public int CompareTo(NitroxVersion other)
    {
        if (Major != other.Major)
        {
            return Major > other.Major ? 1 : -1;
        }
        if (Minor != other.Minor)
        {
            return Minor > other.Minor ? 1 : -1;
        }

        return 0;
    }

    /// <summary>
    ///     True if two builds can exchange packets. Packets are serialized as a BinaryPack union whose indices depend on the
    ///     exact set of packet types, so in general only identical major.minor versions are compatible.
    ///     Nitrod 1.8 and 1.9 shipped the same packet set, so they are allowed to interoperate.
    /// </summary>
    public bool IsNetworkCompatibleWith(NitroxVersion other)
    {
        if (CompareTo(other) == 0)
        {
            return true;
        }

        return Major == 1 && other.Major == 1 &&
               Minor is 8 or 9 && other.Minor is 8 or 9;
    }

    public override string ToString() => $"{Major}.{Minor}";
}
