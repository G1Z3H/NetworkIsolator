using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using NetworkIsolator.Core;

namespace NetworkIsolator.Localization;

/// <summary>
/// Looks up translated texts. XAML binds to the indexer (so a language change updates the window live),
/// code calls <see cref="T"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private AppLanguage _current;

    private Loc() => _current = LoadSavedLanguage() ?? DetectSystemLanguage();

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage Current => _current;

    /// <summary>Text for a key, used by XAML bindings such as <c>[Nav_Apps]</c>.</summary>
    public string this[string key] => Lookup(key);

    /// <summary>Text for a key, formatted with the given arguments.</summary>
    public static string T(string key, params object[] args)
    {
        string text = Instance.Lookup(key);
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    public void SetLanguage(AppLanguage language)
    {
        if (language == _current) return;

        _current = language;
        SaveLanguage(language);

        // "Item[]" tells WPF that every indexer binding must be read again.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }

    private string Lookup(string key)
    {
        var table = _current == AppLanguage.French ? StringsFr.Table : StringsEn.Table;
        if (table.TryGetValue(key, out string? text)) return text;

        // A missing translation falls back to the other language, then to the key itself.
        var other = _current == AppLanguage.French ? StringsEn.Table : StringsFr.Table;
        return other.TryGetValue(key, out text) ? text : key;
    }

    private static AppLanguage DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? AppLanguage.French : AppLanguage.English;

    private static AppLanguage? LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(AppPaths.LanguageFile)) return null;
            return File.ReadAllText(AppPaths.LanguageFile).Trim().ToLowerInvariant() switch
            {
                "fr" => AppLanguage.French,
                "en" => AppLanguage.English,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLanguage(AppLanguage language)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.LanguageFile, language == AppLanguage.French ? "fr" : "en");
        }
        catch
        {
            // The choice just won't be remembered.
        }
    }
}
