using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Ndz.Gui.Services;

/// <summary>The four theme choices this app offers - see the <c>Themes/*.axaml</c> resource dictionaries for the actual color tokens each of Light/Dark/Signature defines.</summary>
public enum AppTheme
{
    Light,
    Dark,

    /// <summary>Follows the OS's own live light/dark preference - re-applied automatically if that preference changes while the app is running (see <see cref="ThemeService"/>'s <c>ColorValuesChanged</c> subscription).</summary>
    System,

    /// <summary>The original hand-tuned dark palette this whole GUI was designed against, kept as its own explicit choice - see Themes/Signature.axaml.</summary>
    Signature,
}

/// <summary>
/// Applies and persists the user's chosen <see cref="AppTheme"/>. Two things happen on
/// every change: the matching <c>Themes/*.axaml</c> color-token dictionary gets merged
/// into <see cref="Application.Resources"/> (swapping out whichever one was there before -
/// every control in MainWindow.axaml references these tokens via
/// <c>{DynamicResource Surface.Card}</c> etc., not hardcoded colors, so this alone
/// restyles the whole window), and <see cref="Application.RequestedThemeVariant"/> gets
/// set so FluentTheme's own built-in chrome (ComboBox popups, scrollbars, the default
/// window chrome) matches too - Signature resolves to the Dark variant there, since it's
/// a dark palette even though it's not literally called "Dark".
/// </summary>
public static class ThemeService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NdzForge", "settings.json");

    private static IResourceProvider? _currentThemeDictionary;
    private static bool _listeningForSystemChanges;

    public static AppTheme Current { get; private set; } = AppTheme.Signature;

    /// <summary>Loads the persisted choice (falling back to Signature - today's existing default look - if none was ever saved) and applies it. Call once at startup, before the main window is shown.</summary>
    public static void Initialize()
    {
        Current = LoadPersisted();
        Apply(Current);
    }

    /// <summary>Switches to <paramref name="theme"/>, applies it immediately, and persists the choice for next launch.</summary>
    public static void SetTheme(AppTheme theme)
    {
        Current = theme;
        Apply(theme);
        Save(theme);
    }

    private static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app)
            return;

        ThemeVariant variant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark or AppTheme.Signature => ThemeVariant.Dark,
            AppTheme.System => ResolveSystemVariant(app),
            _ => ThemeVariant.Dark,
        };
        app.RequestedThemeVariant = variant;

        string dictionaryName = theme switch
        {
            AppTheme.Light => "Light",
            AppTheme.Dark => "Dark",
            AppTheme.Signature => "Signature",
            AppTheme.System => variant == ThemeVariant.Light ? "Light" : "Dark",
            _ => "Dark",
        };
        ApplyColorDictionary(app, dictionaryName);

        if (theme == AppTheme.System)
            StartListeningForSystemChanges(app);
        else
            StopListeningForSystemChanges(app);
    }

    private static void ApplyColorDictionary(Application app, string name)
    {
        var uri = new Uri($"avares://Ndz.Gui/Themes/{name}.axaml");
        var loaded = (IResourceProvider)AvaloniaXamlLoader.Load(uri, null)!;

        if (_currentThemeDictionary is not null)
            app.Resources.MergedDictionaries.Remove(_currentThemeDictionary);
        app.Resources.MergedDictionaries.Add(loaded);
        _currentThemeDictionary = loaded;
    }

    private static ThemeVariant ResolveSystemVariant(Application app) =>
        app.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

    private static void StartListeningForSystemChanges(Application app)
    {
        if (_listeningForSystemChanges || app.PlatformSettings is null)
            return;
        app.PlatformSettings.ColorValuesChanged += OnSystemColorValuesChanged;
        _listeningForSystemChanges = true;
    }

    private static void StopListeningForSystemChanges(Application app)
    {
        if (!_listeningForSystemChanges || app.PlatformSettings is null)
            return;
        app.PlatformSettings.ColorValuesChanged -= OnSystemColorValuesChanged;
        _listeningForSystemChanges = false;
    }

    private static void OnSystemColorValuesChanged(object? sender, PlatformColorValues e)
    {
        // Only re-applies while System is still the active choice - a stale subscription
        // is always removed the moment the user picks something else (see Apply's own
        // Start/StopListening calls), but this guards the moment the event fires anyway.
        if (Current == AppTheme.System && Application.Current is { } app)
            Apply(AppTheme.System);
    }

    private static AppTheme LoadPersisted()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(SettingsPath));
                if (settings?.Theme is not null && Enum.TryParse<AppTheme>(settings.Theme, out var theme))
                    return theme;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable settings file - fall back to the default rather than
            // crash on startup over a cosmetic preference.
        }
        return AppTheme.Signature;
    }

    private static void Save(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string json = JsonSerializer.Serialize(new SettingsFile { Theme = theme.ToString() });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only - the theme still applies for this session even if it
            // can't be saved for next time.
        }
    }

    private sealed class SettingsFile
    {
        public string? Theme { get; set; }
    }
}
