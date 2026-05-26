using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Configuration;
using GankedTV.Api.Services.ObjectStorage;

namespace GankedTV.Api.Tests.Configuration;

public class ProductionStartupValidatorTests
{
    private static JwtOptions ValidJwt() => new()
    {
        Secret = "a-production-jwt-secret-at-least-32b!",
        Issuer = "gankedtv",
        Audience = "gankedtv-web",
    };

    private static S3Options ValidS3() => new()
    {
        Endpoint = "https://s3.example.com",
        AccessKey = "prod-access-key",
        SecretKey = "prod-secret-key",
        PublicUrl = "https://cdn.example.com",
    };

    private static IReadOnlyList<string> Validate(
        string? connectionString = "Host=db;Database=gankedtv",
        JwtOptions? jwt = null,
        OAuthOptions? oauth = null,
        S3Options? s3 = null,
        string? corsOrigins = "https://ganked.tv")
        => ProductionStartupValidator.Validate(
            connectionString,
            jwt ?? ValidJwt(),
            oauth ?? new OAuthOptions { WebOrigin = "https://ganked.tv" },
            s3 ?? ValidS3(),
            corsOrigins);

    [Fact]
    public void Validate_AllPresent_ReturnsNoErrors()
    {
        Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingConnectionString_Flags()
    {
        Validate(connectionString: "  ").Should().ContainSingle(e => e.Contains("DATABASE_URL"));
    }

    [Fact]
    public void Validate_MissingJwtSecret_Flags()
    {
        var jwt = ValidJwt();
        jwt.Secret = "";
        Validate(jwt: jwt).Should().ContainSingle(e => e.Contains("JWT_SECRET"));
    }

    [Fact]
    public void Validate_ShortJwtSecret_Flags()
    {
        var jwt = ValidJwt();
        jwt.Secret = "too-short";
        Validate(jwt: jwt).Should().ContainSingle(e => e.Contains("at least 32 bytes"));
    }

    [Fact]
    public void Validate_MissingWebOrigin_Flags()
    {
        Validate(oauth: new OAuthOptions { WebOrigin = "" })
            .Should().ContainSingle(e => e.Contains("WEB_ORIGIN"));
    }

    [Fact]
    public void Validate_MissingCorsOrigins_Flags()
    {
        Validate(corsOrigins: null).Should().ContainSingle(e => e.Contains("CORS_ORIGINS"));
    }

    [Fact]
    public void Validate_MissingS3Endpoint_Flags()
    {
        var s3 = ValidS3();
        s3.Endpoint = "";
        Validate(s3: s3).Should().ContainSingle(e => e.Contains("S3_ENDPOINT"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("minioadmin")]
    public void Validate_MissingOrDefaultS3AccessKey_Flags(string accessKey)
    {
        var s3 = ValidS3();
        s3.AccessKey = accessKey;
        Validate(s3: s3).Should().ContainSingle(e => e.Contains("S3_ACCESS_KEY"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("minioadmin")]
    public void Validate_MissingOrDefaultS3SecretKey_Flags(string secretKey)
    {
        var s3 = ValidS3();
        s3.SecretKey = secretKey;
        Validate(s3: s3).Should().ContainSingle(e => e.Contains("S3_SECRET_KEY"));
    }

    [Fact]
    public void Validate_MissingS3PublicUrl_Flags()
    {
        var s3 = ValidS3();
        s3.PublicUrl = null;
        Validate(s3: s3).Should().ContainSingle(e => e.Contains("S3_PUBLIC_URL"));
    }

    [Fact]
    public void Validate_AllMissing_AggregatesEveryError()
    {
        var errors = ProductionStartupValidator.Validate(
            connectionString: null,
            new JwtOptions { Secret = "", Issuer = "x", Audience = "x" },
            new OAuthOptions { WebOrigin = "" },
            new S3Options(),
            corsOrigins: null);

        // connection, jwt, weborigin, cors, s3 endpoint, s3 access, s3 secret, s3 public url
        errors.Should().HaveCount(8);
    }
}
