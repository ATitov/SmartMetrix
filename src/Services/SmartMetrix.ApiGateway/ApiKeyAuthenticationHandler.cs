using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemes,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<OperatorApiOptions> options)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    public const string SchemeName = "SmartMetrixApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-API-Key", out var supplied) || supplied.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());

        foreach (var credential in options.Value.ApiKeys)
        {
            if (!FixedTimeEquals(credential.Key, supplied[0]!)) continue;
            var role = credential.Value.Trim().ToLowerInvariant();
            if (role is not (OperatorRoles.Operator or OperatorRoles.Engineer)) continue;
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, $"api-key:{credential.Key[..Math.Min(8, credential.Key.Length)]}"),
                new Claim(ClaimTypes.Role, role)
            ], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
