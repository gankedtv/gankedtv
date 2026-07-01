namespace GankedTV.Api.Data.Entities;

// One in-flight OAuth 2.0 Device Authorization Grant (RFC 8628). A desktop client starts one,
// the user approves it in the browser, and the client polls until it can exchange the device
// code for a minted API key. Rows are short-lived and swept once expired.
public class DeviceAuthorization
{
    public Guid Id { get; set; }

    // SHA-256 of the high-entropy device_code the client holds. The raw value is returned once
    // at start and never stored.
    public required string DeviceCodeHash { get; set; }

    // Short human-typable code the user enters/confirms in the browser (e.g. "WDJB-MJHT").
    public required string UserCode { get; set; }

    // Label the client supplied (e.g. "rewynd"); shown on the approval page and used to name
    // the minted key.
    public string? ClientName { get; set; }

    // Null until a signed-in user approves; then set to the approving user (who the minted key
    // will belong to).
    public Guid? UserId { get; set; }

    public required string Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int IntervalSeconds { get; set; }
    // Last poll instant, used to enforce the RFC 8628 slow_down / interval contract.
    public DateTimeOffset? LastPolledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public User? User { get; set; }
}

public static class DeviceAuthorizationStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
}
