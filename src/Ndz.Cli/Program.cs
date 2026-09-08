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
            case "analyze":
                return RunAnalyze(args[1..]);
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
          ndz compress <in.nds> <out.ndz> [--level 1-{NdzConstants.MaxLevel}] [--block-size N] [--frame-size N]
                                          [--no-filters] [--raw-dict <size>] [--base <base.nds>] [--no-verify]
                                                             Compress a decrypted .nds into .ndz.
                                                             --level defaults to 19 and cannot exceed
                                                             {NdzConstants.MaxLevel} - a hardware limit of the
                                                             target decoder, not a preference (higher
                                                             levels decompress too slowly). --block-size
                                                             defaults to {NdzConstants.BlockSize} bytes - one of
                                                             8 KiB/16 KiB/32 KiB, or `auto` to pick from those
                                                             the same way --raw-dict auto does (see below).
                                                             Bigger blocks trade away random-access
                                                             granularity (a whole block must be decompressed
                                                             to reach any byte in it) for ratio - 16/32 KiB
                                                             became real options 2026-09-06 after a firmware
                                                             fix; only these three sizes are offered here (not
                                                             every power of two up to the hardware's
                                                             {NdzConstants.MaxBlockSize}-byte ceiling) to keep the
                                                             choice simple and always a safe one.
                                                             --frame-size (e.g. 128k) defaults to
                                                             {NdzConstants.FrameSize} bytes - the outer seek-table
                                                             bucketing granularity, not a hardware limit (no
                                                             cap, no power-of-two requirement, just must be
                                                             >= --block-size); nothing in the file records
                                                             it, decompress always reads it back correctly
                                                             from the seek table itself regardless.
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
                                                             --raw-dict auto runs the same sampled estimate
                                                             as `ndz analyze` internally and packs with
                                                             whichever size it recommends (see `ndz analyze
                                                             --help` below); --max-dict caps that search
                                                             (default 8m, the real PSRAM budget on the target
                                                             hardware - the recommendation itself already
                                                             tends to land below that on its own once growing
                                                             the dictionary further stops being clearly worth
                                                             it, since spare PSRAM is available to the
                                                             decompressed-block cache instead), only
                                                             meaningful with `auto`.
                                                             --base patches against a second, already-
                                                             decrypted .nds (windowed dictionary
                                                             compression against its content, not a binary
                                                             diff) - the same base must be supplied again
                                                             to decompress/verify. --no-verify skips the
                                                             default decode-and-byte-compare check that
                                                             runs after every pack (reads the file back off
                                                             disk and confirms it decodes to the original
                                                             before reporting success) - matches
                                                             ndztool.py's own --no-verify exactly.
          ndz compress <target1.nds> [target2.nds ...] --pair-out <pair.ndz> --base <base.nds>
                                                             Pack a base plus one or more base-patched targets
                                                             into one self-contained file - no external base
                                                             needed to unpack any of them. A star topology:
                                                             every target is patched against the SAME shared
                                                             base, never against each other - so a whole
                                                             family of similar ROMs (e.g. every regional/
                                                             version release of one game) can go in one file,
                                                             not just a base+one-target pair. Which ROM is the
                                                             base doesn't need to be picked carefully - dedup
                                                             is roughly symmetric regardless of which side is
                                                             "base". Nothing stops packing unrelated ROMs
                                                             together either - base-patch just finds little or
                                                             no matching content then, safe but not
                                                             beneficial. --raw-dict auto analyzes the base and
                                                             each base-patched target SEPARATELY and may
                                                             recommend a different size for each (an
                                                             already-excellent base-window match often makes a
                                                             dictionary on a patched target pure overhead) -
                                                             an explicit --raw-dict <size> still applies the
                                                             same size to all of them, matching ndztool.py's
                                                             own --pair-out (which only ever packs exactly 2
                                                             ROMs - the N-target case is our own addition,
                                                             needing no new wire-format bits since the
                                                             container's own entry count was already generic).
          ndz analyze <in.nds> [--base <base.nds>] [--max-dict <size>] [--level N] [--block-size N]
                                                             Print an estimated dictionary-size/result-size
                                                             curve (sampled, not a full pack - seconds, not
                                                             minutes) and a recommended size, capped at
                                                             --max-dict (default 8m, see --raw-dict auto
                                                             above). Without --block-size, prints the FULL
                                                             grid - every dictionary size at every one of
                                                             {FormatBlockSizeChoices()} blocks, not just each
                                                             block size's own winner - then an overall
                                                             recommendation; pass --block-size N to see just
                                                             that one size's curve. This is an original
                                                             heuristic of ours, not reverse-engineered from
                                                             either reference implementation or ndz-studio's
                                                             own analyze() - see DictionaryAnalyzer's own
                                                             remarks for what that means for accuracy.
          ndz analyze <target1.nds> [target2.nds ...] --base <base.nds> --pair
                                     [--max-dict <size>] [--level N] [--block-size N]
                                                             Previews what `compress --pair-out --block-size
                                                             auto --raw-dict auto` would actually choose,
                                                             without running a real pack - the base and every
                                                             target are scored TOGETHER on their combined
                                                             total (BlockSizeAnalyzer.AnalyzePair), not
                                                             separately. This matters: analyzing the base and
                                                             a target separately can each recommend a setting
                                                             that's badly wrong once forced to share one block
                                                             size - a bigger block helps a self-contained
                                                             base's own ratio but can badly hurt a base-patched
                                                             target once the block exceeds the base-patch
                                                             window's fixed 16 KiB, collapsing that target's
                                                             match quality. When in doubt, skip this and just
                                                             let --block-size auto --raw-dict auto on the real
                                                             `compress --pair-out` decide for you - this
                                                             command is for previewing that choice first.
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

/// <summary>Renders a byte count as whichever of B/KB/MB reads most naturally - used for --raw-dict auto's own summary and `ndz analyze`'s curve, not a wire-format concern.</summary>
static string FormatSize(long bytes) => bytes switch
{
    0 => "none",
    >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F2} MB",
    >= 1024 => $"{bytes / 1024.0:F1} KB",
    _ => $"{bytes} B",
};

/// <summary>The curated --block-size menu (see <see cref="NdzConstants.SupportedBlockSizes"/>'s remarks), rendered for error/help text.</summary>
static string FormatBlockSizeChoices() => string.Join(", ", NdzConstants.SupportedBlockSizes.Select(size => FormatSize(size)));

static int RunAnalyze(string[] args)
{
    var positionals = new List<string>();
    string? basePath = null;
    int level = 19;
    int? blockSize = null; // null = sweep NdzConstants.SupportedBlockSizes
    int maxDictSize = DictionaryAnalyzer.DefaultMaxDictionarySize;
    bool pairMode = false;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--base")
        {
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("--base requires a file path.");
                return 1;
            }
            basePath = args[i];
        }
        else if (args[i] == "--pair")
        {
            pairMode = true;
        }
        else if (args[i] == "--max-dict")
        {
            if (++i >= args.Length || !TryParseSize(args[i], out maxDictSize))
            {
                Console.Error.WriteLine("--max-dict requires a size, e.g. 8m.");
                return 1;
            }
        }
        else if (args[i] == "--level")
        {
            if (++i >= args.Length || !int.TryParse(args[i], out level))
            {
                Console.Error.WriteLine($"--level requires an integer value (1-{NdzConstants.MaxLevel}).");
                return 1;
            }
        }
        else if (args[i] == "--block-size")
        {
            if (++i >= args.Length || !int.TryParse(args[i], out int parsedBlockSize) || !NdzConstants.SupportedBlockSizes.Contains(parsedBlockSize))
            {
                Console.Error.WriteLine($"--block-size must be one of {FormatBlockSizeChoices()} (omit it to compare all of them).");
                return 1;
            }
            blockSize = parsedBlockSize;
        }
        else positionals.Add(args[i]);
    }

    if (positionals.Count == 0)
    {
        Console.Error.WriteLine("Usage: ndz analyze <in.nds> [--base <base.nds>] [--max-dict <size>] [--level N] [--block-size N]");
        Console.Error.WriteLine("   or: ndz analyze <target1.nds> [target2.nds ...] --base <base.nds> --pair [--max-dict <size>] [--level N] [--block-size N]");
        return 1;
    }

    if (pairMode)
    {
        if (basePath is null)
        {
            Console.Error.WriteLine("--pair needs --base (there's nothing to base-patch every target against otherwise).");
            return 1;
        }
        return RunPairAnalyze(positionals, basePath, maxDictSize, level, blockSize);
    }

    if (positionals.Count > 1)
    {
        Console.Error.WriteLine($"Unexpected argument: {positionals[1]}");
        Console.Error.WriteLine("(Multiple ROMs given - did you mean --pair, to analyze them together as a shared-base family?)");
        return 1;
    }

    string inPath = positionals[0];
    byte[] rom = File.ReadAllBytes(inPath);
    byte[]? baseRom = basePath == null ? null : File.ReadAllBytes(basePath);
    string romDescription = basePath == null
        ? $"'{inPath}' ({rom.Length:N0} bytes), self-dictionary only"
        : $"'{inPath}' ({rom.Length:N0} bytes) base-patched against '{basePath}'";

    var started = DateTime.UtcNow;

    if (blockSize is int fixedBlockSize)
    {
        Console.WriteLine($"Analyzing {romDescription} at {FormatSize(fixedBlockSize)} blocks...");
        var result = DictionaryAnalyzer.Analyze(rom, baseRom, maxDictSize, (CompressionType)level, fixedBlockSize);
        PrintDictionaryCurve(result, rom.Length);
        Console.WriteLine();
        Console.WriteLine($"Recommended: {FormatSize(result.RecommendedDictionarySize)} dictionary " +
            $"(sampled {result.SampledBlockCount:N0}/{result.TotalBlockCount:N0} blocks, " +
            $"analyzed in {(DateTime.UtcNow - started).TotalSeconds:F1}s). {EstimateDisclaimer()}");
        return 0;
    }

    Console.WriteLine($"Analyzing {romDescription}, comparing every dictionary size at each of {FormatBlockSizeChoices()} blocks...");
    var sweep = BlockSizeAnalyzer.Analyze(rom, baseRom, maxDictionarySize: maxDictSize, level: (CompressionType)level);

    // Every combination, not just each block size's own winner - the full block size x
    // dictionary size grid, since a size that's second-best within one block size can
    // still matter (e.g. deciding between two close options by hand).
    foreach (var candidate in sweep.Candidates)
    {
        string blockMarker = candidate.BlockSize == sweep.RecommendedBlockSize ? " <- recommended block size" : "";
        Console.WriteLine();
        Console.WriteLine($"--- {FormatSize(candidate.BlockSize)} blocks{blockMarker} ---");
        PrintDictionaryCurve(candidate.DictionaryAnalysis, rom.Length);
    }

    Console.WriteLine();
    Console.WriteLine($"{"block size",12}   {"best dict",12}   {"est. total",14}   est. ratio");
    foreach (var candidate in sweep.Candidates)
    {
        string marker = candidate.BlockSize == sweep.RecommendedBlockSize ? " <- recommended" : "";
        Console.WriteLine($"{FormatSize(candidate.BlockSize),12}   {FormatSize(candidate.DictionaryAnalysis.RecommendedDictionarySize),12}   " +
            $"{candidate.BestEstimatedTotalSize,10:N0} B   {(rom.Length == 0 ? 0 : (double)rom.Length / candidate.BestEstimatedTotalSize),8:F2}x{marker}");
    }

    Console.WriteLine();
    Console.WriteLine($"Recommended: {FormatSize(sweep.RecommendedBlockSize)} blocks, {FormatSize(sweep.RecommendedDictionarySize)} dictionary " +
        $"(analyzed in {(DateTime.UtcNow - started).TotalSeconds:F1}s). {EstimateDisclaimer()} A bigger block trades away " +
        "random-access granularity for ratio, same as dictionary size trades away PSRAM - see BlockSizeAnalyzer's remarks.");
    return 0;
}

/// <summary>
/// `ndz analyze --pair`: previews what `ndz compress &lt;targets...&gt; --pair-out &lt;out&gt;
/// --base &lt;base&gt; --block-size auto --raw-dict auto` would actually choose, without
/// running a real pack - the base and every target share ONE block size (a star topology,
/// same as <see cref="NdzPairWriter"/>), scored on their COMBINED total via
/// <see cref="BlockSizeAnalyzer.AnalyzePair"/>. This exists because analyzing a base and a
/// target SEPARATELY (the single-ROM `ndz analyze --base` path) can each recommend a
/// setting that's badly wrong once the two are actually forced to share one block size -
/// see AnalyzePair's own remarks on the fixed 16 KiB base-patch window not growing with
/// block size, which is exactly the trap this command exists to avoid walking into by hand.
/// </summary>
static int RunPairAnalyze(List<string> targetPaths, string basePath, int maxDictSize, int level, int? blockSize)
{
    byte[] baseRom = File.ReadAllBytes(basePath);
    byte[][] targetRoms = targetPaths.Select(File.ReadAllBytes).ToArray();
    long totalOriginal = baseRom.Length + targetRoms.Sum(t => (long)t.Length);

    var started = DateTime.UtcNow;
    int[]? candidates = blockSize is int fixedBlockSize ? new[] { fixedBlockSize } : null;
    string blockSizeDescription = blockSize is int fb ? $"at {FormatSize(fb)} blocks" : $"comparing every dictionary size at each of {FormatBlockSizeChoices()} blocks";

    Console.WriteLine($"Analyzing '{basePath}' ({baseRom.Length:N0} bytes) as a shared base for {targetPaths.Count} " +
        $"target(s) ({string.Join(", ", targetPaths.Select(p => $"'{p}'"))}), {blockSizeDescription}...");
    Console.WriteLine("(Star topology, matching --pair-out: every target is base-patched against the base, never against each other.)");

    var pair = BlockSizeAnalyzer.AnalyzePair(baseRom, targetRoms, blockSizeCandidates: candidates, maxDictionarySize: maxDictSize, level: (CompressionType)level);

    foreach (var candidate in pair.Candidates)
    {
        string blockMarker = candidate.BlockSize == pair.RecommendedBlockSize ? " <- recommended block size" : "";
        Console.WriteLine();
        Console.WriteLine($"--- {FormatSize(candidate.BlockSize)} blocks{blockMarker} ---");
        Console.WriteLine($"Base '{basePath}':");
        PrintDictionaryCurve(candidate.BaseAnalysis, baseRom.Length);
        for (int i = 0; i < targetPaths.Count; i++)
        {
            Console.WriteLine($"Target '{targetPaths[i]}' (base-patched):");
            PrintDictionaryCurve(candidate.TargetAnalyses[i], targetRoms[i].Length);
        }
        double combinedRatio = candidate.CombinedBestEstimatedTotalSize == 0 ? 0 : (double)totalOriginal / candidate.CombinedBestEstimatedTotalSize;
        Console.WriteLine($"Combined at this block size: {candidate.CombinedBestEstimatedTotalSize:N0} B ({combinedRatio:F2}x overall)");
    }

    if (pair.Candidates.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine($"{"block size",12}   {"combined total",16}   est. ratio");
        foreach (var candidate in pair.Candidates)
        {
            string marker = candidate.BlockSize == pair.RecommendedBlockSize ? " <- recommended" : "";
            double ratio = candidate.CombinedBestEstimatedTotalSize == 0 ? 0 : (double)totalOriginal / candidate.CombinedBestEstimatedTotalSize;
            Console.WriteLine($"{FormatSize(candidate.BlockSize),12}   {candidate.CombinedBestEstimatedTotalSize,14:N0} B   {ratio,8:F2}x{marker}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Recommended: {FormatSize(pair.RecommendedBlockSize)} blocks, base dict {FormatSize(pair.RecommendedBaseDictionarySize)}, " +
        $"target dict(s) {string.Join(", ", pair.RecommendedTargetDictionarySizes.Select(x => FormatSize(x)))} " +
        $"(analyzed in {(DateTime.UtcNow - started).TotalSeconds:F1}s). {EstimateDisclaimer()} A bigger block size trades away " +
        "random-access granularity for ratio - but ALSO, uniquely here, can badly hurt a base-patched target once it exceeds the fixed 16 KiB " +
        "base-patch window, which is why the winning size can differ sharply from what a target would recommend analyzed alone (see " +
        "BlockSizeAnalyzer.AnalyzePair's remarks). When in doubt, just run `ndz compress <targets...> --pair-out <out> --base <base> " +
        "--block-size auto --raw-dict auto` and let it pick these settings itself - that's exactly what this command previews.");
    return 0;
}

static void PrintDictionaryCurve(DictionaryAnalyzer.Result result, long originalSize)
{
    Console.WriteLine($"{"dict size",12}   {"est. total",14}   est. ratio");
    foreach (var point in result.Curve)
    {
        string marker = point.DictionarySize == result.RecommendedDictionarySize ? " <- recommended" : "";
        Console.WriteLine($"{FormatSize(point.DictionarySize),12}   {point.EstimatedTotalSize,10:N0} B   {point.EstimatedRatio(originalSize),8:F2}x{marker}");
    }
}

static string EstimateDisclaimer() =>
    "This is a sampled ESTIMATE - see DictionaryAnalyzer's remarks; a real pack at these " +
    "settings will typically do slightly better (filters aren't tried here) and is the only exact number.";

static int RunCompress(string[] args)
{
    string? inPath = null, outPath = null, basePath = null, pairOutPath = null;
    var positionals = new List<string>();
    int level = 19;
    int blockSize = NdzConstants.BlockSize;
    bool blockSizeAuto = false;
    int frameSize = NdzConstants.FrameSize;
    bool enableFilters = true;
    bool noVerify = false;
    int rawDictionarySize = 0;
    bool rawDictAuto = false;
    int maxDictSize = DictionaryAnalyzer.DefaultMaxDictionarySize;

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
            if (++i >= args.Length)
            {
                Console.Error.WriteLine($"--block-size requires a value: {FormatBlockSizeChoices()}, or 'auto'.");
                return 1;
            }
            if (string.Equals(args[i], "auto", StringComparison.OrdinalIgnoreCase))
            {
                blockSizeAuto = true;
            }
            else if (!int.TryParse(args[i], out blockSize) || !NdzConstants.SupportedBlockSizes.Contains(blockSize))
            {
                Console.Error.WriteLine($"--block-size must be one of {FormatBlockSizeChoices()}, or 'auto'.");
                return 1;
            }
        }
        else if (args[i] == "--frame-size")
        {
            if (++i >= args.Length || !TryParseSize(args[i], out frameSize))
            {
                Console.Error.WriteLine("--frame-size requires a size, e.g. 128k.");
                return 1;
            }
        }
        else if (args[i] == "--no-filters")
        {
            enableFilters = false;
        }
        else if (args[i] == "--no-verify")
        {
            noVerify = true;
        }
        else if (args[i] == "--raw-dict")
        {
            if (++i >= args.Length)
            {
                Console.Error.WriteLine("--raw-dict requires a size (e.g. 8m or 512k) or 'auto'.");
                return 1;
            }
            if (string.Equals(args[i], "auto", StringComparison.OrdinalIgnoreCase))
            {
                rawDictAuto = true;
            }
            else if (!TryParseSize(args[i], out rawDictionarySize))
            {
                Console.Error.WriteLine("--raw-dict requires a size (e.g. 8m or 512k) or 'auto'.");
                return 1;
            }
        }
        else if (args[i] == "--max-dict")
        {
            if (++i >= args.Length || !TryParseSize(args[i], out maxDictSize))
            {
                Console.Error.WriteLine("--max-dict requires a size, e.g. 8m.");
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
        else positionals.Add(args[i]);
    }

    List<string> targetPaths = new();
    if (pairOutPath != null)
    {
        // Pair mode takes one or more targets (a star topology: every one of them is
        // base-patched against the same shared --base, never against each other) - see
        // NdzPairWriter's own remarks. A single target is the common case (two ROMs in
        // total); nothing stops packing a whole family of similar releases together
        // (e.g. every regional/version release of one game) in one file.
        if (positionals.Count == 0)
        {
            Console.Error.WriteLine("Usage: ndz compress <target1.nds> [target2.nds ...] --pair-out <pair.ndz> --base <base.nds>");
            return 1;
        }
        targetPaths = positionals;
    }
    else
    {
        if (positionals.Count != 2)
        {
            Console.Error.WriteLine("Usage: ndz compress <in.nds> <out.ndz> [--level 1-19] [--block-size N] [--frame-size N]");
            Console.Error.WriteLine("                                       [--no-filters] [--raw-dict <size>] [--base <base.nds>] [--no-verify]");
            Console.Error.WriteLine("   or: ndz compress <target1.nds> [target2.nds ...] --pair-out <pair.ndz> --base <base.nds>");
            return 1;
        }
        inPath = positionals[0];
        outPath = positionals[1];
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
    if (blockSizeAuto && rawDictionarySize > 0)
    {
        Console.Error.WriteLine("--block-size auto can't be combined with an explicit --raw-dict <size> - " +
            "use --raw-dict auto (or omit --raw-dict) so both are chosen together, since the best dictionary " +
            "size depends on which block size gets picked.");
        return 1;
    }

    byte[]? romForAnalysis = null, baseForAnalysis = null;
    byte[][]? targetRomsForAnalysis = null;
    if (blockSizeAuto || rawDictAuto)
    {
        baseForAnalysis = basePath == null ? null : File.ReadAllBytes(basePath);
        if (pairOutPath != null)
            targetRomsForAnalysis = targetPaths.Select(File.ReadAllBytes).ToArray();
        else
            romForAnalysis = File.ReadAllBytes(inPath!);
    }

    int[]? targetDictionarySizes = null;
    if (blockSizeAuto)
    {
        if (pairOutPath != null)
        {
            // Pair mode: block size is shared by the base and every target (matches
            // ndztool.py's own --pair-out, which only ever takes one - and this
            // project's own N-target generalization keeps that a shared, star-topology
            // choice too), so it MUST be scored on their COMBINED total - not the base
            // ROM's self-contained curve alone. The base-patch window search always uses
            // a fixed 16 KiB window (BaseRomIndex.WindowSize/ndztool.py's own
            // NDZ_BASE_WINDOW, confirmed - it does not grow with block size), so a block
            // bigger than that only ever gets half-covered by one candidate window - a
            // bigger block size can look clearly better for the self-contained base while
            // badly hurting a base-patched target at the very same size. See
            // AnalyzePair's own remarks - evaluating block size from the base alone was a
            // real mistake caught here (a real 256 MB Pokemon Black/White pair went from
            // 91 MB to 127.6 MB after "helpfully" auto-picking 32 KiB blocks that way).
            var pairSweep = BlockSizeAnalyzer.AnalyzePair(baseForAnalysis!, targetRomsForAnalysis!, maxDictionarySize: maxDictSize, level: (CompressionType)level);
            blockSize = pairSweep.RecommendedBlockSize;
            rawDictionarySize = pairSweep.RecommendedBaseDictionarySize;
            targetDictionarySizes = pairSweep.RecommendedTargetDictionarySizes.ToArray();
            rawDictAuto = false;
            Console.WriteLine($"--block-size auto: recommended {FormatSize(blockSize)} " +
                $"(base dict {FormatSize(rawDictionarySize)}, target dict(s) {string.Join(", ", targetDictionarySizes.Select(x => FormatSize(x)))}).");
        }
        else
        {
            var blockSizeResult = BlockSizeAnalyzer.Analyze(romForAnalysis!, baseForAnalysis, maxDictionarySize: maxDictSize, level: (CompressionType)level);
            blockSize = blockSizeResult.RecommendedBlockSize;
            // Single-file mode already analyzed this exact rom/base/blockSize combination
            // as part of the sweep above - reuse it instead of analyzing it all over again.
            rawDictionarySize = blockSizeResult.RecommendedDictionarySize;
            rawDictAuto = false;
            Console.WriteLine($"--block-size auto: recommended {FormatSize(blockSize)}.");
        }
    }

    // Not a hardware limit like block size (no cap, no power-of-two requirement) - just
    // has to be able to hold at least one block. Matches ndztool.py's own
    // `if frame_size < block_size: sys.exit(...)`.
    if (frameSize <= 0 || frameSize < blockSize)
    {
        Console.Error.WriteLine("--frame-size must be >= --block-size.");
        return 1;
    }

    if (rawDictAuto)
    {
        if (pairOutPath != null)
        {
            // Pair mode: the base sub-pack and each base-patched target sub-pack have
            // very different dictionary economics (an already-near-perfect base-window
            // match leaves a dictionary nothing real to win, only its own storage cost
            // to add) - each analyzed independently rather than forcing one shared size
            // on all of them. See NdzPairWriter.Write's targetDictionarySizes remarks.
            var baseAnalysis = DictionaryAnalyzer.Analyze(baseForAnalysis!, null, maxDictSize, (CompressionType)level, blockSize);
            rawDictionarySize = baseAnalysis.RecommendedDictionarySize;
            targetDictionarySizes = targetRomsForAnalysis!
                .Select(t => DictionaryAnalyzer.Analyze(t, baseForAnalysis, maxDictSize, (CompressionType)level, blockSize).RecommendedDictionarySize)
                .ToArray();
            Console.WriteLine($"--raw-dict auto: base recommended {FormatSize(rawDictionarySize)}, " +
                $"target(s) recommended {string.Join(", ", targetDictionarySizes.Select(x => FormatSize(x)))} (cap {FormatSize(maxDictSize)}).");
        }
        else
        {
            var analysis = DictionaryAnalyzer.Analyze(romForAnalysis!, baseForAnalysis, maxDictSize, (CompressionType)level, blockSize);
            rawDictionarySize = analysis.RecommendedDictionarySize;
            Console.WriteLine($"--raw-dict auto: recommended {FormatSize(rawDictionarySize)} " +
                $"(sampled {analysis.SampledBlockCount:N0}/{analysis.TotalBlockCount:N0} blocks, cap {FormatSize(maxDictSize)}).");
        }
    }

    if (pairOutPath != null)
    {
        int?[]? targetDictionarySizesNullable = targetDictionarySizes?.Select(x => (int?)x).ToArray();
        NdzPairWriter.WriteFile(pairOutPath, basePath!, targetPaths, (CompressionType)level, blockSize, enableFilters, rawDictionarySize, frameSize, targetDictionarySizesNullable);
        long baseSize = new FileInfo(basePath!).Length;
        long targetsSize = targetPaths.Sum(p => new FileInfo(p).Length);
        long containerSize = new FileInfo(pairOutPath).Length;
        double pairRatio = containerSize == 0 ? 0 : (double)(baseSize + targetsSize) / containerSize;
        Console.WriteLine($"Wrote '{pairOutPath}': {baseSize + targetsSize:N0} -> {containerSize:N0} bytes ({pairRatio:F3}x) [1 base + {targetPaths.Count} target(s)].");

        if (!noVerify && !VerifyPairRoundTrip(pairOutPath, basePath!, targetPaths))
            return 1;
        return 0;
    }

    long originalSize = new FileInfo(inPath!).Length;
    NdzWriter.CompressFile(inPath!, outPath!, (CompressionType)level, blockSize, enableFilters, basePath, rawDictionarySize, frameSize);
    long compressedSize = new FileInfo(outPath!).Length;

    double ratio = originalSize == 0 ? 0 : (double)compressedSize / originalSize;
    Console.WriteLine($"Wrote '{outPath}': {originalSize:N0} -> {compressedSize:N0} bytes ({ratio:P1}).");

    if (!noVerify && !VerifySingleRoundTrip(outPath!, inPath!, basePath))
        return 1;
    return 0;
}

/// <summary>
/// Re-reads <paramref name="outPath"/> from disk (not the in-memory bytes just
/// compressed - so what's checked is the file as it actually landed, catching a
/// write-side bug too) and decodes it, comparing byte-for-byte against
/// <paramref name="inPath"/> - matches `ndztool.py`'s own default post-pack behavior
/// exactly (its own comment: "so what gets verified is the file on disk read back the
/// way this tool will actually read it"). On by default; skip with --no-verify.
/// </summary>
static bool VerifySingleRoundTrip(string outPath, string inPath, string? basePath)
{
    byte[] decoded;
    using (var archive = NdzArchive.Open(File.ReadAllBytes(outPath), basePath == null ? null : File.ReadAllBytes(basePath)))
        decoded = archive.DecompressAll();

    bool ok = decoded.AsSpan().SequenceEqual(File.ReadAllBytes(inPath));
    Console.WriteLine($"  roundtrip    {(ok ? "OK (byte-exact)" : "FAILED")}");
    return ok;
}

/// <summary>Like <see cref="VerifySingleRoundTrip"/>, but for a pair container: both entries, the patched one resolved against the freshly re-decoded base (not the original base bytes) - matches `ndztool.py`'s own pair-out verify exactly.</summary>
/// <summary>Verifies every entry in a (possibly N-way, star-topology) pair container - the shared base plus each target, matched to <paramref name="targetPaths"/> in file order (skipping the plain entry), matching NdzPairWriter's own known layout (base always entry 0, targets 1..N in the given order).</summary>
static bool VerifyPairRoundTrip(string pairOutPath, string basePath, IReadOnlyList<string> targetPaths)
{
    var pair = NdzPairContainer.ReadFile(pairOutPath);
    if (pair.Entries.Count != 1 + targetPaths.Count)
        throw new InvalidDataException($"Expected a {1 + targetPaths.Count}-entry pair container, got {pair.Entries.Count}.");

    bool ok = pair.DecompressEntry(pair.PlainEntryIndex).AsSpan().SequenceEqual(File.ReadAllBytes(basePath));

    int targetIndex = 0;
    for (int i = 0; i < pair.Entries.Count; i++)
    {
        if (i == pair.PlainEntryIndex)
            continue;
        bool entryOk = pair.DecompressEntry(i).AsSpan().SequenceEqual(File.ReadAllBytes(targetPaths[targetIndex]));
        if (!entryOk)
            Console.WriteLine($"  entry {i} ('{targetPaths[targetIndex]}')    FAILED");
        ok &= entryOk;
        targetIndex++;
    }

    Console.WriteLine($"  roundtrip    {(ok ? "OK (byte-exact)" : "FAILED")}");
    return ok;
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
    // Frame size isn't a fixed format-wide value (see NdzConstants.FrameSize's remarks) -
    // read back the file's own first frame rather than assume the default, so this stays
    // accurate for a file packed with a non-default --frame-size.
    long typicalFrameSize = seekTable.Count > 0 ? seekTable[0].DecompressedSize : 0;
    Console.WriteLine($"Frames:             {seekTable.Count:N0} ({typicalFrameSize:N0} bytes each, last frame may be shorter)");
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
