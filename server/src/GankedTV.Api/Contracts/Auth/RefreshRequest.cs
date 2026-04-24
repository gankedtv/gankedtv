using System.ComponentModel.DataAnnotations;

namespace GankedTV.Api.Contracts.Auth;

public sealed record RefreshRequest(
    [property: Required]
    string Refresh);
