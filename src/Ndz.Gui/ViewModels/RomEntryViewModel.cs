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
    /// the Core flag back on re-enables this automatically, nothing here to touch.
    /// </summary>
    public bool PairPackingEnabled => PairContainerPolicy.CreationEnabled;

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

    public string PackButtonText => IsPacking ? "Packing…" : "Pack";

    partial void OnIsPackingChanged(bool value) => OnPropertyChanged(nameof(PackButtonText));

    [ObservableProperty]
    private string? _packResultText;

    public bool HasPackResult => !string.IsNullOrEmpty(PackResultText);

    [ObservableProperty]
    private string? _packError;

    public bool HasPackError => !string.IsNullOrEmpty(PackError);

    partial void OnPackResultTextChanged(string? value) => OnPropertyChanged(nameof(HasPackResult));
    partial void OnPackErrorChanged(string? value) => OnPropertyChanged(nameof(HasPackError));

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
        Action<RomEntryViewModel> onRemove)
    {
        Source = source;
        Icon = icon;
        ShortTitle = shortTitle;
        FullTitle = fullTitle;
        GameCode = gameCode;
        RomVersion = romVersion;
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

        var tooltipLines = new List<string>();
        if (!string.IsNullOrEmpty(fullTitle))
            tooltipLines.Add(fullTitle);
        tooltipLines.Add($"{gameCode}{(HasRevision ? " · " + RevisionText : "")} · {PlatformText} · {DestinationText}");
        if (HasRegionLock)
            tooltipLines.Add(RegionLockText);
        if (HasBannerNote)
            tooltipLines.Add(BannerNote);
        tooltipLines.Add(source.FullLabel);
        TooltipText = string.Join("\n\n", tooltipLines);
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
}
