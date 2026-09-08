using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>Builds synthetic .nds-shaped byte arrays for tests - real header fields, deterministic filler payload.</summary>
internal static class TestRom
{
    /// <summary>
    /// Builds a <paramref name="totalSize"/>-byte fake ROM with a valid-shaped header
    /// (game code at 0x0C, banner offset at 0x68) and a recognizable repeating pattern
    /// at the banner itself, so <c>NdsRomInfo.FromRom</c> has real data to read.
    /// Everything else is deterministic (seeded) pseudo-random filler - not all zeros -
    /// so compression exercises real ZStd matching rather than a degenerate all-zero case.
    /// </summary>
    /// <param name="bannerVersion">
    /// The banner's own version field (a u16 at the banner offset) - determines the real
    /// banner content size via <see cref="NdzConstants.GetBannerContentSize"/>. Written
    /// explicitly (rather than left to incidental pattern bytes) so tests can assert on
    /// an exact, known banner size.
    /// </param>
    /// <param name="unitCode">The header's platform byte (0x12) - see <see cref="NdsRomInfo.UnitCode"/>. Written explicitly for the same reason as <paramref name="bannerVersion"/>.</param>
    /// <param name="romVersion">The header's revision byte (0x1E) - see <see cref="NdsRomInfo.RomVersion"/>. Written explicitly for the same reason as <paramref name="bannerVersion"/>.</param>
    /// <param name="region">The header's region byte (0x1D) - see <see cref="NdsRomInfo.Region"/>. Written explicitly for the same reason as <paramref name="bannerVersion"/>.</param>
    public static byte[] Build(int totalSize, string gameCode = "ABCE", int seed = 1, ushort bannerVersion = 0, byte unitCode = 0x00, byte romVersion = 0x00, byte region = 0x00)
    {
        if (totalSize < 0x1000)
            throw new ArgumentException("Test ROM must be at least 0x1000 bytes to fit a header + banner.", nameof(totalSize));
        if (gameCode.Length != 4)
            throw new ArgumentException("Game code must be exactly 4 characters.", nameof(gameCode));

        var rom = new byte[totalSize];
        var rng = new Random(seed);
        rng.NextBytes(rom);

        // Game title (0x00-0x0B): arbitrary ASCII, unused by NDZ.
        "TESTGAME"u8.CopyTo(rom.AsSpan(0x00));

        // Game code (0x0C-0x0F).
        System.Text.Encoding.ASCII.GetBytes(gameCode).CopyTo(rom, 0x0C);

        // Unit code (0x12): 00h=NDS, 02h=NDS+DSi, 03h=DSi-exclusive.
        rom[0x12] = unitCode;

        // Region (0x1D).
        rom[0x1D] = region;

        // ROM version / revision (0x1E).
        rom[0x1E] = romVersion;

        // Banner offset (0x68): right after the 0x200-byte header.
        const uint bannerOffset = 0x200;
        BitConverter.GetBytes(bannerOffset).CopyTo(rom, 0x68);

        // Give the whole largest-possible banner region a recognizable, repeating
        // pattern so tests can assert on it regardless of which version size applies.
        int patternLength = (int)Math.Min(0x23C0, rom.Length - bannerOffset);
        for (int i = 0; i < patternLength; i++)
            rom[bannerOffset + i] = (byte)(0xB0 + (i % 16));

        // Version field (the banner's own first 2 bytes) - written last so it isn't
        // clobbered by the pattern fill above.
        BitConverter.GetBytes(bannerVersion).CopyTo(rom, (int)bannerOffset);

        return rom;
    }
}
