using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Globalization;
using Windows.Win32.System.Com;

namespace Freihand.Windows.Recognition;

/// <summary>
/// Rechtschreibprüfung von Windows (P11 S4, „Wort lernen“): Ist ein gehörtes Wort ein gewöhnliches Wort, darf es nur
/// ausdrücklich ersetzt werden. Lokal, ohne Netz; Windows liefert die Wörterbücher der installierten Sprachen. Nur auf dem
/// Thread verwenden, auf dem sie erzeugt wurde.
/// </summary>
public sealed class WindowsSpelling : IDisposable
{
    private ISpellChecker? _checker;

    private WindowsSpelling(ISpellChecker checker, string language)
    {
        _checker = checker;
        Language = language;
    }

    /// <summary>Sprache des Wörterbuchs, z. B. „de-CH“.</summary>
    public string Language { get; }

    /// <summary>Die erste unterstützte Sprache aus <paramref name="languages"/>; null, wenn keine verfügbar ist.</summary>
    public static unsafe WindowsSpelling? TryCreate(params string[] languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            return null;
        }

        try
        {
            var clsid = typeof(SpellCheckerFactory).GUID;
            var iid = typeof(ISpellCheckerFactory).GUID;
            PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, out var instance).ThrowOnFailure();
            var factory = (ISpellCheckerFactory)instance;
            try
            {
                foreach (var language in languages)
                {
                    if (factory.IsSupported(language))
                    {
                        return new WindowsSpelling(factory.CreateSpellChecker(language), language);
                    }
                }

                return null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Kennt das Wörterbuch das Wort? null, wenn die Prüfung scheitert.</summary>
    public bool? IsKnown(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        ObjectDisposedException.ThrowIf(_checker is null, this);
        if (word.Trim().Length == 0 || !OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            return null;
        }

        IEnumSpellingError? errors = null;
        try
        {
            errors = _checker.Check(word);
            var result = errors.Next(out var error);
            if (error is not null)
            {
                Marshal.ReleaseComObject(error);
            }

            // S_OK: ein Fehler gefunden, das Wort ist unbekannt; S_FALSE: keiner, das Wort ist bekannt
            return result.Value == 0 ? false : result.Value == 1 ? true : null;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidCastException)
        {
            // Nicht prüfbar: der Vorschlag ist dann nicht vorgewählt
            return null;
        }
        finally
        {
            if (errors is not null)
            {
                Marshal.ReleaseComObject(errors);
            }
        }
    }

    public void Dispose()
    {
        if (_checker is not null)
        {
            Marshal.ReleaseComObject(_checker);
            _checker = null;
        }
    }
}
