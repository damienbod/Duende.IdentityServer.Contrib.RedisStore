using Duende.IdentityServer.Contrib.RedisStore.Tests;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace Microsoft.Extensions.DependencyInjection;

internal static class TestingExtensions
{

    public static IIdentityServerBuilder AddFakeMemeoryCaching(this IIdentityServerBuilder builder)
    {
        builder.Services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        builder.Services.AddSingleton<IDistributedCache, MemoryDistributedCache>();
        return builder;
    }

    public static IIdentityServerBuilder AddFakeLogger<T>(this IIdentityServerBuilder builder)
    {
        builder.Services.AddSingleton(new FakeLogger<T>());
        return builder;
    }
}
