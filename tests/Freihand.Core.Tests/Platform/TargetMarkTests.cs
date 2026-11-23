using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.Platform;

/// <summary>
/// D06, Teil Zielbindung: Merken bindet nicht und abonniert nichts;
/// Binden aus dem Gemerkten nur in genau dieses Feld, mit den Regeln von heute.
/// </summary>
public sealed class TargetMarkTests
{
    private static readonly TargetRule N = TargetTable.N;

    private static (FakeTargetInspector Inspector, TargetBinding Binding, ElementIdentity Identity) Focused()
    {
        var identity = TargetTable.IdentityFor(N);
        var inspector = new FakeTargetInspector { Focus = (identity, TargetTable.FactsFor(N)) };
        inspector.Existing.Add(identity);
        return (inspector, new TargetBinding(inspector, TargetTable.Table()), identity);
    }

    [Fact]
    public void D06_MarkingReadsLikeBindingButNeitherBindsNorSubscribes()
    {
        var (inspector, binding, identity) = Focused();

        var mark = binding.MarkFocused(ProfileStrictness.Standard);

        Assert.Null(mark.Refusal);
        Assert.Equal(N.Display, mark.Mark!.Display);
        Assert.Equal(identity, mark.Mark.Identity);
        Assert.Null(binding.Bound);
        Assert.Equal(0, inspector.FocusSubscribers);
    }

    [Fact]
    public void D06_MarkingFollowsTheBindingRules()
    {
        var (inspector, binding, identity) = Focused();
        inspector.Focus = (identity, TargetTable.FactsFor(N) with { IsOwnProcess = true });
        Assert.Equal(BindRefusal.OwnProcess, binding.MarkFocused(ProfileStrictness.Standard).Refusal);

        inspector.Focus = null;
        Assert.Equal(BindRefusal.NoFocus, binding.MarkFocused(ProfileStrictness.Standard).Refusal);

        inspector.Focus = (identity, TargetTable.FactsFor(N));
        inspector.SecureDesktop = true;
        Assert.Equal(BindRefusal.SecureDesktop, binding.MarkFocused(ProfileStrictness.Standard).Refusal);
    }

    [Fact]
    public void D06_BindMarkedBindsTheSameFieldWithANewBinding()
    {
        var (inspector, binding, _) = Focused();
        var mark = binding.MarkFocused(ProfileStrictness.Standard).Mark!;

        var bound = binding.BindMarked(mark, ProfileStrictness.Standard);

        Assert.Null(bound.Refusal);
        Assert.Equal(mark.Display, bound.Target!.Display);
        Assert.Equal(TargetCheck.Focused, binding.CheckBound(bound.Target.BindingId));
        Assert.Equal(1, inspector.FocusSubscribers);
        binding.Release();
        Assert.Equal(0, inspector.FocusSubscribers);
    }

    [Fact]
    public void D06_AnotherFieldWithTheFocusIsNeverBound()
    {
        var (inspector, binding, identity) = Focused();
        var mark = binding.MarkFocused(ProfileStrictness.Standard).Mark!;

        // Gleiches Programm, gleiche Zeile, anderes Element
        var other = identity with { RuntimeId = "42.9" };
        inspector.Focus = (other, TargetTable.FactsFor(N));
        inspector.Existing.Add(other);
        var bound = binding.BindMarked(mark, ProfileStrictness.Standard);

        Assert.Equal(BindRefusal.NotMarkedField, bound.Refusal);
        Assert.Null(bound.Target);
        Assert.Null(binding.Bound);
        Assert.Equal(0, inspector.FocusSubscribers);
    }

    [Fact]
    public void D06_TheRulesOfNowApplyNotThoseOfTheMark()
    {
        var (inspector, binding, identity) = Focused();
        var mark = binding.MarkFocused(ProfileStrictness.Standard).Mark!;

        // Dasselbe Feld meldet inzwischen ein Passwortfeld: gesperrt wie beim Binden (SI-04)
        inspector.Focus = (identity, TargetTable.FactsFor(N) with { IsPassword = true });
        var bound = binding.BindMarked(mark, ProfileStrictness.Standard);

        Assert.Equal(BindRefusal.PasswordField, bound.Refusal);
        Assert.Null(binding.Bound);
    }
}
