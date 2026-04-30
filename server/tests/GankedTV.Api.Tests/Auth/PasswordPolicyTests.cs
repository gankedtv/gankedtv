using FluentAssertions;
using GankedTV.Api.Auth.Passwords;

namespace GankedTV.Api.Tests.Auth;

public class PasswordPolicyTests
{
    [Fact]
    public void Validate_GoodPassword_IsValid()
    {
        var result = PasswordPolicy.Validate("correct-horse-battery", "user@example.com", "user");
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Validate_TooShort_IsInvalid()
    {
        var result = PasswordPolicy.Validate("short1", "user@example.com", "user");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("12");
    }

    [Fact]
    public void Validate_NullOrEmpty_IsInvalid()
    {
        PasswordPolicy.Validate("", "user@example.com", "user").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_EqualToEmail_IsInvalid()
    {
        var result = PasswordPolicy.Validate("user@example.com", "user@example.com", "user");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("email");
    }

    [Fact]
    public void Validate_EqualToEmail_IsCaseInsensitive()
    {
        var result = PasswordPolicy.Validate("USER@example.com", "user@example.com", "user");
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_EqualToUsername_IsInvalid()
    {
        // Username equal-to check fires when the password text matches the username verbatim;
        // pad the username so it's >= 12 chars and the length floor doesn't pre-empt the rule.
        var result = PasswordPolicy.Validate("longusername123", "user@example.com", "longusername123");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("username");
    }

    [Theory]
    // All inputs are entries from PasswordPolicy.CommonPasswords AND >= 12 chars,
    // so the length check passes and execution actually reaches the common-password
    // branch. Shorter "common" entries (e.g. "password123" at 11 chars) are rejected
    // by the length floor first and don't exercise this code path.
    [InlineData("123456789012")]
    [InlineData("111111111111")]
    [InlineData("abc123abc123")]
    [InlineData("test1234test")]
    public void Validate_CommonPassword_IsInvalidWithCommonError(string pw)
    {
        var result = PasswordPolicy.Validate(pw, "u@e.com", "u");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("common");
    }

    [Fact]
    public void Validate_WhitespaceOnly_IsInvalid()
    {
        // A 12-space password used to slip past IsNullOrEmpty + the length floor;
        // the policy now uses IsNullOrWhiteSpace to treat blank input as missing.
        PasswordPolicy.Validate("            ", "u@e.com", "u").IsValid.Should().BeFalse();
    }
}
