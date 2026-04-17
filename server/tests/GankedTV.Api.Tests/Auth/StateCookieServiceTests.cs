using FluentAssertions;
using GankedTV.Api.Auth;
using GankedTV.Api.Auth.State;
using Microsoft.Extensions.Options;

namespace GankedTV.Api.Tests.Auth;

public class StateCookieServiceTests
{
    private static StateCookieService BuildService(string secret = "state-secret-at-least-32-bytes-long-xxxx") =>
        new(Options.Create(new OAuthOptions
        {
            StateSecret = secret,
            WebOrigin = "http://localhost:5173",
        }));

    [Fact]
    public void IssueState_NoReturnTo_ProducesThreeSegmentValue()
    {
        var state = BuildService().IssueState(returnTo: null);

        state.Split('.').Should().HaveCount(3);
    }

    [Fact]
    public void ValidateState_MatchingCookieAndParam_ReturnsReturnTo()
    {
        var service = BuildService();
        var state = service.IssueState(returnTo: "/feed");

        var result = service.ValidateState(state, state);

        result.Ok.Should().BeTrue();
        result.ReturnTo.Should().Be("/feed");
    }

    [Fact]
    public void ValidateState_NoReturnTo_ReturnsNullReturnTo()
    {
        var service = BuildService();
        var state = service.IssueState(returnTo: null);

        var result = service.ValidateState(state, state);

        result.Ok.Should().BeTrue();
        result.ReturnTo.Should().BeNull();
    }

    [Fact]
    public void ValidateState_MismatchedNonce_ReturnsFalse()
    {
        var service = BuildService();
        var a = service.IssueState(returnTo: "/feed");
        var b = service.IssueState(returnTo: "/feed");

        service.ValidateState(a, b).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateState_TamperedHmac_ReturnsFalse()
    {
        var service = BuildService();
        var state = service.IssueState(returnTo: "/feed");
        var parts = state.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";

        service.ValidateState(tampered, tampered).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateState_MalformedInput_ReturnsFalse()
    {
        var service = BuildService();

        service.ValidateState("not.enough", "not.enough").Ok.Should().BeFalse();
        service.ValidateState("", "something").Ok.Should().BeFalse();
        service.ValidateState("something", "").Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateState_DifferentSecret_ReturnsFalse()
    {
        var issuer = BuildService(secret: "one-secret-that-is-at-least-32-bytes-xxxxx");
        var validator = BuildService(secret: "totally-different-secret-32-bytes-yyyyyyyy");
        var state = issuer.IssueState(returnTo: "/feed");

        validator.ValidateState(state, state).Ok.Should().BeFalse();
    }
}
