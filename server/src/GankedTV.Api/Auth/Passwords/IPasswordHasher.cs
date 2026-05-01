namespace GankedTV.Api.Auth.Passwords;

public interface IPasswordHasher
{
    string Algorithm { get; }

    string Hash(string password);

    bool Verify(string password, string encoded);
}
