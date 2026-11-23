using System.Reflection;
using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Tests.Recovery;
using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

// Eigenständige Gegenproben zur Geschäftsausgabe. Nur Testattrappen und künstliche Dateien.
// Nicht übernommen: BusinessBinaryHasNoUnrestrictedDiagnosticFileSink braucht einen veröffentlichten Geschäftsbau; dieselbe
// Prüfung (kein MeasurementLog in Freihand.dll) machen die CI und tools/Geschaeft/Freihand-Geschaeftspaket.ps1 am Bau.
public sealed class BusinessAuditCounterTests : Pr70InterleavingTests
{
    [Fact]
    public async Task MeasurementPreservesAnOrdinaryReloadedDraft()
    {
        const string draft = "offen";
        var field = new ProbeField("41.1", "", changeOnRead: draft, changeAtRead: 2);
        ProbeField.Current.Value = field;
        try
        {
            await Messen(field.RuntimeId, null);
            Assert.True(field.DraftLoaded);
            Assert.False(field.ReloadDuringFinalCheck);
            Assert.Equal(draft, field.Raw);
            Assert.Equal(0, field.ClearCalls);
        }
        finally { ProbeField.Current.Value = null; }
    }

    [Fact]
    public void IdentifyFieldRequiresNoContentRead()
    {
        var reads = 0;
        var field = new ProbeField("41.1", "") { ReadOverride = _ => { reads++; return "synthetisch"; } };
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.json");
        ProbeField.Current.Value = field;
        try
        {
            var options = Optionen.Lesen(["fakten", "--modus", "erkennen", "--wartezeit", "0",
                "--name", "test", "--out", path]);
            Assert.Equal(0, Fakten.Ausfuehren(options));
            Assert.Equal(0, reads);
        }
        finally { ProbeField.Current.Value = null; File.Delete(path); }
    }

    [Fact]
    public async Task AQueuedRecordKeyCannotBindTheLaterField()
    {
        await using var rig = new ControllerRig();
        rig.Controller.RegisterHotkeys();
        var queueGate = typeof(DictationController).GetField("_gate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Controller)!;
        lock (queueGate)
        {
            rig.Binding.NextMark = new(FieldMark("OUTLOOK", 42, "41.1"), null);
            rig.Binding.Next = new(FakeBinding.NewTarget("Dokument A") with { ProcessName = "OUTLOOK" }, null);
            rig.Hotkeys.Press(HotkeyAction.Record);
            rig.Binding.NextMark = new(FieldMark("ms-teams", 84, "41.2"), null);
            rig.Binding.Next = new(FakeBinding.NewTarget("Chat B") with { ProcessName = "ms-teams" }, null);
            rig.Binding.MarkedFocus.Enqueue(BindRefusal.NotMarkedField);
        }

        await rig.WaitUntil(() => rig.Binding.Bound is not null ||
            rig.Controller.Current.Message == ControllerMessage.BindRefused, "Zielentscheid");
        Assert.NotEqual("ms-teams", rig.Binding.Bound?.ProcessName);

        static TargetMark FieldMark(string process, int pid, string id)
        {
            var mark = FakeBinding.NewMark(process);
            return mark with { ProcessName = process, Identity = mark.Identity with { ProcessId = pid, RuntimeId = id },
                Rule = mark.Rule with { ProcessName = process } };
        }
    }

    [Fact]
    public void ContinuedUseEnforcesThePromisedLogRetention()
    {
        var dir = Directory.CreateTempSubdirectory("freihand-audit-");
        try
        {
            var clock = new ManualTimeProvider();
            var log = new DailyLog(dir.FullName, clock);
            log.DeleteOld();
            log.Write("test");
            var old = Assert.Single(dir.GetFiles()).FullName;
            clock.Advance(TimeSpan.FromDays(16));
            log.Write("test");
            Assert.False(File.Exists(old));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void UnknownPasswordStateMatchesTheITPromise()
    {
        var facts = new TargetFacts("test", "Edit", "", "Chrome", null, false, true, false, false, false);
        var decision = new TargetRuleTable([]).Decide(facts, ProfileStrictness.Standard);
        string leaflet = File.ReadAllText(Path.Combine(Root(), "docs", "pakete", "it-merkblatt.md"));
        // Beide zulässigen Korrekturen: technisch sperren oder der IT die Reichweite der Regel offenlegen.
        if (decision is Bindable)
            Assert.DoesNotContain("Nie in Passwortfelder", leaflet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovalOffersAProgramOnlyOption()
    {
        var manual = File.ReadAllText(Path.Combine(Root(), "tools", "Geschaeft", "LIESMICH.txt"));
        var removal = manual.Split("\nEntfernen", StringSplitOptions.None)[1];
        Assert.Contains("Daten behalten", removal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATransientEmptyReadDoesNotConfirmCleanup()
    {
        int readsAfterClear = 0;
        ProbeField? field = null;
        field = new ProbeField("41.1", "", clearResult: "Rest")
        {
            ReadOverride = _ => field!.ClearCalls > 0 && ++readsAfterClear == 1 ? "" : field!.Raw,
        };
        ProbeField.Current.Value = field;
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
            var result = await Schreiben.AusfuehrenAsync(options);
            Assert.Equal(1, field.ClearCalls);
            Assert.NotEqual(0, result);
        }
        finally { ProbeField.Current.Value = null; File.Delete(path); }
    }

    [Fact]
    public async Task ACellInReplaceModeKeepsItsValueWhenCheckingForAHint()
    {
        var field = new ProbeField("41.1", "42") { ContentAfterInsert = Schreiben.Pruefzeichen.ToString() };
        ProbeField.Current.Value = field;
        try
        {
            await Schreiben.HinweisAsync(field.RuntimeId);
            Assert.Contains("42", field.Raw, StringComparison.Ordinal);
        }
        finally { ProbeField.Current.Value = null; }
    }

    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Freihand.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
}

