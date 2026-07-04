using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityServer.Contrib.RedisStore;
using Duende.IdentityServer.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Duende.IdentityServer.Services;

/// <summary>
/// Caching decorator for IProfileService
/// </summary>
/// <seealso cref="Duende.IdentityServer.Services.IProfileService" />
public class CachingProfileService<TProfileService> : IProfileService
where TProfileService : class, IProfileService
{
    private readonly TProfileService _inner;
    private readonly IDistributedCache _cache;
    private readonly ProfileServiceCachingOptions<TProfileService> _options;

    public CachingProfileService(TProfileService inner, IDistributedCache cache, ProfileServiceCachingOptions<TProfileService> options)
    {
        _inner = inner;
        _cache = cache;
        _options = options;
    }

    /// <summary>
    /// This method is called whenever claims about the user are requested (e.g. during token creation or via the userinfo endpoint)
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns></returns>
    public async Task GetProfileDataAsync(ProfileDataRequestContext context, CancellationToken  ct)
    {
        await _inner.GetProfileDataAsync(context, ct);
    }

    /// <summary>
    /// This method gets called whenever identity server needs to determine if the user is valid or active (e.g. if the user's account has been deactivated since they logged in).
    /// (e.g. during token issuance or validation).
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns></returns>
    public async Task IsActiveAsync(IsActiveContext context, CancellationToken ct)
    {
        var key = $"{_options.KeyPrefix}{_options.KeySelector(context)}";

        if (_options.ShouldCache(context))
        {
            // Try to get from cache
            var cachedBytes = await _cache.GetAsync(key, ct);

            if (cachedBytes != null)
            {
                // Deserialize from cache
                var entry = JsonSerializer.Deserialize<IsActiveContextCacheEntry>(cachedBytes);
                context.IsActive = entry?.IsActive ?? false;
            }
            else
            {
                // Not in cache, call inner service
                await _inner.IsActiveAsync(context, ct);

                // Store in cache
                var entry = new IsActiveContextCacheEntry { IsActive = context.IsActive };
                var serialized = JsonSerializer.SerializeToUtf8Bytes(entry);

                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _options.Expiration
                };

                await _cache.SetAsync(key, serialized, options, ct);
            }
        }
        else
        {
            await _inner.IsActiveAsync(context, ct);
        }
    }
}
