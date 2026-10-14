using System.Xml.Linq;

namespace AiGateway.Tests;

public class GuardrailTests
{
    [Fact]
    public void AiGateway_csproj_has_no_reference_to_Security()
    {
        var csproj = Path.Combine(RepoRoot(), "src", "AiGateway", "AiGateway.csproj");

        var includes = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty);

        Assert.DoesNotContain(includes, i => i.Contains("AiGateway.Security", StringComparison.OrdinalIgnoreCase));
    }

    // The compiler drops unused references from metadata, so this only catches actual use; the csproj check above catches the bare reference.
    [Fact]
    public void AiGateway_assembly_does_not_reference_Security()
    {
        var referenced = typeof(AiGatewayServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.DoesNotContain("AiGateway.Security", referenced);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiGateway.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AiGateway.sln not found above " + AppContext.BaseDirectory);
    }
}
