using Nanook.GrindCore;
using Ndz.Core.Compression;
using Ndz.Core.Format;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    try
    {
        switch (args[0])
        {
            case "compress":
                return RunCompress(args[1..]);
            case "decompress":
                return RunDecompress(args[1..]);
            case "info":
                return RunInfo(args[1..]);
            case "verify":
                return RunVerify(args[1..]);
            case "-h":
            case "--help":
            case "help":
                PrintUsage();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintUsage();
                return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine($"""
        ndz - NDS ROM <-> seekable-zstd .ndz converter

        Usage:
          ndz compress <in.nds> <out.ndz> [--level 1-{NdzConstants.MaxLevel}] [--block-size N]
                                          [--no-filters] [--raw-dict <size>] [--base <base.nds>]
                                                             Compress a decrypted .nds into .ndz.
                                                             --level defaults to 19 and cannot exceed
                                                             {NdzConstants.MaxLevel} - a hardware limit of the
                                                             target decoder, not a preference (higher
                                                             levels decompress too slowly). --block-size
                                                             defaults to {NdzConstants.BlockSize} bytes, must be a power
                                                             of two, and cannot exceed {NdzConstants.MaxBlockSize} - also a
                                                             hardware limit (bigger blocks take too long
                                                             to fetch and decompress on a cache miss).
                                                             --no-filters skips trying the five per-block
                                                             byte-transform filters (on by default - real,
                                                             brute-force extra compression cost, up to ~6x
                                                             more zstd calls per full block). --raw-dict
                                                             <size> (e.g. 8m, 512k) derives a dictionary
                                                             from this ROM's own repeated content, up to
                                                             that size - the only way either reference
                                                             implementation ever builds one; there is no
                                                             option to load externally-supplied dictionary
                                                             content, because neither reference has one.
                                                             --base patches against a second, already-
                                                             decrypted .nds (windowed dictionary
                                                             compression against its content, not a binary
                                                             diff) - the same base must be supplied again
                                                             to decompress/verify.
          ndz compress <in.nds> --pair-out <pair.ndz> --base <base.nds>
                                                             Pack a base + base-patched pair into one
                                                             self-contained file - no external base
                                                             needed to unpack either side.
          ndz decompress <in.ndz> <out.nds> [--base <base.nds>] [--index N]
                                                             Reconstruct the original .nds. --base is
                                                             required if the file used base-patch mode
                                                             (not for a pair container, which carries its
                                                             own base). --index picks which ROM to extract
                                                             from a pair container (default: the
                                                             self-contained one).
          ndz info <in.ndz>                                 Print front-matter and seek-table summary, or
                                                             (for a pair container) both entries' summaries.
          ndz verify <in.ndz> <in.nds> [--base <base.nds>] [--index N]
                                                             Decompress and byte-compare against the
                                                             original .nds.

        Notes:
          - ROMs should be decrypted first; NDZ compresses raw bytes as-is.
          - The five byte-transform filter modes, raw-dictionary mode, base-ROM patch
            mode, and the pair-container format are all fully implemented, both
            directions - decompress correctly reads real files from the reference
            tooling using any of them.
        """);
}

/// <summary>Parses a size like "8m", "512k", or a plain byte count - matches `ndztool.py`'s own `parse_size`.</summary>
static bool TryParseSize(string text, out int size)
{
    size = 0;
    string s = text.Trim().ToLowerInvariant();
    int multiplier = 1;
    if (s.EndsWith('k')) { multiplier = 1024; s = s[..^1]; }
    else if (s.EndsWith('m')) { multiplier = 1024 * 1024; s = s[..^1]; }

    if (!long.TryParse(s, out long value))
        return false;
    long result = value * multiplier;
    if (result < 0 || result > int.MaxValue)
        return false;
    size = (int)result;
    return true;
}

static int RunCompress(string[] args)
{
    string? inPath = null, outPath = null, basePath = null, pairOutPath = null;
    int level = 19;
    int blockSize = NdzConstants.BlockSize;
    bool enableFilters = true;
    int rawDictionarySize = 0;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--level")
        {
            if (++i >= args.Length || !int.TryParse(args[i], out level))
            {
                Console.Error.WriteLine($"--level requires an integer value (1-{NdzConstants.MaxLevel}).");
                return 1;
            }
        }
        else if (args[i] == "--block-size")
        {
            if (++i >= args.Length || !int.TryParse(args[i], out blockSize))
            {
                Console.Error.WriteLine($"--block-size requires an integer value in bytes (power of two, up to {NdzConstants.MaxBlockSize}).");
                return 1;
            }
        }
        else if (args[i] == "--no-filters")
        {
            enableFilters = false;
        }
        else if (args[i] == "--raw-dict")
        {
            if (++i >= args.Length || !TryParseSize(args[i], out rawDictionarySize))
            {
                Console.Error.WriteLine("--raw-dict requires a size, e.g. 8m or 512k.");
                return 1;
            }
        }
        else if (args[i] == "--base")
        {
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("--base requires a file path.");
                return 1;
            }
            basePath = args[i];
        }
        else if (args[i] == "--pair-out")
        {
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("--pair-out requires a file path.");
                return 1;
            }
            pairOutPath = args[i];
        }
        else if (inPath is null) inPath = args[i];
        else if (outPath is null) outPath = args[i];
        else
        {
            Console.Error.WriteLine($"Unexpected argument: {args[i]}");
            return 1;
        }
    }

    if (inPath is null || (outPath is null && pairOutPath is null))
    {
        Console.Error.WriteLine("Usage: ndz compress <in.nds> <out.ndz> [--level 1-19] [--block-size N] [--no-filters] [--raw-dict <size>] [--base <base.nds>]");
        Console.Error.WriteLine("   or: ndz compress <in.nds> --pair-out <pair.ndz> --base <base.nds>");
        return 1;
    }
    if (pairOutPath != null && basePath is null)
    {
        Console.Error.WriteLine("--pair-out needs --base (the pair holds base + patched).");
        return 1;
    }

    // Hardware limits, not preferences - see NdzConstants.MaxLevel/MaxBlockSize's
    // remarks. Checked here too (not just inside NdzWriter) so a bad --level/--block-size
    // is reported as a normal usage error, not an internal exception.
    if (level < 1 || level > NdzConstants.MaxLevel)
    {
        Console.Error.WriteLine($"--level must be between 1 and {NdzConstants.MaxLevel} (higher levels decompress too slowly for the target hardware).");
        return 1;
    }
    if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0 || blockSize > NdzConstants.MaxBlockSize)
    {
        Console.Error.WriteLine($"--block-size must be a power of two up to {NdzConstants.MaxBlockSize} (bigger blocks take too long to fetch/decompress on the target hardware).");
        return 1;
    }

    if (pairOutPath != null)
    {
        NdzPairWriter.WriteFile(pairOutPath, basePath!, inPath, (CompressionType)level, blockSize, enableFilters, rawDictionarySize);
        long baseSize = new FileInfo(basePath!).Length, targetSize = new FileInfo(inPath).Length;
        long containerSize = new FileInfo(pairOutPath).Length;
        double pairRatio = containerSize == 0 ? 0 : (double)(baseSize + targetSize) / containerSize;
        Console.WriteLine($"Wrote '{pairOutPath}': {baseSize + targetSize:N0} -> {containerSize:N0} bytes ({pairRatio:F3}x).");
        return 0;
    }

    long originalSize = new FileInfo(inPath).Length;
    NdzWriter.CompressFile(inPath, outPath!, (CompressionType)level, blockSize, enableFilters, basePath, rawDictionarySize);
    long compressedSize = new FileInfo(outPath!).Length;

    double ratio = originalSize == 0 ? 0 : (double)compressedSize / originalSize;
    Console.WriteLine($"Wrote '{outPath}': {originalSize:N0} -> {compressedSize:N0} bytes ({ratio:P1}).");
    return 0;
}

static int RunDecompress(string[] args)
{
    if (!TryParsePositionalsWithBaseAndIndex(args, 2, out var positionals, out string? basePath, out int? index, out string? error))
    {
        Console.Error.WriteLine(error ?? "Usage: ndz decompress <in.ndz> <out.nds> [--base <base.nds>] [--index N]");
        return 1;
    }

    byte[] bytes = File.ReadAllBytes(positionals[0]);
    byte[] rom;
    if (NdzPairContainer.TryRead(bytes, out var pair))
    {
        int i = index ?? pair!.PlainEntryIndex;
        rom = pair!.DecompressEntry(i);
        Console.WriteLine($"[{i}] extracted from pair container '{positionals[0]}'.");
    }
    else
    {
        using var archive = NdzArchive.Open(bytes, basePath == null ? null : File.ReadAllBytes(basePath));
        rom = archive.DecompressAll();
    }
    File.WriteAllBytes(positionals[1], rom);

    Console.WriteLine($"Wrote '{positionals[1]}' ({rom.Length:N0} bytes).");
    return 0;
}

/// <summary>Parses `expectedPositionals` bare arguments plus optional `--base <path>`/`--index N`, in any order - shared by decompress/verify.</summary>
static bool TryParsePositionalsWithBaseAndIndex(string[] args, int expectedPositionals, out List<string> positionals, out string? basePath, out int? index, out string? error)
{
    positionals = new List<string>();
    basePath = null;
    index = null;
    error = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--base")
        {
            if (++i >= args.Length)
            {
                error = "--base requires a file path.";
                return false;
            }
            basePath = args[i];
        }
        else if (args[i] == "--index")
        {
            if (++i >= args.Length || !int.TryParse(args[i], out int parsedIndex))
            {
                error = "--index requires an integer value.";
                return false;
            }
            index = parsedIndex;
        }
        else
        {
            positionals.Add(args[i]);
        }
    }

    if (positionals.Count != expectedPositionals)
    {
        error = $"Expected {expectedPositionals} argument(s), got {positionals.Count}.";
        return false;
    }
    return true;
}

static int RunInfo(string[] args)
{
    if (args.Length != 1)
    {
        Console.Error.WriteLine("Usage: ndz info <in.ndz>");
        return 1;
    }

    byte[] bytes = File.ReadAllBytes(args[0]);

    if (NdzPairContainer.TryRead(bytes, out var pair))
    {
        Console.WriteLine($"Pair container, {pair!.Entries.Count} ROM(s):");
        for (int i = 0; i < pair.Entries.Count; i++)
        {
            var entry = pair.Entries[i];
            var (fm, _) = pair.ReadEntryInfo(i);
            var flags = new[] { NdzFlags.V2, NdzFlags.ZStd, NdzFlags.Filters, NdzFlags.BasePatch, NdzFlags.RawDictionary }
                .Where(f => fm.Flags.HasFlag(f));
            string gameCodeText = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(entry.GameCode));
            Console.WriteLine($"  [{i}] {gameCodeText}  {entry.Size:N0} -> {entry.OriginalSize:N0} bytes  [{string.Join(", ", flags)}]" +
                (i == pair.PlainEntryIndex ? "  (self-contained)" : ""));
        }
        return 0;
    }

    var (frontMatter, seekTable) = NdzArchive.ReadInfo(bytes);

    long compressedTotal = seekTable.Sum(e => (long)e.CompressedSize);

    var namedFlags = new[] { NdzFlags.V2, NdzFlags.ZStd, NdzFlags.Filters, NdzFlags.BasePatch, NdzFlags.RawDictionary }
        .Where(f => frontMatter.Flags.HasFlag(f));

    Console.WriteLine($"Game code:          0x{frontMatter.GameCode:X8}");
    Console.WriteLine($"Original size:      {frontMatter.OriginalSize:N0} bytes");
    Console.WriteLine($"Compressed payload: {compressedTotal:N0} bytes");
    Console.WriteLine($"Frames:             {seekTable.Count:N0} ({NdzConstants.FrameSize:N0} bytes each, last frame may be shorter)");
    Console.WriteLine($"Block size:         {frontMatter.Flags.GetBlockSize():N0} bytes (from flags)");
    Console.WriteLine($"Flags:              0x{(uint)frontMatter.Flags:X8} [{string.Join(", ", namedFlags)}]");
    Console.WriteLine($"Dictionary:         {(frontMatter.HasDictionary ? $"{frontMatter.DictionaryDecompressedSize:N0} bytes (decompressed)" : "none")}");
    if (frontMatter.Flags.HasFlag(NdzFlags.BasePatch))
        Console.WriteLine($"Base ROM:           0x{frontMatter.BaseGameCode:X8}, {frontMatter.BaseOriginalSize:N0} bytes (needed to decompress)");
    return 0;
}

static int RunVerify(string[] args)
{
    if (!TryParsePositionalsWithBaseAndIndex(args, 2, out var positionals, out string? basePath, out int? index, out string? error))
    {
        Console.Error.WriteLine(error ?? "Usage: ndz verify <in.ndz> <in.nds> [--base <base.nds>] [--index N]");
        return 1;
    }

    byte[] bytes = File.ReadAllBytes(positionals[0]);
    byte[] rebuilt;
    if (NdzPairContainer.TryRead(bytes, out var pair))
    {
        rebuilt = pair!.DecompressEntry(index ?? pair.PlainEntryIndex);
    }
    else
    {
        using var archive = NdzArchive.Open(bytes, basePath == null ? null : File.ReadAllBytes(basePath));
        rebuilt = archive.DecompressAll();
    }
    byte[] original = File.ReadAllBytes(positionals[1]);

    if (original.AsSpan().SequenceEqual(rebuilt))
    {
        Console.WriteLine($"PASS: '{positionals[0]}' decompresses byte-identically to '{positionals[1]}' ({original.Length:N0} bytes).");
        return 0;
    }

    Console.WriteLine("FAIL: decompressed output differs from the original.");
    Console.WriteLine($"  original:    {original.Length:N0} bytes");
    Console.WriteLine($"  decompressed: {rebuilt.Length:N0} bytes");

    int minLen = Math.Min(original.Length, rebuilt.Length);
    for (int i = 0; i < minLen; i++)
    {
        if (original[i] != rebuilt[i])
        {
            Console.WriteLine($"  first difference at byte offset 0x{i:X}");
            break;
        }
    }

    return 1;
}
