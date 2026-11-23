using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>
/// R48: Der Erkenner verweist weder auf <c>System.Net.Http</c> noch auf <c>System.Net.Sockets</c> noch auf
/// <c>System.Net.WebSockets</c> (WE-07). Geprüft werden alle mitgelieferten Bibliotheken, die der Erkenner direkt oder über
/// andere erreicht (Gegencheck vom 10.11.2026), nicht nur seine eigene Datei. Die Metadaten werden gelesen; nichts wird
/// geladen oder ausgeführt. Dass zur Laufzeit keine Verbindung entsteht, belegt erst RM15.
/// </summary>
public sealed class R48NoNetworkReferencesTests
{
    private static readonly string[] Forbidden = ["System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets"];

    /// <summary>
    /// Bekannte Ausnahmen, beide Abhängigkeiten von Whisper.net: Whisper.net enthält einen Modell-Downloader
    /// (<c>WhisperGgmlDownloader</c>) mit <c>HttpClient</c>; Microsoft.Extensions.AI.Abstractions beschreibt Schnittstellen für
    /// KI-Dienste. Freihand ruft weder den Downloader auf noch benutzt es diese Schnittstellen (geprüft unten); die Modelle
    /// liegen lokal (WE-14).
    /// </summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["Whisper.net"] = ["System.Net.Http"],
        ["Microsoft.Extensions.AI.Abstractions"] = ["System.Net.Http"],
    };

    private static readonly string[] DownloaderTypes = ["WhisperGgmlDownloader", "HttpClient"];

    [Fact]
    public void R48_RecognizerAssemblyDoesNotReferenceNetworkLibraries()
    {
        var references = References(RecognizerPath());

        // Gegen eine leere Prüfung: Der Erkenner benutzt Freihand.Adapters (Protokoll, Dienst) und Whisper.net
        // (Kapitel 3.1); der Compiler legt einen Verweis nur an, wenn der Code die Bibliothek wirklich benutzt.
        Assert.Contains("Freihand.Adapters", references);
        Assert.Contains("Whisper.net", references);

        foreach (var forbidden in Forbidden)
        {
            Assert.DoesNotContain(forbidden, references);
        }
    }

    [Fact]
    public void R48_NoShippedLibraryReachedByTheRecognizerReferencesNetworkLibrariesExceptTheKnownOne()
    {
        var folder = Path.GetDirectoryName(RecognizerPath())!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(["Freihand.Recognizer"]);
        var findings = new List<string>();
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            var path = Path.Combine(folder, name + ".dll");

            // Bibliotheken der .NET-Laufzeit liegen nicht im Ausgabeordner; sie gehören zur Laufzeit selbst
            if (!seen.Add(name) || !File.Exists(path))
            {
                continue;
            }

            foreach (var reference in References(path))
            {
                if (Forbidden.Contains(reference) && !(Allowed.TryGetValue(name, out var allowed) && allowed.Contains(reference)))
                {
                    findings.Add($"{name} → {reference}");
                }

                queue.Enqueue(reference);
            }
        }

        // Gegen eine leere Prüfung: Whisper.net wurde erreicht und gelesen
        Assert.Contains("Whisper.net", seen);
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("Freihand.Recognizer")]
    [InlineData("Freihand.Adapters")]
    [InlineData("Freihand.Core")]
    public void R48_FreihandNeverUsesTheDownloaderOfWhisperNet(string assembly)
    {
        var path = Path.Combine(Path.GetDirectoryName(RecognizerPath())!, assembly + ".dll");
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var types = metadata.TypeReferences
            .Select(handle => metadata.GetString(metadata.GetTypeReference(handle).Name))
            .ToList();

        Assert.NotEmpty(types);
        foreach (var type in DownloaderTypes)
        {
            Assert.DoesNotContain(type, types);
        }

        // Die Schnittstellen für KI-Dienste benutzt Freihand nicht; ohne Verweis legt der Compiler keinen an
        var assemblies = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();
        Assert.DoesNotContain("Microsoft.Extensions.AI.Abstractions", assemblies);
    }

    private static string RecognizerPath() => typeof(Freihand.Recognizer.Program).Assembly.Location;

    private static List<string> References(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();
    }
}
