using System.Collections.ObjectModel;

namespace Ndz.Gui.ViewModels;

/// <summary>What a dropped/opened file in Examine turned out to be, driving how its <see cref="ExamineSourceViewModel.Entries"/> render.</summary>
public enum ExamineSourceKind
{
    /// <summary>A single self-contained (or, rarely, standalone base-patched) .ndz blob - exactly one entry.</summary>
    NdzSingle,

    /// <summary>An 'NDZP' pair/N-way container - one entry per bundled ROM (see <see cref="Compression.NdzPairContainer"/>).</summary>
    NdzPair,

    /// <summary>A raw, already-unpacked .nds/.dsi ROM, loaded here just to validate it - exactly one entry, unpackable.</summary>
    RawRom,
}

/// <summary>
/// One dropped/opened file in the Examine queue, grouped the way the user actually
/// dropped it - a pair container shows as one source row with all its bundled ROMs
/// listed underneath, not as N separate top-level rows.
/// </summary>
public sealed class ExamineSourceViewModel
{
    public string FilePath { get; }
    public string FileName { get; }
    public ExamineSourceKind Kind { get; }

    /// <summary>e.g. "pair container - 3 ROMs, 187.4 MiB" / "single .ndz" / "raw ROM".</summary>
    public string HeaderText { get; }

    public ObservableCollection<ExamineEntryViewModel> Entries { get; }

    public ExamineSourceViewModel(string filePath, ExamineSourceKind kind, string headerText, ObservableCollection<ExamineEntryViewModel> entries)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        Kind = kind;
        HeaderText = headerText;
        Entries = entries;
    }
}
