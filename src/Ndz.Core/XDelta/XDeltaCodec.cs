namespace Ndz.Core.XDelta;

/// <summary>
/// Public entry point for xdelta3/VCDIFF (RFC 3284) patch apply and generate - used for both
/// version-diffing (one release of a game against another) and ROM hacks (a hack against a
/// vanilla dump), matching real usage on ndz-studio. `byte[]`-based, not `Stream`-based, to
/// match how <c>NdzWriter</c>/<c>NdzPairWriter</c> already work throughout this codebase -
/// ROMs are always fully-resident byte arrays here, never streamed. See
/// <see cref="VcdiffDecoder"/>/<see cref="VcdiffEncoder"/> for the actual codec, and
/// <c>docs/xdelta-vcdiff-notes.md</c> for the format investigation this was built from.
/// </summary>
public static class XDeltaCodec
{
    /// <summary>Applies <paramref name="delta"/> (a VCDIFF/.xdelta patch) against <paramref name="source"/>, returning the reconstructed target bytes.</summary>
    /// <exception cref="XDeltaException">The delta is malformed, uses an unsupported feature, or its checksum doesn't match.</exception>
    public static byte[] Apply(byte[] source, byte[] delta) => VcdiffDecoder.Decode(source, delta);

    /// <summary>File-path convenience for <see cref="Apply(byte[], byte[])"/>.</summary>
    public static void ApplyToFile(string sourcePath, string deltaPath, string outputPath) =>
        File.WriteAllBytes(outputPath, Apply(File.ReadAllBytes(sourcePath), File.ReadAllBytes(deltaPath)));

    /// <summary>Generates a VCDIFF delta describing how to turn <paramref name="source"/> into <paramref name="target"/>.</summary>
    public static byte[] Generate(byte[] source, byte[] target) => VcdiffEncoder.Encode(source, target);

    /// <summary>File-path convenience for <see cref="Generate(byte[], byte[])"/>.</summary>
    public static void GenerateToFile(string sourcePath, string targetPath, string deltaOutputPath) =>
        File.WriteAllBytes(deltaOutputPath, Generate(File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath)));
}
