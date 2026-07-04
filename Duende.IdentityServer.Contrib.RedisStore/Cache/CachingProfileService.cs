using System.Threading.Tasks;
using Duende.IdentityServer.Contrib.RedisStore;
using Duende.IdentityServer.Models;

namespace Duende.IdentityServer.Services;

/// <summary>
/// Caching decorator for IProfileService
/// </summary>
/// <seealso cref="Duende.IdentityServer.Services.IProfileService" />
public class CachingProfileService<TProfileService> : IProfileService
where TProfileService : class, IProfileService
{
    private readonly TProfileService _inner;
    private readonly ICache<IsActiveContextCacheEntry> _cache;
    private readonly ProfileServiceCachingOptions<TProfileService> _options;

    public CachingProfileService(TProfileService inner, ICache<IsActiveContextCacheEntry> cache, ProfileServiceCachingOptions<TProfileService> options)
    {
        _inner = inner;
        _cache = cache;
        _options = options;
    }

    /// <summary>
    /// This method is called whenever claims about the user are requested (e.g. during token creation or via the userinfo endpoint)
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns></returns>
    public async Task GetProfileDataAsync(ProfileDataRequestContext context)
    {
        await _inner.GetProfileDataAsync(context);
    }

    /// <summary>
    /// This method gets called whenever identity server needs to determine if the user is valid or active (e.g. if the user's account has been deactivated since they logged in).
    /// (e.g. during token issuance or validation).
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns></returns>
    public async Task IsActiveAsync(IsActiveContext context)
    {
        var key = $"{_options.KeyPrefix}{_options.KeySelector(context)}";

        if (_options.ShouldCache(context))
        {
            var entry = await _cache.GetOrAddAsync(key, _options.Expiration,
                async () =>
                {
                    await _inner.IsActiveAsync(context);
                    return new IsActiveContextCacheEntry { IsActive = context.IsActive };
                });

            context.IsActive = entry.IsActive;
        }
        else
        {
            await _inner.IsActiveAsync(context);
        }
    }
}
