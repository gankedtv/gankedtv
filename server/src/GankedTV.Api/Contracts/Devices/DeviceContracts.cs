using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Devices;

// Client-initiated. Optional label shown on the approval page and used to name the minted key.
public sealed record DeviceStartRequest([property: StringLength(100)] string? ClientName);

public sealed record DeviceStartResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

public sealed record DeviceTokenRequest([property: Required] string DeviceCode);

// Success shape from the poll endpoint. `Token` is the minted API key (gtv_…), returned once.
public sealed record DeviceTokenResponse(string Token, string TokenType);

// Interactive approval-page contracts.
public sealed record DeviceLookupResponse(string? ClientName, string Status);

public sealed record DeviceDecisionRequest([property: Required] string UserCode);
