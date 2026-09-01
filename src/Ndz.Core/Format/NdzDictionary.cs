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
/// <see cref="Nanook.GrindCore.CompressionOptions.InitProperties"/>). Released to nuget
/// as GrindCore 0.8.1, then 0.9.0 added the windowLog override
/// (<see cref="Nanook.GrindCore.CompressionDictionaryOptions.WindowBits"/>) needed for
/// dictionaries over ~8 MiB - see <see cref="Compression.NdzWriter"/>'s
/// `ComputeDictionaryWindowBits`. `Ndz.Core.csproj` targets the official
/// `PackageReference Include="GrindCore" Version="0.9.0"` package.
/// </summary>
public sealed class NdzDictionary
{
    /// <summary>Raw, decompressed dictionary content that primes each dict-mode block's compressor.</summary>
    public required byte[] Content { get; init; }

    /// <summary>Decompressed size - recorded verbatim in both DictionaryStoredSize and DictionaryDecompressedSize (the reference packer stores its dictionary uncompressed, so the two are equal).</summary>
    public int DecompressedSize => Content.Length;
}
