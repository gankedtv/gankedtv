using FluentAssertions;
using GankedTV.Api.Auth.Passwords;

namespace GankedTV.Api.Tests.Auth;

public class Argon2idPasswordHasherTests
{
    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Algorithm_IsArgon2id()
    {
        _hasher.Algorithm.Should().Be("argon2id");
    }

    [Fact]
    public void Hash_VerifyRoundtrip_Succeeds()
    {
        var encoded = _hasher.Hash("correct-horse-battery-staple");
        _hasher.Verify("correct-horse-battery-staple", encoded).Should().BeTrue();
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        var encoded = _hasher.Hash("correct-horse-battery-staple");
        _hasher.Verify("wrong-password-12345", encoded).Should().BeFalse();
    }

    [Fact]
    public void Hash_RepeatedCalls_ProduceDifferentEncodingsButBothVerify()
    {
        var first = _hasher.Hash("super-secret-password");
        var second = _hasher.Hash("super-secret-password");

        // Distinct salts → distinct encoded strings, even for the same plaintext.
        first.Should().NotBe(second);
        _hasher.Verify("super-secret-password", first).Should().BeTrue();
        _hasher.Verify("super-secret-password", second).Should().BeTrue();
    }

    [Fact]
    public void Hash_EmittedFormat_IsPhcArgon2id()
    {
        var encoded = _hasher.Hash("hello-world-12345");
        encoded.Should().StartWith("$argon2id$v=19$m=");
        encoded.Split('$').Should().HaveCount(6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-valid-phc-string")]
    [InlineData("$argon2id$v=99$m=19456,t=2,p=1$abc$def")] // wrong version
    [InlineData("$argon2id$v=19$badparams$abc$def")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$!!!notbase64!!!$abc")]
    [InlineData("$bcrypt$v=19$m=1,t=1,p=1$abc$def")] // wrong algo
    [InlineData("$argon2id$x=19$m=19456,t=2,p=1$YWJj$ZGVm")] // version field not "v="
    [InlineData("$argon2id$v=xx$m=19456,t=2,p=1$YWJj$ZGVm")] // version not an integer
    [InlineData("$argon2id$v=19$=5,t=2,p=1$YWJj$ZGVm")] // empty param key (eq == 0)
    [InlineData("$argon2id$v=19$m=xx,t=2,p=1$YWJj$ZGVm")] // param value not an integer
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1,z=9$YWJj$ZGVm")] // unknown param key
    [InlineData("$argon2id$v=19$m=0,t=2,p=1$YWJj$ZGVm")] // non-positive memory
    [InlineData("$argon2id$v=19$t=2,p=1$YWJj$ZGVm")] // missing memory param
    public void Verify_WithMalformedEncoded_ReturnsFalse(string encoded)
    {
        _hasher.Verify("anything", encoded).Should().BeFalse();
    }

    [Fact]
    public void Verify_WithEmptyPassword_ReturnsFalse()
    {
        var encoded = _hasher.Hash("real-password");
        _hasher.Verify("", encoded).Should().BeFalse();
    }

    [Fact]
    public void Hash_WithEmptyPassword_Throws()
    {
        Action act = () => _hasher.Hash("");
        act.Should().Throw<ArgumentException>();
    }
}
