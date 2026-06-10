namespace GankedTV.Api.Contracts.Auth;

// Refresh is optional: in cookie mode the SPA posts an empty object and the token comes
// from the HttpOnly cookie instead. A request with neither yields 401 invalid_refresh.
public sealed record RefreshRequest(string? Refresh);
