using GankedTV.Api.Auth;
using GankedTV.Api.Services.ObjectStorage;

namespace GankedTV.Api.Configuration;

/// <summary>
/// Fail-fast validation of required secrets/config when running in Production. Called from
/// <c>Program.cs</c> after options binding; if <see cref="Validate"/> returns any problems the
/// host refuses to boot with a clear, aggregated error rather than running misconfigured and
/// failing later on the first request. Pure (no I/O) so it is unit-tested directly.
/// </summary>
/// <remarks>
/// The caller in <c>Program.cs</c> passes values read straight from <c>Environment.GetEnvironmentVariable</c>,
/// deliberately bypassing the DI options binding (which falls back to <c>appsettings</c>/config and
/// dev defaults like <c>minioadmin</c> or <c>localhost:5173</c>). This enforces an <b>env-only secret
/// contract</b>: required secrets must arrive via environment variables, per <c>DEPLOYMENT.md</c>.
/// If a future deployment wires secrets through a config provider instead (Azure Key Vault, AWS
/// Parameter Store, etc.), this env-only check will report them "missing" — update both the call
/// site (to read the bound options) and this contract before integrating a vault.
/// The Vaultwarden integration preserves this contract: it injects fetched secrets into the
/// environment (not a config provider) before this validator runs, so the env-only check sees them
/// as ordinary env vars.
/// </remarks>
public static class ProductionStartupValidator
{
    /// <summary>Dev-default object-storage credentials that must never reach Production.</summary>
    private const string DevDefaultCredential = "minioadmin";

    private const int MinJwtSecretBytes = 32;

    public static IReadOnlyList<string> Validate(
        string? connectionString,
        JwtOptions jwt,
        OAuthOptions oauth,
        S3Options s3,
        string? corsOrigins)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add("DATABASE_URL must be set.");
        }

        if (string.IsNullOrWhiteSpace(jwt.Secret))
        {
            errors.Add("JWT_SECRET must be set.");
        }
        else if (System.Text.Encoding.UTF8.GetByteCount(jwt.Secret) < MinJwtSecretBytes)
        {
            errors.Add($"JWT_SECRET must be at least {MinJwtSecretBytes} bytes.");
        }

        if (string.IsNullOrWhiteSpace(oauth.WebOrigin))
        {
            errors.Add("WEB_ORIGIN must be set.");
        }

        if (string.IsNullOrWhiteSpace(corsOrigins))
        {
            errors.Add("CORS_ORIGINS must be set.");
        }

        if (string.IsNullOrWhiteSpace(s3.Endpoint))
        {
            errors.Add("S3_ENDPOINT must be set.");
        }

        if (string.IsNullOrWhiteSpace(s3.AccessKey)
            || string.Equals(s3.AccessKey, DevDefaultCredential, StringComparison.Ordinal))
        {
            errors.Add("S3_ACCESS_KEY must be set to a non-default value.");
        }

        if (string.IsNullOrWhiteSpace(s3.SecretKey)
            || string.Equals(s3.SecretKey, DevDefaultCredential, StringComparison.Ordinal))
        {
            errors.Add("S3_SECRET_KEY must be set to a non-default value.");
        }

        if (string.IsNullOrWhiteSpace(s3.PublicUrl))
        {
            errors.Add("S3_PUBLIC_URL must be set.");
        }

        return errors;
    }
}
