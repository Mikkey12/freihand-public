using System.Reflection;
using Freihand.Adapters.Fakes;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests;

/// <summary>Kern und Adapter bleiben plattformunabhängig.</summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "Freihand.Windows",
        "Freihand.App",
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
    ];

    public static TheoryData<string> PlatformIndependentAssemblies => new()
    {
        typeof(TextSegment).Assembly.GetName().Name!,
        typeof(EchoTransformer).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(PlatformIndependentAssemblies))]
    public void DoesNotReferenceWindowsOrUi(string assemblyName)
    {
        var references = Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name);

        Assert.Empty(references.Intersect(ForbiddenAssemblies));
    }
}
