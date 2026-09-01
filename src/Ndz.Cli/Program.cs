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
          ndz compress <in.nds> <out.ndz> [--level 1-{NdzConstants.MaxLevel}] [--block-size N] [--dict <file>]
                                                             Compress a decrypted .nds into .ndz.
                                                             --level defaults to 19 and cannot exceed
                                                             {NdzConstants.MaxLevel} - a hardware limit of the
                                                             target decoder, not a preference (higher
                                                             levels decompress too slowly). --block-size
                                                             defaults to {NdzConstants.BlockSize} bytes, must be a power
                                                             of two, and cannot exceed {NdzConstants.MaxBlockSize} - also a
                                                             hardware limit (bigger blocks take too long
                                                             to fetch and decompress on a cache miss).
                                                             --dict primes compression with a raw content
                                                             dictionary (its bytes are stored verbatim in
                                                             the output too).
          ndz decompress <in.ndz> <out.nds>                 Reconstruct the original .nds.
          ndz info <in.ndz>                                 Print front-matter and seek-table summary.
          ndz verify <in.ndz> <in.nds>                       Decompress and byte-compare against the
                                                             original .nds.

        Notes:
          - ROMs should be decrypted first; NDZ compresses raw bytes as-is.
          - Base-ROM patch mode (front-matter flags bit 4) is not implemented yet.
          - The five byte-transform filter modes aren't implemented yet; decompress fails with a
            clear error naming the exact frame/block/mode rather than misreading such a file.
            Raw-dictionary mode (Plain/Dict per block) is fully implemented, both directions.
        """);
}

static int RunCompress(string[] args)
{
    string? inPath = null, outPath = null, dictPath = null;
    int level = 19;
    int blockSize = NdzConstants.BlockSize;

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
        else if (args[i] == "--dict")
        {
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("--dict requires a file path.");
                return 1;
            }
            dictPath = args[i];
        }
        else if (inPath is null) inPath = args[i];
        else if (outPath is null) outPath = args[i];
        else
        {
            Console.Error.WriteLine($"Unexpected argument: {args[i]}");
            return 1;
        }
    }

    if (inPath is null || outPath is null)
    {
        Console.Error.WriteLine("Usage: ndz compress <in.nds> <out.ndz> [--level 1-19] [--block-size N] [--dict <file>]");
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

    NdzDictionary? dictionary = dictPath is null
        ? null
        : new NdzDictionary { Content = File.ReadAllBytes(dictPath) };

    long originalSize = new FileInfo(inPath).Length;
    NdzWriter.CompressFile(inPath, outPath, (CompressionType)level, dictionary, blockSize);
    long compressedSize = new FileInfo(outPath).Length;

    double ratio = originalSize == 0 ? 0 : (double)compressedSize / originalSize;
    Console.WriteLine($"Wrote '{outPath}': {originalSize:N0} -> {compressedSize:N0} bytes ({ratio:P1}).");
    return 0;
}

static int RunDecompress(string[] args)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: ndz decompress <in.ndz> <out.nds>");
        return 1;
    }

    using var archive = NdzArchive.OpenFile(args[0]);
    byte[] rom = archive.DecompressAll();
    File.WriteAllBytes(args[1], rom);

    Console.WriteLine($"Wrote '{args[1]}' ({rom.Length:N0} bytes).");
    return 0;
}

static int RunInfo(string[] args)
{
    if (args.Length != 1)
    {
        Console.Error.WriteLine("Usage: ndz info <in.ndz>");
        return 1;
    }

    using var archive = NdzArchive.OpenFile(args[0]);
    NdzFrontMatter fm = archive.FrontMatter;

    long compressedTotal = archive.SeekTable.Sum(e => (long)e.CompressedSize);

    var namedFlags = new[] { NdzFlags.V2, NdzFlags.ZStd, NdzFlags.Filters, NdzFlags.BasePatch, NdzFlags.RawDictionary }
        .Where(f => fm.Flags.HasFlag(f));

    Console.WriteLine($"Game code:          0x{fm.GameCode:X8}");
    Console.WriteLine($"Original size:      {fm.OriginalSize:N0} bytes");
    Console.WriteLine($"Compressed payload: {compressedTotal:N0} bytes");
    Console.WriteLine($"Frames:             {archive.SeekTable.Count:N0} ({NdzConstants.FrameSize:N0} bytes each, last frame may be shorter)");
    Console.WriteLine($"Block size:         {fm.Flags.GetBlockSize():N0} bytes (from flags)");
    Console.WriteLine($"Flags:              0x{(uint)fm.Flags:X8} [{string.Join(", ", namedFlags)}]");
    Console.WriteLine($"Dictionary:         {(fm.HasDictionary ? $"{fm.DictionaryDecompressedSize:N0} bytes (decompressed)" : "none")}");
    return 0;
}

static int RunVerify(string[] args)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: ndz verify <in.ndz> <in.nds>");
        return 1;
    }

    using var archive = NdzArchive.OpenFile(args[0]);
    byte[] rebuilt = archive.DecompressAll();
    byte[] original = File.ReadAllBytes(args[1]);

    if (original.AsSpan().SequenceEqual(rebuilt))
    {
        Console.WriteLine($"PASS: '{args[0]}' decompresses byte-identically to '{args[1]}' ({original.Length:N0} bytes).");
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
