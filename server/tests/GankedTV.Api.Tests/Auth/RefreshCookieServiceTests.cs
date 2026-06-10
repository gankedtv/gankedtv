using FluentAssertions;
using GankedTV.Api.Auth.Cookies;
using GankedTV.Api.Auth.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GankedTV.Api.Tests.Auth;

public class RefreshCookieServiceTests
{
    private static RefreshCookieService BuildService(bool enabled = true, string environment = "Production", int expiryDays = 30)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        return new RefreshCookieService(
            Options.Create(new RefreshCookieOptions { Enabled = enabled }),
            Options.Create(new RefreshTokenOptions { ExpiryDays = expiryDays }),
            env);
    }

    private static string SetCookieHeader(HttpContext ctx) =>
        ctx.Response.Headers.SetCookie.ToString();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enabled_ReflectsOptions(bool enabled)
    {
        BuildService(enabled).Enabled.Should().Be(enabled);
    }

    [Fact]
    public void Append_Production_SetsSecureNoneCookieScopedToAuth()
    {
        var ctx = new DefaultHttpContext();

        BuildService(environment: "Production", expiryDays: 14).Append(ctx.Response, "tok-123");

        var header = SetCookieHeader(ctx);
        header.Should().StartWith($"{RefreshCookieService.CookieName}=tok-123");
        header.Should().Contain("httponly");
        header.Should().Contain("secure");
        header.Should().ContainEquivalentOf("samesite=none");
        header.Should().Contain("path=/auth");
        header.Should().Contain($"max-age={(int)TimeSpan.FromDays(14).TotalSeconds}");
    }

    [Fact]
    public void Append_Development_FallsBackToLaxWithoutSecure()
    {
        var ctx = new DefaultHttpContext();

        BuildService(environment: "Development").Append(ctx.Response, "tok-123");

        var header = SetCookieHeader(ctx);
        header.Should().NotContain("secure");
        header.Should().ContainEquivalentOf("samesite=lax");
    }

    [Fact]
    public void Clear_EmitsDeletionCookieWithMatchingPath()
    {
        var ctx = new DefaultHttpContext();

        BuildService().Clear(ctx.Response);

        var header = SetCookieHeader(ctx);
        header.Should().StartWith($"{RefreshCookieService.CookieName}=");
        header.Should().Contain("path=/auth");
        header.Should().MatchRegex("expires=|max-age=0");
    }

    [Fact]
    public void Read_PresentCookie_ReturnsValue()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{RefreshCookieService.CookieName}=tok-456";

        BuildService().Read(ctx.Request).Should().Be("tok-456");
    }

    [Fact]
    public void Read_MissingOrEmptyCookie_ReturnsNull()
    {
        var service = BuildService();

        service.Read(new DefaultHttpContext().Request).Should().BeNull();

        var empty = new DefaultHttpContext();
        empty.Request.Headers.Cookie = $"{RefreshCookieService.CookieName}=";
        service.Read(empty.Request).Should().BeNull();
    }
}
