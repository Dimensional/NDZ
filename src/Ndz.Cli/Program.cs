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
    Console.WriteLine("""
        ndz - NDS ROM <-> seekable-zstd .ndz converter

        Usage:
          ndz compress <in.nds> <out.ndz> [--level 1-22]   Compress a decrypted .nds into .ndz.
                                                             --level defaults to 19.
          ndz decompress <in.ndz> <out.nds>                 Reconstruct the original .nds.
          ndz info <in.ndz>                                 Print front-matter and seek-table summary.
          ndz verify <in.ndz> <in.nds>                       Decompress and byte-compare against the
                                                             original .nds.

        Notes:
          - ROMs should be decrypted first; NDZ compresses raw bytes as-is.
          - Base-ROM patch mode (front-matter flags bit 4) is not implemented yet.
          - Dictionary sections and non-plain per-block compression modes (raw-dictionary,
            filter transforms) aren't implemented yet; decompress fails with a clear error
            naming the exact frame/block/mode rather than misreading such files.
        """);
}

static int RunCompress(string[] args)
{
    string? inPath = null, outPath = null;
    int level = 19;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--level")
        {
            if (++i >= args.Length || !int.TryParse(args[i], out level))
            {
                Console.Error.WriteLine("--level requires an integer value (1-22).");
                return 1;
            }
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
        Console.Error.WriteLine("Usage: ndz compress <in.nds> <out.ndz> [--level 1-22]");
        return 1;
    }

    if (level < 1 || level > 22)
    {
        Console.Error.WriteLine("--level must be between 1 and 22.");
        return 1;
    }

    long originalSize = new FileInfo(inPath).Length;
    NdzWriter.CompressFile(inPath, outPath, (CompressionType)level);
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
