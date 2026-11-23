using System.Diagnostics;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Beschreibt einen gescheiterten Start fürs Tagesprotokoll: Typ der Ausnahme und der
/// inneren Ausnahmen sowie die obersten Stellen im Programm als „Typ.Methode“, nie die Meldung, weil sie Pfade oder Text
/// enthalten kann (SI-01). So lässt sich ein Startfehler ohne Zugriff aufs Gerät eingrenzen.
/// </summary>
public static class StartFailure
{
    /// <summary>Höchstens so viele Stellen je Ausnahme.</summary>
    public const int Frames = 8;

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var types = new List<string>();
        for (var current = exception; current is not null && types.Count < 4; current = current.InnerException)
        {
            types.Add(current.GetType().Name);
        }

        return $"typ={string.Join('/', types)} ort={Places(exception)}"
            + (exception.InnerException is { } inner ? $" innen={Places(inner)}" : string.Empty);
    }

    private static string Places(Exception exception)
    {
        var places = new StackTrace(exception, fNeedFileInfo: false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => $"{method!.DeclaringType?.Name ?? "-"}.{method.Name}")
            .Take(Frames)
            .ToList();
        return places.Count == 0 ? "-" : string.Join('<', places).Replace(' ', '_');
    }
}
