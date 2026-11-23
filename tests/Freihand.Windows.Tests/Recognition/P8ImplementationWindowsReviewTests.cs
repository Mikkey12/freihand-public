using System.Reflection;
using Freihand.Recognizer;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>PR #27: Quelltextgestützte Gegenprobe ohne Modelle und ohne Mikrofon.</summary>
public sealed class P8ImplementationWindowsReviewTests
{
    /// <summary>
    /// P8 5.5 W1/10.5: Alle nativen Sondertokens entfallen, auch Zeitstempel und Sprachen.
    /// Quelle: whisper.net 1.9.1 referenziert whisper.cpp f24588a272ae8e23280d9c220536437164e6ed28,
    /// src/whisper.cpp:1645–1667. Diese Formen enden teilweise auf ], nicht auf _].
    /// https://github.com/ggml-org/whisper.cpp/blob/f24588a272ae8e23280d9c220536437164e6ed28/src/whisper.cpp#L1645
    /// </summary>
    [Theory]
    [InlineData("[_TT_0]", true)]
    [InlineData("[_TT_1500]", true)]
    [InlineData("[_LANG_de]", true)]
    [InlineData("[_extra_token_50300]", true)]
    [InlineData("[_EOT_]", true)]
    [InlineData("<|0.00|>", true)]
    [InlineData("Beispiel", false)]
    [InlineData("[Klammer]", false)]
    public void D07_NativeSpecialTokenFormsAreRecognized(string text, bool expected)
    {
        var backend = typeof(WhisperNetLoader).GetNestedType("WhisperNetBackend", BindingFlags.NonPublic);
        Assert.NotNull(backend);
        var classify = backend.GetMethod("IsSpecial", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(classify);
        Assert.Equal(expected, Assert.IsType<bool>(classify.Invoke(null, [text])));
    }
}
