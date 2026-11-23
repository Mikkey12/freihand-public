using System.Text;
using Freihand.Core.Live;
using Freihand.Core.Platform;

namespace Freihand.Adapters.Dictation;

/// <summary>Ergebnis des Lernens.</summary>
public enum LearnOutcome
{
    /// <summary>In <c>woerter-gelernt.txt</c> gespeichert; gilt ab dem nächsten Diktat.</summary>
    Saved,

    /// <summary>
    /// Die Schreibweise steht in <c>woerter.txt</c>, die Vorrang hat; nichts gespeichert, weil es nicht wirken würde.
    /// </summary>
    InOwnList,

    /// <summary>
    /// Eine gehörte Form wird schon durch ein anderes Wort ersetzt (z. B. nach einem Tippfehler beim ersten Lernen); nichts
    /// gespeichert, weil es nicht wirken würde. Welches Wort, sagt <see cref="WordStore.Owner"/>.
    /// </summary>
    TakenByOther,
}

/// <summary>
/// Die Wortlisten (P11 S4). Regel (Codex zu PR #66): Jede Datei hat genau einen Schreiber. <c>woerter.txt</c> gehört dem Benutzer;
/// Freihand legt sie höchstens an, wenn es sie nicht gibt, und schreibt sie sonst nie. Was Freihand lernt,
/// steht in <c>woerter-gelernt.txt</c>, die nur Freihand schreibt, über eine temporäre Datei und Umbenennen. Beim
/// Diktatbeginn werden beide neu gelesen und zusammengeführt; eine Zeile in <c>woerter.txt</c> hat Vorrang vor einer
/// gelernten mit derselben Schreibweise. Ins Protokoll gehen nur Anzahlen, nie Wörter.
/// </summary>
public sealed class WordStore(string path, Action<string>? log = null)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Lock _gate = new();
    private (int Words, int Replacements, int Problems)? _logged;

    /// <summary><c>woerter.txt</c>, die Liste der Nutzerin oder des Nutzers.</summary>
    public string Path { get; } = path;

    /// <summary><c>woerter-gelernt.txt</c> daneben; schreibt nur Freihand.</summary>
    public string LearnedPath { get; } = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? ".", "woerter-gelernt.txt");

    /// <summary>
    /// Ersetzungen aus beiden Dateien, bei jedem Aufruf neu gelesen (sie sind klein). Fehlt eine, zählt sie als leer; ist eine
    /// nicht lesbar (z. B. ein Editor speichert gerade), gelten für dieses Diktat keine Ersetzungen, damit ein Abschalten in
    /// <c>woerter.txt</c> nie übergangen wird. Wirft nie. Protokolliert wird nur, wenn sich die Anzahlen ändern.
    /// </summary>
    public WordReplacements Load()
    {
        try
        {
            var (own, learned) = Documents();
            var words = Merge(own, learned);
            var replacements = WordReplacements.From(words);
            var counts = (words.Count, replacements.Count, own.Problems.Count + learned.Problems.Count);
            lock (_gate)
            {
                if (_logged != counts)
                {
                    _logged = counts;
                    log?.Invoke($"woerter geladen woerter={counts.Item1} ersetzungen={counts.Item2} problemzeilen={counts.Item3}");
                }
            }

            return replacements;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log?.Invoke("woerter nicht lesbar fehler=" + exception.GetType().Name);
            return WordReplacements.Empty;
        }
    }

    /// <summary>Fehlerhafte Zeilen beider Dateien, ohne ihren Text (für die Anzeige im Lernfenster).</summary>
    public IReadOnlyList<string> Problems()
    {
        try
        {
            var (own, learned) = Documents();
            return [.. own.Problems.Select(problem => "woerter.txt, " + problem),
                .. learned.Problems.Select(problem => "woerter-gelernt.txt, " + problem)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ["Die Wörterliste ist nicht lesbar (" + exception.GetType().Name + ")."];
        }
    }

    /// <summary>
    /// Gelernte Wörter mit den gehörten Schreibweisen, die beim Diktat wirklich ersetzt werden (gleiche Vorrangregeln wie
    /// <see cref="Load"/>); leer, wenn nicht lesbar.
    /// </summary>
    public IReadOnlyList<LearnedWord> Learned()
    {
        try
        {
            var (own, learned) = Documents();
            var replacements = WordReplacements.From(Merge(own, learned));
            var spellings = own.Words.Select(word => word.Spelling).ToHashSet(StringComparer.Ordinal);
            return [.. learned.Words
                .Where(word => !spellings.Contains(word.Spelling))
                .Select(word => word with { Heard = [.. word.Heard.Where(heard => replacements.Applies(word.Spelling, heard))] })
                .Where(word => word.Heard.Count > 0)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// Speichert das gelernte Wort in <c>woerter-gelernt.txt</c>. Steht die Schreibweise in <c>woerter.txt</c>, wird nichts
    /// gespeichert (es würde nicht wirken).
    /// </summary>
    /// <exception cref="IOException">Datei gesperrt oder nicht schreibbar.</exception>
    /// <exception cref="UnauthorizedAccessException">Kein Zugriff.</exception>
    /// <exception cref="InvalidOperationException">Die gelernte Liste ist voll; nichts geschrieben.</exception>
    public LearnOutcome Add(LearnedWord word)
    {
        ArgumentNullException.ThrowIfNull(word);
        lock (_gate)
        {
            if (WordListFile.Parse(Read(Path)).Words.Any(item => item.Spelling == word.Spelling))
            {
                return LearnOutcome.InOwnList;
            }

            var own = WordListFile.Parse(Read(Path));
            var existing = Read(LearnedPath);
            var text = WordListFile.Add(existing, word);

            // Gespeichert heisst wirksam, und Lernen ändert nie eine bestehende Zuordnung (eigene Vorprüfung und Codex zu
            // PR #66): Jede gehörte Form wird danach durch das gelernte Wort ersetzt, und jede bisher wirksame Zuordnung
            // gilt danach unverändert weiter
            var beforeWords = Merge(own, WordListFile.Parse(existing));
            var before = WordReplacements.From(beforeWords);
            var after = WordReplacements.From(Merge(own, WordListFile.Parse(text)));
            var kept = beforeWords.All(item => item.Heard.All(heard =>
                !before.Applies(item.Spelling, heard) || after.Applies(item.Spelling, heard)));
            if (!kept || word.Heard.Any(heard => !after.Applies(word.Spelling, heard)))
            {
                return LearnOutcome.TakenByOther;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LearnedPath)!);
            var temporary = LearnedPath + ".neu";
            try
            {
                File.WriteAllText(temporary, text, Utf8);
                File.Move(temporary, LearnedPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        log?.Invoke($"woerter gelernt ersetzungen={word.Heard.Count}");
        return LearnOutcome.Saved;
    }

    /// <summary>Welches Wort die gehörte Form heute ersetzt (beide Listen, Vorrang wie beim Diktat), sonst null.</summary>
    public string? Owner(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        try
        {
            var (own, learned) = Documents();
            return WordReplacements.From(Merge(own, learned)).SpellingFor(heard);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Legt <c>woerter.txt</c> mit Kopf an, falls es sie nicht gibt (zum Öffnen im Editor); eine vorhandene Datei bleibt
    /// unberührt, auch wenn sie zwischen Prüfen und Anlegen entsteht.
    /// </summary>
    public void EnsureExists()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try
        {
            using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Utf8.GetBytes(WordListFile.Header));
        }
        catch (IOException) when (File.Exists(Path))
        {
            // Gibt es schon: nie überschreiben
        }
    }

    private (WordListDocument Own, WordListDocument Learned) Documents()
    {
        lock (_gate)
        {
            return (WordListFile.Parse(Read(Path)), WordListFile.Parse(Read(LearnedPath)));
        }
    }

    /// <summary>Zuerst die eigene Liste, dann gelernte Wörter, deren Schreibweise dort nicht steht.</summary>
    private static List<LearnedWord> Merge(WordListDocument own, WordListDocument learned)
    {
        var spellings = own.Words.Select(word => word.Spelling).ToHashSet(StringComparer.Ordinal);
        return [.. own.Words, .. learned.Words.Where(word => !spellings.Contains(word.Spelling))];
    }

    /// <summary>
    /// Inhalt der Datei, leer, wenn es sie nicht gibt. Hat ein Editor die Datei zum Schreiben offen (manche leeren sie zuerst
    /// und schreiben dann auf demselben Handle), scheitert das Lesen, und es gilt der Rückfall „keine Ersetzungen“; sonst
    /// könnte ein abgeschaltetes Wort für ein Diktat wieder wirken (Codex zu PR #66).
    /// </summary>
    private static string Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Zuhören beim Lernen (P11 S4): dieselbe Erkennung wie beim Diktat, aber ohne Ziel und ohne Ersetzungen; jede
/// abgeschlossene Äusserung wird mit ihren Wörtern gemeldet, so wie die Erkennung sie geliefert hat (Codex zu PR #66: aus
/// fertigem Text lassen sich die Wortgrenzen nicht sicher zurückgewinnen). Die Fabrik muss <see cref="HeardOutput"/>
/// melden (<see cref="RecognizerDictationFactory.ForLearning"/>). Das Mikrofon ist mit dem Diktat geteilt: Läuft ein
/// Diktat, scheitert das Öffnen.
/// </summary>
public sealed class WordListener(IDictationFactory factory, IAudioCapture audio)
{
    /// <summary>Meldung, wenn das gewählte oder das Standardmikrofon fehlt.</summary>
    public const string NoMicrophone = "Kein Mikrofon gefunden.";

    /// <summary>Hört zu, bis <paramref name="stop"/> ausgelöst wird, und liefert danach alle gehörten Äusserungen.</summary>
    /// <param name="heard">Zur Anzeige: der Text jeder Äusserung, Wörter mit Leerzeichen getrennt.</param>
    /// <exception cref="InvalidOperationException">Kein Mikrofon oder Mikrofon belegt.</exception>
    public async Task<IReadOnlyList<Utterance>> ListenAsync(
        string? deviceId, Action<string>? heard, CancellationToken stop, CancellationToken cancellationToken)
    {
        var device = (deviceId is { } id ? audio.Devices.FirstOrDefault(item => item.Id == id) : audio.DefaultDevice)
            ?? throw new InvalidOperationException(NoMicrophone);

        // „Fertig“ und die Frist gelten schon beim Start der Erkennung; dann öffnet sich das Mikrofon nicht mehr (Codex zu PR #66)
        IDictationRun run;
        using (var starting = CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken))
        {
            try
            {
                run = await factory.StartAsync(deviceId, starting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return [];
            }
        }

        await using (run.ConfigureAwait(false))
        {
            // Erst hier, damit der gelieferte Lauf in jedem Fall freigegeben wird (Codex zu PR #66): Schliessen bricht ab,
            // „Fertig“ und Frist liefern nichts
            cancellationToken.ThrowIfCancellationRequested();
            if (stop.IsCancellationRequested)
            {
                return [];
            }

            var capture = audio.Open(device);
            await using (capture.ConfigureAwait(false))
            {
                using var stopping = stop.Register(capture.Stop);
                var utterances = new List<Utterance>();
                var seen = new HashSet<Guid>();
                await foreach (var output in run.RunAsync(capture.ReadAsync(cancellationToken), cancellationToken).ConfigureAwait(false))
                {
                    if (output is HeardOutput { Utterance: var utterance } && utterance.Words.Count > 0 && seen.Add(utterance.Id))
                    {
                        utterances.Add(utterance);
                        heard?.Invoke(string.Join(' ', utterance.Words.Select(word => word.Text)));
                    }
                }

                return utterances;
            }
        }
    }
}
