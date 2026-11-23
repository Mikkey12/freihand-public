using System.Text;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Transform;
using Freihand.Core.Platform;
using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Gleichartige Stellen zu den Codex-Befunden an PR #55: Auch die Taste fürs Diktatfenster erfasst im Moment des Drucks;
/// das Erfassen wartet nie hinter einem Diktatstart; jeder Fehler beim Korrigieren wird zu einem Code; die Form des
/// Schlüssels; die Einstellungsdatei behält Kennung und Zeilenenden. Erfundene Texte.
/// </summary>
public sealed class Pr55ReviewFollowupTests
{
    [Fact]
    public async Task TheWindowKeyPressedInThePadStaysInThePadWhileAStartIsPending()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Pad = new FakePad(r.Log) { Focused = true };
            r.Factory.Gate = gate;
        });
        rig.Controller.RegisterHotkeys();
        var arrived = new TaskCompletionSource<DictationWindowRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Controller.DictationWindowRequested += (_, request) => arrived.TrySetResult(request);
        rig.Hotkeys.Press(HotkeyAction.Record);
        await rig.WaitUntil(() => rig.Factory.StartCount == 1, "Start blockiert die Tastenwarteschlange");

        rig.Hotkeys.Press(HotkeyAction.DictationWindow);
        rig.Pad!.Focused = false;
        gate.TrySetResult();
        var request = await arrived.Task.WaitAsync(ControllerRig.Timeout);

        Assert.Null(request.Mark.Mark);
        Assert.Equal(BindRefusal.OwnProcess, request.Mark.Refusal);
        Assert.False(rig.Log.Contains("Binding.MarkFocused:Standard"));
    }

    // Das Erfassen hat eine eigene Reihe: Es kommt an, obwohl der Diktatstart noch hängt
    [Fact]
    public async Task CapturingNeverWaitsBehindAPendingStart()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Factory.Gate = gate;
            r.Foreign.Select("Ein Satz.");
        });
        rig.Controller.RegisterHotkeys();
        var arrived = new TaskCompletionSource<CorrectionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Controller.CorrectionRequested += (_, request) => arrived.TrySetResult(request);
        rig.Hotkeys.Press(HotkeyAction.Record);
        await rig.WaitUntil(() => rig.Factory.StartCount == 1, "Start hängt");

        rig.Hotkeys.Press(HotkeyAction.Correct);
        var request = await arrived.Task.WaitAsync(ControllerRig.Timeout);

        Assert.False(gate.Task.IsCompleted, "Der Start hängt noch");
        Assert.Equal("Ein Satz.", request.Selection.Text!.Reveal());
        gate.TrySetResult();
    }

    [Fact]
    public async Task AnyFailureWhileCorrectingBecomesACodeWithoutInnerException()
    {
        var broken = new CorrectionService(() => throw new InvalidOperationException("GEHEIM-7"), text => text, TimeSpan.FromSeconds(5));
        var failing = new CorrectionService(() => new ThrowingTransformer(), text => text, TimeSpan.FromSeconds(5));

        var first = await Assert.ThrowsAsync<TransformFailedException>(() => broken.SuggestAsync("Ein Satz.", CorrectionKind.Correct));
        var second = await Assert.ThrowsAsync<TransformFailedException>(() => failing.SuggestAsync("Ein Satz.", CorrectionKind.Smooth));

        Assert.Equal(TransformFailure.Failed, first.Failure);
        Assert.Equal(TransformFailure.Failed, second.Failure);
        Assert.Null(first.InnerException);
        Assert.Null(second.InnerException);
        Assert.DoesNotContain("GEHEIM-7", first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("GEHEIM-7", second.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sk-ant-api03-ERFUNDEN_abc-123", true)]
    [InlineData("kurz", false)]
    [InlineData("sk-ant-api03-ERFUNDEN\nZEILE", false)]
    [InlineData("sk-ant-api03 ERFUNDEN", false)]
    [InlineData("sk-ant-api03-ERFUNDENä", false)]
    [InlineData(null, false)]
    public void OnlyTheShapeOfAKeyIsAccepted(string? key, bool expected) => Assert.Equal(expected, ApiKeyFormat.IsValid(key));

    [Fact]
    public void AnUtf8FileWithMarkAndCrLfKeepsBoth()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-pr55-utf8-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "einstellungen.txt");
            File.WriteAllText(path, "ton = ein\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder));
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);

            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            Assert.Equal(1, text.Split('﻿').Length - 1);
            Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("korrigieren = Strg+Alt+F\r\n", text, StringComparison.Ordinal);
            Assert.True(PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder)).Sound);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Feldidentität beim Druck: Die Bindung erkennt, ob genau das gemerkte Feld noch den Fokus hat
    [Fact]
    public void TheMarkedFieldIsFocusedOnlyWhileItHasTheFocus()
    {
        var original = FakeBinding.NewMark();
        var inspector = new Freihand.Core.Tests.Platform.Fakes.FakeTargetInspector();
        var facts = new TargetFacts(original.Rule.ProcessName, "Document", "TestEdit", "", false, false, true, true, false, false);
        inspector.Focus = (original.Identity, facts);
        inspector.Existing.Add(original.Identity);
        var binding = new TargetBinding(inspector, new TargetRuleTable([original.Rule]));
        var mark = binding.MarkFocused(Freihand.Core.Profiles.ProfileStrictness.Standard).Mark!;

        Assert.True(binding.IsFocused(mark));
        inspector.MoveFocus((original.Identity with { RuntimeId = "ANDERES-FELD" }, facts));
        Assert.False(binding.IsFocused(mark));
        Assert.Null(binding.Departures(Guid.NewGuid()));
    }

    private sealed class ThrowingTransformer : ITextTransformer
    {
        public TransformRoute Route => TransformRoute.PrivateProvider;

        public Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("GEHEIM-7");
    }
}
