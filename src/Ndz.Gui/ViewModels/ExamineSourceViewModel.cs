using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

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
public partial class ExamineSourceViewModel : ViewModelBase
{
    public string FilePath { get; }
    public string FileName { get; }
    public ExamineSourceKind Kind { get; }

    /// <summary>e.g. "pair container - 3 ROMs, 187.4 MiB" / "single .ndz" / "raw ROM".</summary>
    public string HeaderText { get; }

    public ObservableCollection<ExamineEntryViewModel> Entries { get; }

    /// <summary>
    /// Only worth offering on a container that actually holds more than one unpackable
    /// ROM - a single-entry source (a standalone .ndz, or a raw ROM with nothing to
    /// unpack at all) already has its own per-entry Unpack button doing the same thing.
    /// </summary>
    public bool ShowUnpackAll => Entries.Count(e => e.CanUnpack) > 1;

    [ObservableProperty]
    private bool _isUnpackingAll;

    public string UnpackAllButtonText => IsUnpackingAll ? "Unpacking…" : "Unpack All";

    partial void OnIsUnpackingAllChanged(bool value) => OnPropertyChanged(nameof(UnpackAllButtonText));

    [ObservableProperty]
    private string? _unpackAllResultText;

    public bool HasUnpackAllResult => !string.IsNullOrEmpty(UnpackAllResultText);

    [ObservableProperty]
    private string? _unpackAllError;

    public bool HasUnpackAllError => !string.IsNullOrEmpty(UnpackAllError);

    partial void OnUnpackAllResultTextChanged(string? value) => OnPropertyChanged(nameof(HasUnpackAllResult));
    partial void OnUnpackAllErrorChanged(string? value) => OnPropertyChanged(nameof(HasUnpackAllError));

    public ExamineSourceViewModel(string filePath, ExamineSourceKind kind, string headerText, ObservableCollection<ExamineEntryViewModel> entries)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Kind = kind;
        HeaderText = headerText;
        Entries = entries;
    }

    /// <summary>
    /// Unpacks every unpackable entry into <paramref name="outputFolder"/> in one go,
    /// each to its own file - reuses each entry's own <see cref="ExamineEntryViewModel.UnpackAsync"/>
    /// (so every entry's own result/error banner updates individually too, not just this
    /// aggregate summary), just orchestrating the file names and the overall count. A
    /// name is built from the entry's own decoded title plus its game code
    /// (<c>"Mega Man Star Force [A6CE].nds"</c>) - not a raw filename field, since the
    /// format has no such field today (see the Reserved-space remarks in
    /// docs/ndz-format-spec.md; a future format update might add one). Titles collide
    /// constantly within one pair container (every entry in a 6-ROM Star Force pack
    /// shares the same title), so the game code is always included, not just appended on
    /// an actual collision - a numeric suffix is still the last-resort fallback for the
    /// genuinely rare case two entries share both.
    /// </summary>
    public async Task UnpackAllAsync(string outputFolder)
    {
        IsUnpackingAll = true;
        UnpackAllError = null;
        UnpackAllResultText = null;
        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int succeeded = 0, failed = 0, needsBase = 0, total = 0;

            foreach (ExamineEntryViewModel entry in Entries)
            {
                if (!entry.CanUnpack)
                    continue;
                total++;

                // A standalone base-patched .ndz (never a pair-container entry - those
                // always resolve their own base internally) needs a base ROM this bulk
                // operation has no window to prompt for - real in practice only for a
                // single-entry source, where ShowUnpackAll is false anyway and this path
                // is just a defensive fallback, not something a user can actually trigger
                // today.
                if (entry.RequiresExternalBaseRom)
                {
                    needsBase++;
                    continue;
                }

                string path = Path.Combine(outputFolder, MakeUniqueFileName(entry, usedNames));
                await entry.UnpackAsync(path, externalBaseRom: null);
                if (entry.HasUnpackError)
                    failed++;
                else
                    succeeded++;
            }

            var parts = new List<string> { $"Unpacked {succeeded} of {total} ROM(s) to \"{Path.GetFileName(outputFolder)}\"" };
            if (failed > 0)
                parts.Add($"{failed} failed - see each entry below");
            if (needsBase > 0)
                parts.Add($"{needsBase} need{(needsBase == 1 ? "s" : "")} its base ROM - use its own Unpack button");
            UnpackAllResultText = string.Join(", ", parts) + ".";
        }
        catch (Exception ex)
        {
            UnpackAllError = $"Unpack All failed: {ex.Message}";
        }
        finally
        {
            IsUnpackingAll = false;
        }
    }

    private static string MakeUniqueFileName(ExamineEntryViewModel entry, HashSet<string> usedNames)
    {
        string baseName = $"{SanitizeFileName(entry.ShortTitle)} [{entry.GameCode}]";
        string candidate = baseName;
        for (int suffix = 2; !usedNames.Add(candidate); suffix++)
            candidate = $"{baseName} ({suffix})";
        return candidate + ".nds";
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Where(c => !invalid.Contains(c)).ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length > 0 ? cleaned : "output";
    }
}
