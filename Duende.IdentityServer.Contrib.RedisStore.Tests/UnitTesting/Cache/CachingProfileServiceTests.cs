using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;

namespace Duende.IdentityServer.Contrib.RedisStore.Tests.Cache;

public class CachingProfileServiceTests
{
    private readonly FakeProfileService _inner;
    private readonly FakeCache<IsActiveContextCacheEntry> _cache;
    private readonly FakeLogger<IDistributedCache> _logger;
    private readonly CachingProfileService<FakeProfileService> _profileServiceCache;
    private readonly IMemoryCache memoryCache;

    public CachingProfileServiceTests()
    {
        _inner = new FakeProfileService();
        memoryCache = new MemoryCache(new MemoryCacheOptions());
        _logger = new FakeLogger<IDistributedCache>();
        _cache = new FakeCache<IsActiveContextCacheEntry>(memoryCache, _logger);
        _profileServiceCache = new CachingProfileService<FakeProfileService>(_inner, _cache, new ProfileServiceCachingOptions<FakeProfileService>());
    }

    [Fact]
    public async Task AssertHitingDataStoreAtLeastOnce()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new Claim("sub", "1") }));
        var context = new IsActiveContext(principal, new Client(), "test");
        await _profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await _profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await _profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        context.IsActive.Should().BeTrue();
        _logger.AccessCount["Cache hit for 1"].Should().Be(2);
    }

    [Fact]
    public async Task AssertIsInactive()
    {
        _inner.IsActive = cxt => cxt.IsActive = false;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new Claim("sub", "1") }));
        var context = new IsActiveContext(principal, new Client(), "test");
        await _profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        context.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task AssertExpiryOfCacheEntry()
    {
        var profileServiceCache = new CachingProfileService<FakeProfileService>(_inner, _cache, new ProfileServiceCachingOptions<FakeProfileService>() { Expiration = TimeSpan.FromSeconds(1) });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim> { new Claim("sub", "1") }));
        var context = new IsActiveContext(principal, new Client(), "test");
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        Thread.Sleep(1000);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        await profileServiceCache.IsActiveAsync(context, CancellationToken.None);
        context.IsActive.Should().BeTrue();
        _logger.AccessCount["Cache hit for 1"].Should().Be(2);
    }
}
