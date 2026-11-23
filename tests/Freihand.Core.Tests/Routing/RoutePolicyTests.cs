using Freihand.Core.Routing;
using static Freihand.Core.Tests.Routing.RouteTestData;

namespace Freihand.Core.Tests.Routing;

/// <summary>Einzelne Regeln der Richtlinie mit ihrer Begründung (WE-03, GE-04, SI-07).</summary>
public class RoutePolicyTests
{
    private static readonly RoutePolicy Private = new(new RoutePolicyOptions(Edition.Private));

    [Fact]
    public void LocalRecognitionDoesNotPermitOnlineTransformForConfidentialData()
    {
        // Kapitel 5: Die Datenklasse gilt für beide Schritte
        var decision = Private.Evaluate(Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.PrivateProvider));

        Assert.False(decision.Allowed);
        Assert.Equal(DenialReason.DataClass, decision.Denial);
    }

    [Fact]
    public void HomeIsNotLocalForConfidentialData()
    {
        var decision = Private.Evaluate(Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.Home));

        Assert.Equal(DenialReason.DataClass, decision.Denial);
    }

    [Fact]
    public void EndpointThatDoesNotMatchTheRouteIsDenied()
    {
        // WE-03: geprüft wird der tatsächliche Endpunkt, nicht nur der angegebene Weg
        var request = Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Local)
            with { TransformEndpoint = Provider };

        var decision = Private.Evaluate(request);

        Assert.Equal(DenialReason.EndpointMismatch, decision.Denial);
    }

    [Fact]
    public void ItCannotEnableRoutesTheBusinessEditionDoesNotHave()
    {
        // GE-02: Online und Heim fehlen im Geschäft ganz, auch mit Freigabe und Endpunkt der IT
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Business) { It = ApproveEverything() });

        Assert.Equal(DenialReason.NotInEdition,
            policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Online, TransformRoute.None)).Denial);
        Assert.Equal(DenialReason.NotInEdition,
            policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Home)).Denial);
    }

    [Fact]
    public void CompanyServiceNeedsApprovalAndApprovedEndpoint()
    {
        var request = Request(DataClass.Internal, RecognitionRoute.Device, TransformRoute.CompanyService);

        var withoutApproval = new RoutePolicy(new RoutePolicyOptions(Edition.Business));
        var withoutEndpoint = new RoutePolicy(new RoutePolicyOptions(Edition.Business)
        {
            It = new ItDirectives { ApprovedTransform = new HashSet<TransformRoute> { TransformRoute.CompanyService } },
        });
        var approved = new RoutePolicy(new RoutePolicyOptions(Edition.Business)
        {
            It = new ItDirectives
            {
                ApprovedTransform = new HashSet<TransformRoute> { TransformRoute.CompanyService },
                ApprovedEndpoints = new HashSet<string> { CompanyTenant.Address },
            },
        });

        Assert.Equal(DenialReason.ApprovalMissing, withoutApproval.Evaluate(request).Denial);
        Assert.Equal(DenialReason.EndpointNotApproved, withoutEndpoint.Evaluate(request).Denial);
        Assert.True(approved.Evaluate(request).Allowed);
    }

    [Fact]
    public void LocalTransformInBusinessNeedsApprovalAfterMeasurement()
    {
        var request = Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.Local);
        var measured = new RoutePolicy(new RoutePolicyOptions(Edition.Business)
        {
            It = new ItDirectives { ApprovedTransform = new HashSet<TransformRoute> { TransformRoute.Local } },
        });

        Assert.Equal(DenialReason.ApprovalMissing, new RoutePolicy(new RoutePolicyOptions(Edition.Business)).Evaluate(request).Denial);
        Assert.True(measured.Evaluate(request).Allowed);
    }

    [Fact]
    public void ItBlockOverridesApproval()
    {
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Business)
        {
            It = ApproveEverything() with
            {
                BlockedTransform = new HashSet<TransformRoute> { TransformRoute.CompanyService },
            },
        });

        var decision = policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.CompanyService));

        Assert.Equal(DenialReason.BlockedByIt, decision.Denial);
    }

    [Fact]
    public void ItMinimumDataClassOverridesTheProfile()
    {
        // GE-04: Das Profil sagt „öffentlich“, die IT verlangt mindestens „vertraulich“
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Private)
        {
            It = new ItDirectives { MinimumDataClass = DataClass.Confidential },
        });

        var online = policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Online, TransformRoute.None));
        var device = policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Local));

        Assert.Equal(DenialReason.DataClass, online.Denial);
        Assert.Contains("vertraulich", online.Reason, StringComparison.Ordinal);
        Assert.True(device.Allowed);
        Assert.Equal(DataClass.Confidential, policy.EffectiveDataClass(DataClass.Public));
    }

    [Fact]
    public void ForgedRankCannotBypassTheItMinimumDataClass()
    {
        // Codex-Review R4: der Rang kommt aus der Definition der Klasse, nicht vom Aufrufer (WE-03, GE-04)
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Private)
        {
            It = new ItDirectives { MinimumDataClass = DataClass.Confidential },
        });
        var forged = new DataClass(DataClass.Public.Id, 99);

        var decision = policy.Evaluate(Request(forged, RecognitionRoute.Online, TransformRoute.None));

        Assert.False(decision.Allowed);
        Assert.Equal(DenialReason.DataClass, decision.Denial);
        Assert.Equal(DataClass.Confidential, policy.EffectiveDataClass(forged));
    }

    [Theory]
    [InlineData("oeffentlich", 2)]
    [InlineData("vertraulich", 0)]
    [InlineData("intern", 1000)]
    public void SuppliedRankIsReplacedByTheDefinedOne(string id, int rank)
    {
        // Codex-Nachprüfung N4: Rang immer aus der Definition nach Kennung, mitgelieferter Rang wird ignoriert
        var supplied = new DataClass(id, rank);
        var defined = Private.EffectiveDataClass(supplied);

        Assert.NotNull(defined);
        Assert.Equal(id, defined.Id);
        Assert.NotEqual(rank, defined.Rank);
        foreach (var recognition in Enum.GetValues<RecognitionRoute>())
        {
            foreach (var transform in Enum.GetValues<TransformRoute>())
            {
                Assert.Equal(
                    Private.Evaluate(Request(defined, recognition, transform)),
                    Private.Evaluate(Request(supplied, recognition, transform)));
            }
        }
    }

    [Fact]
    public void StandardClassWithRankRedefinedByItStillAppliesItsRules()
    {
        // Codex-Nachprüfung N4: P2 importiert „intern“ mit Standardrang 1, die IT legt Rang 10 fest
        var internalByIt = new DataClass(DataClass.Internal.Id, 10);
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Private)
        {
            It = new ItDirectives { DataClasses = [internalByIt] },
        });

        Assert.Equal(internalByIt, policy.EffectiveDataClass(DataClass.Internal));
        Assert.True(policy.Evaluate(Request(DataClass.Internal, RecognitionRoute.Device, TransformRoute.None)).Allowed);
        Assert.True(policy.Evaluate(Request(DataClass.Internal, RecognitionRoute.Home, TransformRoute.Local)).Allowed);
        Assert.Equal(DenialReason.DataClass,
            policy.Evaluate(Request(DataClass.Internal, RecognitionRoute.Online, TransformRoute.None)).Denial);
        Assert.Equal(DenialReason.DataClass,
            policy.Evaluate(Request(DataClass.Internal, RecognitionRoute.Device, TransformRoute.PrivateProvider)).Denial);
    }

    [Fact]
    public void ItDefinedDataClassesAreResolvedById()
    {
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Business)
        {
            It = new ItDirectives
            {
                DataClasses = [StrictlyConfidential],
                MinimumDataClass = StrictlyConfidential,
                DataClassRules = new Dictionary<string, DataClassRule>
                {
                    [StrictlyConfidential.Id] = new([RecognitionRoute.Device], [TransformRoute.None], [EndpointKind.Device]),
                },
            },
        });

        Assert.Equal(StrictlyConfidential, policy.EffectiveDataClass(DataClass.Public));
        Assert.Equal(StrictlyConfidential, policy.EffectiveDataClass(new DataClass(StrictlyConfidential.Id, 1)));
        Assert.True(policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.None)).Allowed);
        Assert.Equal(DenialReason.UnknownDataClass,
            policy.Evaluate(Request(new DataClass("geheim", 5), RecognitionRoute.Device, TransformRoute.None)).Denial);
    }

    [Fact]
    public void ItMinimumDataClassMustBeDefined()
    {
        var options = new RoutePolicyOptions(Edition.Business)
        {
            It = new ItDirectives { MinimumDataClass = new DataClass("geheim", 5) },
        };

        Assert.Throws<ArgumentException>(() => new RoutePolicy(options));
    }

    [Fact]
    public void ItDataClassRulesReplaceTheDefaults()
    {
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Business)
        {
            It = ApproveEverything() with
            {
                DataClassRules = new Dictionary<string, DataClassRule>
                {
                    // die IT lässt „vertraulich“ an den Firmendienst und hält „öffentlich“ auf dem Gerät
                    [DataClass.Confidential.Id] = new(
                        [RecognitionRoute.Device],
                        [TransformRoute.None, TransformRoute.CompanyService],
                        [EndpointKind.Device, EndpointKind.CompanyTenant]),
                    [DataClass.Public.Id] = new([RecognitionRoute.Device], [TransformRoute.None], [EndpointKind.Device]),
                },
            },
        });

        Assert.True(policy.Evaluate(Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.CompanyService)).Allowed);
        Assert.Equal(DenialReason.DataClass,
            policy.Evaluate(Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.Local)).Denial);
        Assert.Equal(DenialReason.DataClass,
            policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.CompanyService)).Denial);
    }

    [Fact]
    public void DataClassRuleChecksRoutesAndEndpointKindsSeparately()
    {
        // WE-03: Weg und Endpunkt sind je eigene Prüfungen; jede allein genügt zum Verweigern
        DataClassRule routeWithoutEndpoint = new([RecognitionRoute.Device], [TransformRoute.Home], [EndpointKind.Device]);
        DataClassRule endpointWithoutRoute = new([RecognitionRoute.Device], [TransformRoute.None], [EndpointKind.Device, EndpointKind.Home]);
        var request = Request(DataClass.Internal, RecognitionRoute.Device, TransformRoute.Home);

        foreach (var rule in new[] { routeWithoutEndpoint, endpointWithoutRoute })
        {
            var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Private)
            {
                DataClassRules = new Dictionary<string, DataClassRule> { [DataClass.Internal.Id] = rule },
            });

            Assert.Equal(DenialReason.DataClass, policy.Evaluate(request).Denial);
        }
    }

    [Fact]
    public void CompanyDefinedDataClassNeedsAnItRule()
    {
        var request = Request(StrictlyConfidential, RecognitionRoute.Device, TransformRoute.None);
        var withRule = new RoutePolicy(new RoutePolicyOptions(Edition.Business)
        {
            It = new ItDirectives
            {
                DataClasses = [StrictlyConfidential],
                DataClassRules = new Dictionary<string, DataClassRule>
                {
                    [StrictlyConfidential.Id] = new([RecognitionRoute.Device], [TransformRoute.None], [EndpointKind.Device]),
                },
            },
        });

        Assert.Equal(DenialReason.UnknownDataClass, new RoutePolicy(new RoutePolicyOptions(Edition.Business)).Evaluate(request).Denial);
        Assert.True(withRule.Evaluate(request).Allowed);
    }

    [Fact]
    public void ApprovedEndpointsOfUserAndItMustBothContainTheAddress()
    {
        var request = Request(DataClass.Public, RecognitionRoute.Online, TransformRoute.None);
        var other = request with { RecognitionEndpoint = new RouteEndpoint(EndpointKind.Provider, "anderer.example") };

        var userOnly = new RoutePolicy(new RoutePolicyOptions(Edition.Private)
        {
            ApprovedEndpoints = new HashSet<string> { Provider.Address },
        });
        var userAndIt = new RoutePolicy(new RoutePolicyOptions(Edition.Private)
        {
            ApprovedEndpoints = new HashSet<string> { Provider.Address },
            It = new ItDirectives { ApprovedEndpoints = new HashSet<string> { "anderer.example" } },
        });

        Assert.True(userOnly.Evaluate(request).Allowed);
        Assert.Equal(DenialReason.EndpointNotApproved, userOnly.Evaluate(other).Denial);
        Assert.Equal(DenialReason.EndpointNotApproved, userAndIt.Evaluate(request).Denial);
        Assert.Equal(DenialReason.EndpointNotApproved, userAndIt.Evaluate(other).Denial);
    }

    [Fact]
    public void DeniedRouteProducesNoRequest()
    {
        // Abnahme WE-03: Eine verbotene Kombination erzeugt keine Anfrage
        var sent = new List<RouteRequest>();
        void Send(RouteRequest route)
        {
            Private.EnsureAllowed(route);
            sent.Add(route);
        }

        var denied = Request(DataClass.Confidential, RecognitionRoute.Online, TransformRoute.None);
        var allowed = Request(DataClass.Confidential, RecognitionRoute.Device, TransformRoute.None);

        var exception = Assert.Throws<RouteDeniedException>(() => Send(denied));
        Send(allowed);

        Assert.Equal(DenialReason.DataClass, exception.Decision.Denial);
        Assert.Equal([allowed], sent);
    }

    [Fact]
    public void PolicyFollowsTheLocalOnlySwitchImmediately()
    {
        var localOnly = new LocalOnlySwitch();
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Private), localOnly);
        var home = Request(DataClass.Public, RecognitionRoute.Home, TransformRoute.None);

        Assert.True(policy.Evaluate(home).Allowed);

        localOnly.Engage();
        Assert.Equal(DenialReason.LocalOnly, policy.Evaluate(home).Denial);
        Assert.True(policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.Local)).Allowed);

        localOnly.Release();
        Assert.True(policy.Evaluate(home).Allowed);
    }

    [Fact]
    public void LocalOnlyAlsoBlocksApprovedCompanyServices()
    {
        var localOnly = new LocalOnlySwitch();
        localOnly.Engage();
        var policy = new RoutePolicy(new RoutePolicyOptions(Edition.Business) { It = ApproveEverything() }, localOnly);

        var decision = policy.Evaluate(Request(DataClass.Public, RecognitionRoute.Device, TransformRoute.CompanyService));

        Assert.Equal(DenialReason.LocalOnly, decision.Denial);
    }
}
