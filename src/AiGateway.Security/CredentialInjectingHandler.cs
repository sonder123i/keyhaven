using System.Net.Http.Headers;
using AiGateway.Secrets;
using AiGateway.Upstream;

namespace AiGateway.Security;

internal sealed class CredentialInjectingHandler(ICiphertextStore store) : DelegatingHandler
{
    private const string ApiKeyHeader = "x-api-key";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(UpstreamRequest.ProviderId, out var providerId))
        {
            throw new InvalidOperationException("Upstream request carries no provider id.");
        }

        var record = store.Find(providerId)
            ?? throw new InvalidOperationException($"No key is stored for provider '{providerId}'.");

        var plaintext = SecretProtector.Unprotect(record.ProviderId, record.Ciphertext);
        var bearer = request.Options.TryGetValue(UpstreamRequest.BearerAuth, out var useBearer) && useBearer;

        request.Headers.Authorization = null;
        request.Headers.Remove(ApiKeyHeader);
        if (bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        }
        else
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, plaintext);
        }

        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            // By now the header is on the wire; removing it keeps the plaintext out of whoever holds the request object.
            request.Headers.Authorization = null;
            request.Headers.Remove(ApiKeyHeader);
        }
    }
}
