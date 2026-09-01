namespace Ndz.Core.Format;

/// <summary>
/// One entry in a pair container's header: where its complete `.ndz` blob lives, its
/// compressed and original sizes, and its game code - confirmed against `ndztool.py`'s
/// own <c>struct.unpack_from("&lt;III4s", ...)</c>. See
/// <see cref="Compression.NdzPairContainer"/>.
/// </summary>
public readonly record struct NdzPairEntry(uint Offset, uint Size, uint OriginalSize, uint GameCode);
