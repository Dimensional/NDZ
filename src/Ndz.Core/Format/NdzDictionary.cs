namespace Ndz.Core.Format;

/// <summary>
/// The (optional) dictionary section of a .ndz file - raw bytes stored verbatim between
/// the front-matter and frame 0, used to prime the ZStd compressor for every block that
/// benefits from it (see <see cref="BlockMode.Dict"/>).
///
/// GrindCore.net's ZStd wrapper had no API to load a raw content dictionary as of
/// 2026-08-22 - flagged to Nanook (GrindCore.net's maintainer), who fixed it upstream
/// (native `*BlockWithDict` functions using `ZSTD_compress_usingCDict`/
/// `ZSTD_decompress_usingDDict`, wired through `ZStdBlock` via
/// <see cref="Nanook.GrindCore.CompressionOptions.InitProperties"/>). NDZ currently
/// builds against a local dev build of that fix
/// (`Dimensional/GrindCore.net`, branch `release/uniquenames-zstddict` - see
/// `Ndz.Core.csproj`'s `ProjectReference`), not yet released to nuget.
/// </summary>
public sealed class NdzDictionary
{
    /// <summary>Raw, decompressed dictionary content that primes each dict-mode block's compressor.</summary>
    public required byte[] Content { get; init; }

    /// <summary>Decompressed size - recorded verbatim in both DictionaryStoredSize and DictionaryDecompressedSize (the reference packer stores its dictionary uncompressed, so the two are equal).</summary>
    public int DecompressedSize => Content.Length;
}
