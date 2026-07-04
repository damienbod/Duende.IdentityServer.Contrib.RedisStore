using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Duende.IdentityServer.Contrib.RedisStore.Tests.Cache;

public class CachingProfileServiceTests
{
    private readonly FakeProfileService _inner;
    private readonly FakeCache<IDistributedCache> cache;
    private readonly FakeLogger<CachingProfileServiceTests> logger;
    private readonly CachingProfileService<FakeProfileService> profileServiceCache;
    private readonly IMemoryCache memoryCache;

    public CachingProfileServiceTests()
    {
        _inner = new FakeProfileService();
        memoryCache = new MemoryCache(new MemoryCacheOptions());
        logger = new FakeLogger<CachingProfileServiceTests>();
        cache = new FakeCache<IDistributedCache>(memoryCache);
        profileServiceCache = new CachingProfileService<FakeProfileService>(_inner, cache, new ProfileServiceCachingOptions<FakeProfileService>());
    }

    [Fact]
    public async Task AssertHitingDataStoreAtLeastOnce()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new Claim("sub", "1") }));
        var context = new IsActiveContext(principal, new Client(), "test");
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        context.IsActive.Should().BeTrue();
        logger.AccessCount["Cache hit for 1"].Should().Be(2);
    }

    [Fact]
    public async Task AssertIsInactive()
    {
        _inner.IsActive = cxt => cxt.IsActive = false;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new Claim("sub", "1") }));
        var context = new IsActiveContext(principal, new Client(), "test");
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        context.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task AssertExpiryOfCacheEntry()
    {
        var profileServiceCache = new CachingProfileService<FakeProfileService>(_inner, cache, new ProfileServiceCachingOptions<FakeProfileService>() { Expiration = TimeSpan.FromSeconds(1) });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new Claim("sub", "1") }));
        var context = new IsActiveContext(principal, new Client(), "test");
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        Thread.Sleep(1000);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        context.IsActive.Should().BeTrue();
        logger.AccessCount["Cache hit for 1"].Should().Be(2);
    }
}
