using System.Security.Cryptography;
using GankedTV.Api.Auth.ApiKeys;
using GankedTV.Api.Auth.Tokens;
using GankedTV.Api.Data;
using GankedTV.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GankedTV.Api.Auth.Devices;

// OAuth 2.0 Device Authorization Grant (RFC 8628). A desktop client starts a flow, the user
// approves it in the browser, and the client polls to exchange the device code for a minted
// ApiKey — so this reuses the entire API-key credential layer as its back-end.
public sealed class DeviceAuthorizationService
{
    public const string DeviceCodePrefix = "dvc_";
    // Unambiguous alphabet (no 0/O/1/I) per RFC 8628 §6.1 so users can't mistype the code.
    private const string UserCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int UserCodeLength = 8;
    private const int InsertRetries = 3;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int IntervalSeconds = 5;

    private readonly GankedTvDbContext _db;
    private readonly ApiKeyService _apiKeys;
    private readonly TimeProvider _clock;

    public DeviceAuthorizationService(GankedTvDbContext db, ApiKeyService apiKeys, TimeProvider? clock = null)
    {
        _db = db;
        _apiKeys = apiKeys;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<DeviceStartResult> StartAsync(string? clientName, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var name = string.IsNullOrWhiteSpace(clientName) ? null : clientName.Trim();

        // Retry on the rare unique collision (device_code hash or user_code) before giving up.
        for (var attempt = 0; ; attempt++)
        {
            var rawDeviceCode = OpaqueToken.Generate(DeviceCodePrefix);
            var userCode = GenerateUserCode();
            _db.DeviceAuthorizations.Add(new DeviceAuthorization
            {
                DeviceCodeHash = OpaqueToken.Hash(rawDeviceCode),
                UserCode = userCode,
                ClientName = name,
                Status = DeviceAuthorizationStatuses.Pending,
                ExpiresAt = now + Lifetime,
                IntervalSeconds = IntervalSeconds,
                CreatedAt = now,
            });
            try
            {
                await _db.SaveChangesAsync(ct);
                return new DeviceStartResult(rawDeviceCode, userCode, now + Lifetime, IntervalSeconds);
            }
            catch (DbUpdateException) when (attempt < InsertRetries)
            {
                // Drop the failed add from the change tracker and try fresh codes.
                _db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<DevicePollResult> PollAsync(string deviceCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(deviceCode) || !deviceCode.StartsWith(DeviceCodePrefix, StringComparison.Ordinal))
        {
            return DevicePollResult.Of(DevicePollStatus.Expired);
        }

        var hash = OpaqueToken.Hash(deviceCode);
        var now = _clock.GetUtcNow();
        var row = await _db.DeviceAuthorizations.SingleOrDefaultAsync(d => d.DeviceCodeHash == hash, ct);

        if (row is null || row.ExpiresAt <= now)
        {
            return DevicePollResult.Of(DevicePollStatus.Expired);
        }
        if (row.Status == DeviceAuthorizationStatuses.Denied)
        {
            return DevicePollResult.Of(DevicePollStatus.Denied);
        }
        if (row.Status == DeviceAuthorizationStatuses.Pending)
        {
            // RFC 8628 §3.5: a client polling faster than the interval gets slow_down. Recording
            // every poll instant is required to detect that, so this write is not throttled the
            // way ApiKey.LastUsedAt is (that one is cosmetic; this one is functional).
            if (row.LastPolledAt is not null && now - row.LastPolledAt.Value < TimeSpan.FromSeconds(row.IntervalSeconds))
            {
                return DevicePollResult.Of(DevicePollStatus.SlowDown);
            }
            row.LastPolledAt = now;
            await _db.SaveChangesAsync(ct);
            return DevicePollResult.Of(DevicePollStatus.Pending);
        }

        // Approved: mint the key for the approving user.
        var mint = await _apiKeys.CreateAsync(row.UserId!.Value, row.ClientName ?? "rewynd", null, ct);
        if (!mint.IsSuccess)
        {
            // Don't consume the row on a failed mint (e.g. the user is at the key cap): keep it
            // approved so the client's next poll reports too_many_keys accurately, and so the
            // flow self-recovers once the user revokes a key. The sweep clears it on expiry.
            return DevicePollResult.Of(DevicePollStatus.TooManyKeys);
        }

        // Consume the row so the device code is single-use.
        _db.DeviceAuthorizations.Remove(row);
        await _db.SaveChangesAsync(ct);
        return DevicePollResult.Success(mint.RawKey!);
    }

    public async Task<DeviceLookupResult?> LookupByUserCodeAsync(string userCode, CancellationToken ct = default)
    {
        var normalized = NormalizeUserCode(userCode);
        var now = _clock.GetUtcNow();
        var row = await _db.DeviceAuthorizations
            .AsNoTracking()
            .Where(d => d.UserCode == normalized && d.ExpiresAt > now)
            .Select(d => new DeviceLookupResult(d.ClientName, d.Status))
            .SingleOrDefaultAsync(ct);
        return row;
    }

    public Task<DeviceDecisionOutcome> ApproveAsync(Guid userId, string userCode, CancellationToken ct = default) =>
        DecideAsync(userId, userCode, DeviceAuthorizationStatuses.Approved, ct);

    public Task<DeviceDecisionOutcome> DenyAsync(Guid userId, string userCode, CancellationToken ct = default) =>
        DecideAsync(userId, userCode, DeviceAuthorizationStatuses.Denied, ct);

    private async Task<DeviceDecisionOutcome> DecideAsync(
        Guid userId, string userCode, string decision, CancellationToken ct)
    {
        var normalized = NormalizeUserCode(userCode);
        var now = _clock.GetUtcNow();
        var row = await _db.DeviceAuthorizations
            .SingleOrDefaultAsync(d => d.UserCode == normalized, ct);

        if (row is null || row.ExpiresAt <= now)
        {
            return DeviceDecisionOutcome.NotFound;
        }
        // Only a still-pending request can be decided; a second decision (or a poll that already
        // consumed it) is a conflict, not a silent no-op.
        if (row.Status != DeviceAuthorizationStatuses.Pending)
        {
            return DeviceDecisionOutcome.AlreadyDecided;
        }

        row.Status = decision;
        if (decision == DeviceAuthorizationStatuses.Approved)
        {
            row.UserId = userId;
            row.ApprovedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return DeviceDecisionOutcome.Ok;
    }

    // Display form is grouped with a dash ("WDJB-MJHT"); we store the raw 8 chars and normalize
    // user input (uppercase, strip non-alphanumerics) so a typed dash/space/lowercase still matches.
    public static string NormalizeUserCode(string input) =>
        new(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string FormatUserCode(string userCode) =>
        userCode.Length == UserCodeLength ? $"{userCode[..4]}-{userCode[4..]}" : userCode;

    private static string GenerateUserCode()
    {
        Span<char> chars = stackalloc char[UserCodeLength];
        for (var i = 0; i < UserCodeLength; i++)
        {
            chars[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }
        return new string(chars);
    }
}

public sealed record DeviceStartResult(string DeviceCode, string UserCode, DateTimeOffset ExpiresAt, int IntervalSeconds);

public sealed record DeviceLookupResult(string? ClientName, string Status);

public enum DevicePollStatus
{
    Pending,
    SlowDown,
    Denied,
    Expired,
    TooManyKeys,
    Approved,
}

public readonly record struct DevicePollResult(DevicePollStatus Status, string? ApiKey)
{
    public static DevicePollResult Of(DevicePollStatus status) => new(status, null);
    public static DevicePollResult Success(string apiKey) => new(DevicePollStatus.Approved, apiKey);
}

public enum DeviceDecisionOutcome
{
    Ok,
    NotFound,
    AlreadyDecided,
}
