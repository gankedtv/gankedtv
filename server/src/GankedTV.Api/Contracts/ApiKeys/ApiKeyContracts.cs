using GankedTV.Api.Data.Entities;

namespace GankedTV.Api.Contracts.ApiKeys;

// List/metadata view — never carries the secret or its hash. Keys are minted by the
// device-authorization flow; this contract only supports viewing and revoking them.
public sealed record ApiKeyResponse(
    Guid Id,
    string? Name,
    string KeyPrefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public static class ApiKeyMappings
{
    public static ApiKeyResponse ToResponse(this ApiKey k) =>
        new(k.Id, k.Name, k.KeyPrefix, k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.RevokedAt);
}
