namespace VolMon.Hardware.Beacn;

/// <summary>
/// Represents a Beacn device firmware version, packed as a 32-bit integer.
/// Layout: [major:4][minor:4][patch:8][build:16]
/// </summary>
internal readonly struct BeacnVersion : IComparable<BeacnVersion>
{
    public uint Raw { get; }
    public int Major => (int)(Raw >> 28);
    public int Minor => (int)((Raw >> 24) & 0x0F);
    public int Patch => (int)((Raw >> 16) & 0xFF);
    public int Build => (int)(Raw & 0xFFFF);

    public BeacnVersion(uint raw) => Raw = raw;

    /// <summary>
    /// Parse a firmware version from a 4-byte little-endian buffer.
    /// </summary>
    public static BeacnVersion FromBytes(ReadOnlySpan<byte> bytes)
    {
        var raw = BitConverter.ToUInt32(bytes);
        return new BeacnVersion(raw);
    }

    public int CompareTo(BeacnVersion other) => Raw.CompareTo(other.Raw);

    public static bool operator >(BeacnVersion a, BeacnVersion b) => a.Raw > b.Raw;
    public static bool operator <(BeacnVersion a, BeacnVersion b) => a.Raw < b.Raw;
    public static bool operator >=(BeacnVersion a, BeacnVersion b) => a.Raw >= b.Raw;
    public static bool operator <=(BeacnVersion a, BeacnVersion b) => a.Raw <= b.Raw;

    public override string ToString() => $"{Major}.{Minor}.{Patch}.{Build}";
}
