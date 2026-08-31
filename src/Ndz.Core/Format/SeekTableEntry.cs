namespace Ndz.Core.Format;

/// <summary>One entry in the trailer's seek table: a frame's compressed and decompressed size.</summary>
public readonly record struct SeekTableEntry(uint CompressedSize, uint DecompressedSize);
