using Microsoft.AspNetCore.Http;

namespace AiGateway.Hosting;

internal static class AnthropicErrors
{
    internal static IResult Create(int statusCode, string type, string message) =>
        Results.Json(new { type = "error", error = new { type, message } }, statusCode: statusCode);
}
