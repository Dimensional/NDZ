using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ndz.Core.Archives;
using Ndz.Core.Compression;
using Ndz.Core.Format;
using Ndz.Core.XDelta;
using Ndz.Gui.Models;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// One ROM loaded into the Pack queue - a decoded icon/title/id plus enough metadata to
/// render a card. Doubles as a base when <see cref="Targets"/> is non-empty: the pair
/// format is a star topology (one base, N base-patched targets, no further nesting), so a
/// target's own <see cref="Targets"/> is always left empty - nothing adds to it, and the
/// UI never renders a "+" strip on a target chip in the first place. Grouping only ever
/// happens by dropping a fresh file from Explorer onto a card's "+" strip (see
/// <see cref="MainWindowViewModel.AddTargetsAsync"/>) - dragging one already-queued card
/// onto another isn't a supported gesture.
/// </summary>
public partial class RomEntryViewModel : ViewModelBase
{
    /// <summary>
    /// Leaves two logical cores free for the UI thread, the OS, and everything else
    /// running on the machine, rather than the Core analyzers'/writers' own default of
    /// unbounded (every core - fine for the CLI, a real problem for a window that needs
    /// to keep feeling alive while this runs in the background). Real-world motivation: a
    /// full pack at level 19 legitimately can occupy every core by design (matches the
    /// reference packer's own multi-threaded approach), which made an actual GUI session
    /// feel like it had hung even though the UI thread itself was never blocked - just
    /// starved of scheduler time. **Widened from one reserved core to two (2026-09-08)**
    /// after a real session on a multi-target pair pack (base + several targets, each
    /// sourced from a separate .zip - see <see cref="RomSource"/>) still nearly hung the
    /// whole machine at the one-core buffer - one spare core wasn't enough headroom in
    /// practice, particularly on a lower-core-count machine where "every core but one" is
    /// still nearly total saturation.
    /// </summary>
    private static readonly int MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>
    /// Bindable mirror of <see cref="PairContainerPolicy.CreationEnabled"/> - drives
    /// whether a card's "+" target strip shows at all (see MainWindow.axaml). Flipping
    /// the Core flag back on re-enables this automatically, nothing here to touch. Only
    /// meaningful for a raw-ROM card in the first place - see <see cref="ShowPairTargetSection"/>.
    /// </summary>
    public bool PairPackingEnabled => PairContainerPolicy.CreationEnabled;

    /// <summary>
    /// Whether to show the pair-target strip/disabled-message block at all - a packed-base
    /// card (<see cref="IsPackedBase"/>) shows neither, since <see cref="NdzPairWriter"/>
    /// needs a raw base ROM and there'd be nothing informative about calling out the
    /// unrelated <see cref="PairContainerPolicy"/> gate on a card the gate was never about.
    /// </summary>
    public bool ShowPairTargetSection => !IsPackedBase;

    /// <summary>Whether to show this card's own Analyze/Pack/dictionary/block-size controls - hidden for a packed-base card (<see cref="IsPackedBase"/>), which is already packed and has nothing left to decide about itself.</summary>
    public bool ShowFullPackControls => !IsPackedBase;

    private readonly Action<RomEntryViewModel> _onRemove;

    /// <summary>Where this ROM's bytes actually come from - a real file, or one entry inside a .zip/.7z/.rar archive (see <see cref="RomSource"/>). Re-read from here whenever the real bytes are needed, never kept resident.</summary>
    public RomSource Source { get; }

    public Bitmap Icon { get; }
    public string ShortTitle { get; }
    public string FullTitle { get; }
    public string GameCode { get; }

    /// <summary>
    /// The header's revision byte (0x1E) - usually 0, but distinguishes re-releases
    /// sharing the same <see cref="GameCode"/> (e.g. a Wii U Virtual Console dump vs. the
    /// original cartridge). Part of the duplicate-detection key alongside
    /// <see cref="GameCode"/> - see <see cref="MainWindowViewModel"/>.
    /// </summary>
    public byte RomVersion { get; }

    /// <summary>Non-empty only when <see cref="RomVersion"/> isn't the default 0 - most ROMs have nothing worth calling out here.</summary>
    public string RevisionText { get; }

    public bool HasRevision => !string.IsNullOrEmpty(RevisionText);

    /// <summary>
    /// Derived from the header's own unit-code byte (0x12), not the banner's version field
    /// - the authoritative platform classification, e.g. "NDS + DSi" for Pokemon
    /// Black/White regardless of what a .nds/.dsi extension happened to suggest.
    /// </summary>
    public string PlatformText { get; }

    /// <summary>Non-empty only when the banner's own version field marks it as carrying an animated icon sequence - unrelated to <see cref="PlatformText"/>, and not decoded (see <see cref="Ndz.Core.Format.NdsIcon"/>).</summary>
    public string BannerNote { get; }

    public bool HasBannerNote => !string.IsNullOrEmpty(BannerNote);

    /// <summary>
    /// The "USA / Europe / Japan"-style release territory, from the game code's own 4th
    /// character (see <see cref="Ndz.Core.Format.NdsRomInfo.DestinationLabel"/>) - what
    /// most people mean by "region". Always present (every game code has a 4th
    /// character), unlike <see cref="RegionLockText"/>.
    /// </summary>
    public string DestinationText { get; }

    /// <summary>
    /// A different, rarely-relevant hardware field GBATEK happens to also call "Region"
    /// (see <see cref="Ndz.Core.Format.NdsRomInfo.RegionLockLabel"/>) - already empty
    /// (from Core) when there's nothing worth claiming, including on any DSi title, where
    /// the byte is confirmed unreliable.
    /// </summary>
    public string RegionLockText { get; }

    public bool HasRegionLock => !string.IsNullOrEmpty(RegionLockText);

    public string SourceFileText { get; }

    /// <summary>
    /// A composed multi-line hover summary (full title, platform/region/revision, full
    /// file path) for the card and chip templates' tooltips - everything the compact card
    /// truncates away is still one hover away.
    /// </summary>
    public string TooltipText { get; }

    /// <summary>
    /// True when another loaded ROM - anywhere, a top-level card or a target chip nested
    /// under any base - already carries the same <see cref="GameCode"/> AND
    /// <see cref="RomVersion"/>. Recomputed over everything currently loaded after every
    /// add/remove - a same-code-and-revision heuristic, not a full-ROM hash comparison
    /// (cheap, and two files sharing both are effectively always the same content) -
    /// deliberately NOT keyed on GameCode alone, since a same-code re-release (e.g. a
    /// Virtual Console dump) can carry a different RomVersion and is genuinely different
    /// content worth keeping both of.
    /// </summary>
    [ObservableProperty]
    private bool _isDuplicate;

    /// <summary>This card's patch targets, if any - rendered as small chips, not full cards. Empty for a target itself (see the class remarks).</summary>
    public ObservableCollection<RomEntryViewModel> Targets { get; } = [];

    /// <summary>
    /// This card's `.delta.ndz` hack jobs, if any - a genuinely different relationship
    /// from <see cref="Targets"/> (see <see cref="HackTargetViewModel"/>'s own remarks):
    /// each one needs an already-packed base `.ndz` (or builds one) and always produces
    /// its own standalone output file, never merged with anything. Never populated for a
    /// nested target itself, same as <see cref="Targets"/> - the star topology has no
    /// further nesting.
    /// </summary>
    public ObservableCollection<HackTargetViewModel> HackTargets { get; } = [];

    /// <summary>The curated block-size menu (see <see cref="NdzConstants.SupportedBlockSizes"/>) plus <see cref="SizeOption.Auto"/> - fixed, doesn't grow from Analyze the way <see cref="DictionarySizeOptions"/> does, since there's already a real curated Core menu for this one.</summary>
    public ObservableCollection<SizeOption> BlockSizeOptions { get; } =
        new([SizeOption.Auto, .. NdzConstants.SupportedBlockSizes.Select(SizeOption.FromBytes)]);

    [ObservableProperty]
    private SizeOption _selectedBlockSize = SizeOption.Auto;

    /// <summary>
    /// Starts with just <see cref="SizeOption.Auto"/> - unlike block size, there's no
    /// fixed curated dictionary-size menu in Core (the CLI takes freeform sizes via
    /// `--raw-dict`), so real choices only appear once Analyze actually computes one for
    /// this specific ROM.
    /// </summary>
    public ObservableCollection<SizeOption> DictionarySizeOptions { get; } = [SizeOption.Auto];

    [ObservableProperty]
    private SizeOption _selectedDictionarySize = SizeOption.Auto;

    [ObservableProperty]
    private bool _isAnalyzing;

    public string AnalyzeButtonText => IsAnalyzing ? "Analyzing…" : "Analyze";

    partial void OnIsAnalyzingChanged(bool value) => OnPropertyChanged(nameof(AnalyzeButtonText));

    [ObservableProperty]
    private string? _analysisResultText;

    public bool HasAnalysisResult => !string.IsNullOrEmpty(AnalysisResultText);

    [ObservableProperty]
    private string? _analysisError;

    public bool HasAnalysisError => !string.IsNullOrEmpty(AnalysisError);

    // [ObservableProperty]-generated partial hooks, called after the backing field changes
    // and its own PropertyChanged fires - used here just to keep the dependent Has* flags
    // in sync, since they're plain computed properties, not [ObservableProperty] fields
    // themselves.
    partial void OnAnalysisResultTextChanged(string? value) => OnPropertyChanged(nameof(HasAnalysisResult));
    partial void OnAnalysisErrorChanged(string? value) => OnPropertyChanged(nameof(HasAnalysisError));

    [ObservableProperty]
    private bool _isPacking;

    /// <summary>
    /// Adapts to what this one button actually does: a plain solo/pair pack (no hacks
    /// attached), or - once one or more <see cref="HackTargets"/> are attached - the
    /// combined build <see cref="PackWithHacksAsync"/> performs (the base too, if this
    /// card is a raw ROM rather than an already-packed one). One button, one action,
    /// whatever that action currently is - not a second button competing for attention.
    /// </summary>
    public string PackButtonText
    {
        get
        {
            if (IsPacking)
                return "Packing…";
            if (HackTargets.Count == 0)
                return "Pack";
            string hacks = $"{HackTargets.Count} hack{(HackTargets.Count == 1 ? "" : "s")}";
            return IsPackedBase ? $"Build {hacks}" : $"Build base + {hacks}";
        }
    }

    /// <summary>
    /// Whether the pack button (and its result/error) shows at all - hidden only for a
    /// packed-base card with no hack targets yet attached, since there's nothing at all to
    /// do with it until then (it's already packed, and building a hack is the only thing
    /// this button now does for that kind of card).
    /// </summary>
    public bool ShowPackButton => !IsPackedBase || HackTargets.Count > 0;

    partial void OnIsPackingChanged(bool value) => OnPropertyChanged(nameof(PackButtonText));

    [ObservableProperty]
    private string? _packResultText;

    public bool HasPackResult => !string.IsNullOrEmpty(PackResultText);

    [ObservableProperty]
    private string? _packError;

    public bool HasPackError => !string.IsNullOrEmpty(PackError);

    partial void OnPackResultTextChanged(string? value) => OnPropertyChanged(nameof(HasPackResult));
    partial void OnPackErrorChanged(string? value) => OnPropertyChanged(nameof(HasPackError));

    /// <summary>
    /// True for a card sourced from an already-packed .ndz rather than a raw ROM (dropped
    /// straight onto the Pack tab - see <see cref="MainWindowViewModel.AddPathsAsync"/>'s
    /// own remarks). Not something to pack again - it already is - so this card shows
    /// neither pair-target nor Analyze/Pack controls, only its <see cref="HackTargets"/>
    /// section: it exists purely to be a hack's base with zero extra configuration, since
    /// <see cref="Source"/>'s own bytes already ARE the packed .ndz a hack needs, and its
    /// raw ROM bytes (for the actual byte-matching) are one <c>NdzArchive.Open(...).DecompressAll()</c>
    /// away - see <see cref="HackTargetViewModel"/>'s own remarks on why this beats needing
    /// a separate build-or-browse step per hack target.
    /// </summary>
    public bool IsPackedBase { get; }

    public RomEntryViewModel(
        RomSource source,
        Bitmap icon,
        string shortTitle,
        string fullTitle,
        string gameCode,
        byte unitCode,
        string destinationLabel,
        string regionLockLabel,
        byte romVersion,
        ushort bannerVersion,
        long fileSizeBytes,
        bool isPackedBase,
        Action<RomEntryViewModel> onRemove)
    {
        Source = source;
        Icon = icon;
        ShortTitle = shortTitle;
        FullTitle = fullTitle;
        GameCode = gameCode;
        RomVersion = romVersion;
        IsPackedBase = isPackedBase;
        RevisionText = romVersion != 0 ? $"Rev {romVersion}" : string.Empty;
        PlatformText = unitCode switch
        {
            0x00 => "NDS",
            0x02 => "NDS + DSi",
            0x03 => "DSi-exclusive",
            _ => $"Unknown platform (0x{unitCode:X2})",
        };
        BannerNote = bannerVersion >= 0x0103 ? "Animated icon (not rendered)" : string.Empty;
        DestinationText = destinationLabel;
        RegionLockText = string.IsNullOrEmpty(regionLockLabel) ? string.Empty : $"Region-locked: {regionLockLabel}";
        SourceFileText = $"{source.ShortLabel} · {fileSizeBytes / (1024.0 * 1024.0):0.#} MB";
        _onRemove = onRemove;

        // A packed base's front matter carries no unit-code/region byte (only a raw ROM's
        // header does), so PlatformText/DestinationText above are meaningless placeholders
        // for one - left out of the tooltip rather than shown as if they were real.
        var tooltipLines = new List<string>();
        if (!string.IsNullOrEmpty(fullTitle))
            tooltipLines.Add(fullTitle);
        if (isPackedBase)
            tooltipLines.Add($"{gameCode} · packed base (.ndz)");
        else
            tooltipLines.Add($"{gameCode}{(HasRevision ? " · " + RevisionText : "")} · {PlatformText} · {DestinationText}");
        if (!isPackedBase && HasRegionLock)
            tooltipLines.Add(RegionLockText);
        if (!isPackedBase && HasBannerNote)
            tooltipLines.Add(BannerNote);
        tooltipLines.Add(source.FullLabel);
        TooltipText = string.Join("\n\n", tooltipLines);

        HackTargets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PackButtonText));
            OnPropertyChanged(nameof(ShowPackButton));
        };
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);

    /// <summary>
    /// Runs the real Core analyzer against this card's actual ROM bytes - re-read via
    /// <see cref="Source"/> here rather than kept resident since load time (a queue of
    /// dozens of ROMs staying fully loaded in memory just in case someone clicks Analyze
    /// would be a real cost for an occasional action; for an archive-sourced ROM this
    /// re-decompresses that entry from the archive again rather than ever having written
    /// it to disk - see <see cref="RomSource"/>). Uses <see cref="BlockSizeAnalyzer.AnalyzePair"/>
    /// when this card has targets (scored on base+targets combined - see that method's own
    /// remarks on why analyzing the base alone would pick a block size that badly hurts
    /// base-patched targets), or the plain single-ROM <see cref="BlockSizeAnalyzer.Analyze"/>
    /// otherwise. Both are real zstd-level-19 compression sampling passes, not
    /// instant - always off the UI thread.
    /// </summary>
    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        IsAnalyzing = true;
        AnalysisError = null;
        try
        {
            if (Targets.Count == 0)
            {
                byte[] rom = await Task.Run(() => Source.ReadBytes());
                BlockSizeAnalyzer.Result result = await Task.Run(() => BlockSizeAnalyzer.Analyze(rom, maxDegreeOfParallelism: MaxDegreeOfParallelism));

                var recommended = result.Candidates.First(c => c.BlockSize == result.RecommendedBlockSize);
                double ratio = recommended.BestEstimatedTotalSize <= 0 ? 0 : (double)rom.LongLength / recommended.BestEstimatedTotalSize;

                ApplyBlockSize(result.RecommendedBlockSize);
                ApplyDictionarySize(result.RecommendedDictionarySize);
                AnalysisResultText = $"Recommended: {SizeOption.FromBytes(result.RecommendedBlockSize).Label} block · " +
                    $"{SizeOption.FromBytes(result.RecommendedDictionarySize).Label} dict · ~{ratio:0.#}x";
            }
            else
            {
                byte[] baseRom = await Task.Run(() => Source.ReadBytes());
                byte[][] targetRoms = await Task.Run(() => Targets.Select(t => t.Source.ReadBytes()).ToArray());

                BlockSizeAnalyzer.PairResult result = await Task.Run(() => BlockSizeAnalyzer.AnalyzePair(baseRom, targetRoms, maxDegreeOfParallelism: MaxDegreeOfParallelism));

                var recommended = result.Candidates.First(c => c.BlockSize == result.RecommendedBlockSize);
                long originalSize = baseRom.LongLength + targetRoms.Sum(t => (long)t.LongLength);
                double ratio = recommended.CombinedBestEstimatedTotalSize <= 0 ? 0 : (double)originalSize / recommended.CombinedBestEstimatedTotalSize;

                ApplyBlockSize(result.RecommendedBlockSize);
                ApplyDictionarySize(result.RecommendedBaseDictionarySize);

                // Each target's own dictionary is analyzed and recommended independently
                // (see AnalyzePair's remarks) - applied to that target's own dropdown, a
                // real RomEntryViewModel instance in its own right (private access to a
                // sibling instance's members is legal within the declaring type), not
                // just reported as text.
                for (int i = 0; i < Targets.Count; i++)
                    Targets[i].ApplyDictionarySize(result.RecommendedTargetDictionarySizes[i]);

                string targetDicts = string.Join(", ", result.RecommendedTargetDictionarySizes.Select(t => SizeOption.FromBytes(t).Label));
                AnalysisResultText = $"Recommended: {SizeOption.FromBytes(result.RecommendedBlockSize).Label} block · " +
                    $"base dict {SizeOption.FromBytes(result.RecommendedBaseDictionarySize).Label} · " +
                    $"target dict(s) {targetDicts} · ~{ratio:0.#}x";
            }
        }
        catch (Exception ex)
        {
            AnalysisError = $"Analyze failed: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private void ApplyBlockSize(int bytes) =>
        SelectedBlockSize = BlockSizeOptions.First(o => o.Bytes == bytes);

    private void ApplyDictionarySize(int bytes)
    {
        if (!DictionarySizeOptions.Any(o => o.Bytes == bytes))
            DictionarySizeOptions.Add(SizeOption.FromBytes(bytes));
        SelectedDictionarySize = DictionarySizeOptions.First(o => o.Bytes == bytes);
    }

    /// <summary>
    /// Packs this card to <paramref name="outputPath"/> - the destination is resolved by
    /// the view (a save-file dialog needs a Window/StorageProvider this plain view model
    /// doesn't have), everything else happens here. Block size is shared (one choice for
    /// the whole pack, base and every target - the format itself only ever has one block
    /// size per file). Dictionary size is fully independent per ROM: this card's own
    /// dropdown decides the base's, and each target chip has its own separate dropdown
    /// (also a real RomEntryViewModel, just nested) that decides that target's - "Auto"
    /// on either resolves via the real Core analyzers at pack time, never silently
    /// skipped, same as the CLI's own `--block-size auto`/`--raw-dict auto`; an explicit
    /// selection is used as-is. Nothing here broadcasts one shared dictionary size to
    /// every target - each is read from that specific target's own selection.
    /// Round-trip-verifies the written file before reporting success, on by default,
    /// matching the CLI's own default (`ndztool.py`'s comment: "so what gets verified is
    /// the file on disk read back the way this tool will actually read it").
    /// </summary>
    public async Task PackAsync(string outputPath)
    {
        IsPacking = true;
        PackError = null;
        PackResultText = null;
        try
        {
            long outputSize = await Task.Run(() => PackAndVerify(outputPath));
            PackResultText = $"Packed \"{Path.GetFileName(outputPath)}\" ({SizeOption.FromBytes((int)Math.Min(outputSize, int.MaxValue)).Label}) - verified byte-exact.";
        }
        catch (Exception ex)
        {
            PackError = $"Pack failed: {ex.Message}";
        }
        finally
        {
            IsPacking = false;
        }
    }

    private long PackAndVerify(string outputPath)
    {
        byte[] baseRom = Source.ReadBytes();
        int? blockSizeOverride = SelectedBlockSize.Bytes;
        int? dictSizeOverride = SelectedDictionarySize.Bytes;

        if (Targets.Count == 0)
        {
            int blockSize = blockSizeOverride ?? BlockSizeAnalyzer.Analyze(baseRom, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedBlockSize;
            int dictSize = dictSizeOverride ?? DictionaryAnalyzer.Analyze(baseRom, null, blockSize: blockSize, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedDictionarySize;

            using (FileStream output = File.Create(outputPath))
                NdzWriter.Compress(baseRom, output, blockSize: blockSize, rawDictionarySize: dictSize, maxDegreeOfParallelism: MaxDegreeOfParallelism);

            byte[] decoded;
            using (NdzArchive archive = NdzArchive.Open(File.ReadAllBytes(outputPath)))
                decoded = archive.DecompressAll();
            if (!decoded.AsSpan().SequenceEqual(baseRom))
                throw new InvalidDataException("round-trip check failed - the packed file didn't decode back to the original ROM byte-for-byte.");
        }
        else
        {
            byte[][] targetRoms = Targets.Select(t => t.Source.ReadBytes()).ToArray();
            int blockSize = blockSizeOverride ?? BlockSizeAnalyzer.AnalyzePair(baseRom, targetRoms, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedBlockSize;

            int baseDictSize = dictSizeOverride ?? DictionaryAnalyzer.Analyze(baseRom, null, blockSize: blockSize, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedDictionarySize;

            // Each target's dictionary is its own independent choice now (its own
            // dropdown, a real per-target control) - Auto there resolves independently
            // per target here, same as the base does above; an explicit per-target
            // selection is used as-is. Nothing here broadcasts one shared size to every
            // target anymore.
            var targetDictSizes = new int?[targetRoms.Length];
            for (int i = 0; i < targetRoms.Length; i++)
            {
                int? targetOverride = Targets[i].SelectedDictionarySize.Bytes;
                targetDictSizes[i] = targetOverride ?? DictionaryAnalyzer.Analyze(targetRoms[i], baseRom, blockSize: blockSize, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedDictionarySize;
            }

            using (FileStream output = File.Create(outputPath))
                NdzPairWriter.Write(output, baseRom, targetRoms, blockSize: blockSize, rawDictionarySize: baseDictSize, targetDictionarySizes: targetDictSizes, maxDegreeOfParallelism: MaxDegreeOfParallelism);

            NdzPairContainer pair = NdzPairContainer.ReadFile(outputPath);
            if (pair.Entries.Count != 1 + targetRoms.Length)
                throw new InvalidDataException($"wrote a {pair.Entries.Count}-entry pair container, expected {1 + targetRoms.Length}.");

            if (!pair.DecompressEntry(pair.PlainEntryIndex).AsSpan().SequenceEqual(baseRom))
                throw new InvalidDataException("round-trip check failed on the base entry.");

            int targetIndex = 0;
            for (int i = 0; i < pair.Entries.Count; i++)
            {
                if (i == pair.PlainEntryIndex)
                    continue;
                if (!pair.DecompressEntry(i).AsSpan().SequenceEqual(targetRoms[targetIndex]))
                    throw new InvalidDataException($"round-trip check failed on target entry \"{Targets[targetIndex].ShortTitle}\".");
                targetIndex++;
            }
        }

        return new FileInfo(outputPath).Length;
    }

    /// <summary>
    /// Builds this base AND every attached <see cref="HackTargets"/> job in one combined
    /// action, into <paramref name="outputFolder"/> - the GUI equivalent of the CLI's own
    /// `pack-hack --build-base` convenience, generalized to N hack targets at once. A
    /// packed-base card (<see cref="IsPackedBase"/>) skips straight to building each hack,
    /// reusing its own already-packed bytes; a raw-ROM card builds its own base .ndz first
    /// (named from <see cref="ShortTitle"/>, using this card's own selected block/dictionary
    /// size, Auto resolving exactly like a plain solo pack would), then reuses those same
    /// bytes for every hack. Each hack target's own delta is named from ITS OWN
    /// <see cref="HackTargetViewModel.ShortTitle"/> and round-trip verified independently -
    /// one failing doesn't stop the rest, all outcomes are reported together.
    /// </summary>
    public async Task PackWithHacksAsync(string outputFolder)
    {
        IsPacking = true;
        PackError = null;
        PackResultText = null;
        try
        {
            string summary = await Task.Run(() => PackWithHacksCore(outputFolder));
            PackResultText = summary;
        }
        catch (Exception ex)
        {
            PackError = $"Build failed: {ex.Message}";
        }
        finally
        {
            IsPacking = false;
        }
    }

    private string PackWithHacksCore(string outputFolder)
    {
        byte[] baseRom;
        byte[] baseNdzBytes;
        string baseSummary;

        if (IsPackedBase)
        {
            baseNdzBytes = Source.ReadBytes();
            using (NdzArchive archive = NdzArchive.Open(baseNdzBytes))
                baseRom = archive.DecompressAll();
            baseSummary = $"base \"{ShortTitle}\" (already packed)";
        }
        else
        {
            baseRom = Source.ReadBytes();
            int blockSize = SelectedBlockSize.Bytes ?? BlockSizeAnalyzer.Analyze(baseRom, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedBlockSize;
            int dictSize = SelectedDictionarySize.Bytes ?? DictionaryAnalyzer.Analyze(baseRom, null, blockSize: blockSize, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedDictionarySize;

            string basePath = UniquePath(Path.Combine(outputFolder, SanitizeFileName(ShortTitle) + ".ndz"));
            using (FileStream output = File.Create(basePath))
                NdzWriter.Compress(baseRom, output, blockSize: blockSize, rawDictionarySize: dictSize, maxDegreeOfParallelism: MaxDegreeOfParallelism);
            baseNdzBytes = File.ReadAllBytes(basePath);

            using (NdzArchive archive = NdzArchive.Open(baseNdzBytes))
            {
                if (!archive.DecompressAll().AsSpan().SequenceEqual(baseRom))
                    throw new InvalidDataException("round-trip check failed on the base.");
            }
            baseSummary = $"base \"{Path.GetFileName(basePath)}\" ({SizeOption.FromBytes((int)Math.Min(new FileInfo(basePath).Length, int.MaxValue)).Label})";
        }

        var hackSummaries = new List<string>();
        var hackErrors = new List<string>();
        foreach (HackTargetViewModel hack in HackTargets)
        {
            try
            {
                byte[] targetRom = hack.Kind == HackTargetKind.XdeltaPatch
                    ? XDeltaCodec.Apply(baseRom, hack.Source.ReadBytes())
                    : hack.Source.ReadBytes();

                int blockSize = BlockSizeAnalyzer.Analyze(targetRom, maxDegreeOfParallelism: MaxDegreeOfParallelism).RecommendedBlockSize;
                string deltaPath = UniquePath(Path.Combine(outputFolder, SanitizeFileName(hack.ShortTitle) + ".delta.ndz"));

                using (FileStream output = File.Create(deltaPath))
                    HackContainerWriter.Compress(targetRom, baseRom, baseNdzBytes, output, blockSize: blockSize, maxDegreeOfParallelism: MaxDegreeOfParallelism);

                using (NdzArchive archive = NdzArchive.Open(File.ReadAllBytes(deltaPath), baseNdzBytes: baseNdzBytes))
                {
                    if (!archive.DecompressAll().AsSpan().SequenceEqual(targetRom))
                        throw new InvalidDataException("round-trip check failed.");
                }

                hackSummaries.Add($"\"{Path.GetFileName(deltaPath)}\" ({SizeOption.FromBytes((int)Math.Min(new FileInfo(deltaPath).Length, int.MaxValue)).Label})");
            }
            catch (Exception ex)
            {
                hackErrors.Add($"\"{hack.ShortTitle}\": {ex.Message}");
            }
        }

        var parts = new List<string> { $"Built {baseSummary}" };
        if (hackSummaries.Count > 0)
            parts.Add($"{hackSummaries.Count} hack{(hackSummaries.Count == 1 ? "" : "s")}: {string.Join(", ", hackSummaries)}");
        if (hackErrors.Count > 0)
            parts.Add($"{hackErrors.Count} failed: {string.Join("; ", hackErrors)}");
        return string.Join(". ", parts) + " - verified byte-exact.";
    }

    /// <summary>Appends " (2)", " (3)", ... rather than silently overwriting an unrelated file that happens to already have this exact derived name in the chosen folder.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        // Handles the ".delta.ndz" double-extension case too: GetFileNameWithoutExtension
        // only strips the last segment, so "Foo.delta" + ".ndz" becomes "Foo.delta (2).ndz" -
        // still unique, still clearly named, just not re-splitting ".delta" out specially.
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Where(c => !invalid.Contains(c)).ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length > 0 ? cleaned : "output";
    }
}
