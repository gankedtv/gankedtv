using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace GankedTV.Api.Contracts.Users;

public sealed record UpdateMeRequest(
    [property: StringLength(30)]
    string? Username,
    [property: StringLength(500)]
    string? Bio,
    // #RRGGBB. Server-side ValidateAccentColor enforces the regex; the validator distinguishes
    // "field absent" (don't touch), "explicit null" (clear), and any other string (regex-check).
    [property: StringLength(7)]
    string? AccentColor,
    SocialLinksDto? SocialLinks);

// Carries the whitelisted handles for the per-user social-links jsonb column. Each handle is
// validated app-side (length + char regex) before persisting; empty string on any field clears
// that platform. Explicit JsonPropertyName on YouTube because the default camelCase policy
// renders consecutive capitals as `youTube` — the SPA wants `youtube` to match user-facing copy.
public sealed record SocialLinksDto(
    [property: StringLength(32)]
    string? Twitch,
    [property: StringLength(32)]
    [property: JsonPropertyName("youtube")]
    string? YouTube,
    [property: StringLength(32)]
    string? Twitter);
