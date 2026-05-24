namespace GankedTV.Api.Services.Clips;

public enum ImportUrlValidationError
{
    InvalidUrl,
    UnsupportedHost,
}

public interface IClipImportUrlValidator
{
    // Parses the user-supplied URL and confirms its host is on the configured allow-list.
    // Returns the canonical https://host/path form on success (so the worker re-validates
    // against the same string the user submitted). Returns null when the URL is malformed
    // or the host isn't allowed; the error tells callers which case.
    bool TryParse(string? url, out string normalised, out ImportUrlValidationError error);
}
