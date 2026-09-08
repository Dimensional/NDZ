namespace Ndz.Gui.Models;

/// <summary>
/// One entry in a block-size or dictionary-size dropdown: either the "Auto" sentinel
/// (<see cref="Bytes"/> null - let the packer decide, no explicit override) or a concrete
/// byte size with a human label. A record struct so equality (used to find/select the
/// matching entry after Analyze) is by value, not reference.
/// </summary>
public readonly record struct SizeOption(int? Bytes, string Label)
{
    public static readonly SizeOption Auto = new(null, "Auto");

    public static SizeOption FromBytes(int bytes) => bytes switch
    {
        0 => new SizeOption(0, "None"),
        >= 1024 * 1024 => new SizeOption(bytes, $"{bytes / (1024.0 * 1024.0):0.#} MiB"),
        >= 1024 => new SizeOption(bytes, $"{bytes / 1024.0:0.#} KiB"),
        _ => new SizeOption(bytes, $"{bytes} B"),
    };

    public override string ToString() => Label;
}
