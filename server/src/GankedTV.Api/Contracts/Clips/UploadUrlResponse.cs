namespace GankedTV.Api.Contracts.Clips;

public sealed record UploadUrlResponse(string Url, DateTimeOffset ExpiresAt);
