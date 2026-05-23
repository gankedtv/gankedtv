namespace GankedTV.Api.Contracts.Clips;

// Response for GET /clips/{id}/stream. Status is "ready" (HlsUrl set, 200) or "pending"
// (HlsUrl null, 202 — a JIT rendition is being built; the client polls). A failed transcode
// surfaces as a 503 problem instead.
public sealed record StreamResponse(string? HlsUrl, string Status);
