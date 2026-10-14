using System.Text.RegularExpressions;
using System.Xml.Linq;
using AiGateway.Secrets;

namespace AiGateway.Tests;

public class GuardrailTests
{
    private static List<string> FilesMatching(string directory, string pattern) =>
        RepoPaths.SourceFiles(directory)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), pattern).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();

    [Fact]
    public void AiGateway_csproj_has_no_reference_to_Security()
    {
        var csproj = Path.Combine(RepoPaths.Src, "AiGateway", "AiGateway.csproj");

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

    [Fact]
    public void Dpapi_decryption_happens_in_exactly_one_place()
    {
        var hits = FilesMatching(RepoPaths.Src, @"ProtectedData\.Unprotect\s*\(").Select(h => h.Split(':')[0]);

        Assert.Equal(["SecretProtector.cs"], hits);
    }

    [Fact]
    public void Only_the_injecting_handler_asks_for_a_decrypted_key()
    {
        var callers = FilesMatching(RepoPaths.Src, @"SecretProtector\.Unprotect\s*\(").Select(h => h.Split(':')[0]);

        Assert.Equal(["CredentialInjectingHandler.cs"], callers);
    }

    [Fact]
    public void Security_cannot_send_requests_of_its_own()
    {
        var offenders = FilesMatching(
            Path.Combine(RepoPaths.Src, "AiGateway.Security"),
            @"\b(HttpClient|IHttpClientFactory|SocketsHttpHandler|HttpClientHandler|HttpMessageInvoker)\b|new\s+HttpRequestMessage");

        Assert.Empty(offenders);
    }

    [Fact]
    public void IRealKeyVault_cannot_hand_out_plaintext()
    {
        Type[] allowedReturns = [typeof(void), typeof(bool), typeof(Task), typeof(Task<bool>)];

        Assert.Empty(typeof(IRealKeyVault).GetProperties());
        Assert.All(typeof(IRealKeyVault).GetMethods(), method =>
        {
            Assert.Contains(method.ReturnType, allowedReturns);
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType.IsByRef);
        });
    }
}
