using System.Threading.RateLimiting;
using GankedTV.Api.Services.Caching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace GankedTV.Api.Auth;

// Rate-limit policy for the anonymous device-authorization endpoints (POST /auth/device and
// /auth/device/token). Per-IP; the in-app RFC 8628 interval (slow_down) is the primary throttle,
// this is a belt-and-suspenders cap against a host hammering the endpoints. Kept out of Program.cs
// so the policy stays inside the coverage denominator per CLAUDE.md.
public static class DeviceRateLimiting
{
    public const string DevicePolicy = "device-auth";

    // 30/min per IP: comfortably above a legitimate 5s poll (~12/min) plus start calls, tight
    // enough to bound abuse.
    public const int PermitLimit = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimiterOptions AddDevicePolicy(this RateLimiterOptions options)
    {
        options.AddPolicy<string>(DevicePolicy, ctx =>
        {
            // Same per-IP caveat as AuthRateLimiting: behind a proxy, wire UseForwardedHeaders.
            var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var factory = ctx.RequestServices.GetRequiredService<RedisRateLimiterFactory>();
            return RateLimitPartition.Get(key, _ => factory.Create(DevicePolicy, key, PermitLimit, Window));
        });
        return options;
    }
}
