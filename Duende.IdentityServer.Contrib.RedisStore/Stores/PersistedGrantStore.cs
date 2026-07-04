using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;

namespace Duende.IdentityServer.Contrib.RedisStore.Stores;

/// <summary>
/// Provides the implementation of IPersistedGrantStore for Redis Cache.
/// </summary>
public class PersistedGrantStore : IPersistedGrantStore
{
    protected readonly RedisOperationalStoreOptions _options;

    protected readonly IDatabase _database;

    protected readonly ILogger<PersistedGrantStore> _logger;

    protected TimeProvider _clock;

    public PersistedGrantStore(RedisMultiplexer<RedisOperationalStoreOptions> multiplexer, ILogger<PersistedGrantStore> logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);

        _options = multiplexer.RedisOptions;
        _database = multiplexer.Database;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock;
    }

    protected string GetKey(string key) => $"{_options.KeyPrefix}{key}";

    protected string GetSetKey(string subjectId) => $"{_options.KeyPrefix}{subjectId}";

    protected string GetSetKey(string subjectId, string clientId) => $"{_options.KeyPrefix}{subjectId}:{clientId}";

    protected string GetSetKeyWithType(string subjectId, string clientId, string type) => $"{_options.KeyPrefix}{subjectId}:{clientId}:{type}";

    protected string GetSetKeyWithSession(string subjectId, string clientId, string sessionId) => $"{_options.KeyPrefix}{subjectId}:{clientId}:{sessionId}";

    public virtual async Task StoreAsync(PersistedGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        try
        {
            var data = ConvertToJson(grant);
            var grantKey = GetKey(grant.Key);

            var expiresIn = grant.Expiration - _clock.GetUtcNow();
            var expiresInRedis = new StackExchange.Redis.Expiration(expiresIn.Value);

            if (!string.IsNullOrEmpty(grant.SubjectId))
            {
                var setKeyforType = GetSetKeyWithType(grant.SubjectId, grant.ClientId, grant.Type);
                var setKeyforSubject = GetSetKey(grant.SubjectId);
                var setKeyforClient = GetSetKey(grant.SubjectId, grant.ClientId);
                var setKetforSession = GetSetKeyWithSession(grant.SubjectId, grant.ClientId, grant.SessionId);

                var ttlOfClientSet = _database.KeyTimeToLiveAsync(setKeyforClient);
                var ttlOfSubjectSet = _database.KeyTimeToLiveAsync(setKeyforSubject);
                var ttlofSessionSet = _database.KeyTimeToLiveAsync(setKetforSession);

                await Task.WhenAll(ttlOfSubjectSet, ttlOfClientSet, ttlofSessionSet);

                var transaction = _database.CreateTransaction();
                
                await transaction.StringSetAsync(grantKey, data, expiresInRedis);
                await transaction.SetAddAsync(setKeyforSubject, grantKey);
                await transaction.SetAddAsync(setKeyforClient, grantKey);
                await transaction.SetAddAsync(setKeyforType, grantKey);

                if (!grant.SessionId.IsNullOrEmpty())
                {
                    await transaction.SetAddAsync(setKetforSession, grantKey);
                }                     

                if ((ttlOfSubjectSet.Result ?? TimeSpan.Zero) <= expiresIn)
                {
                    await transaction.KeyExpireAsync(setKeyforSubject, expiresIn);
                }
                    

                if ((ttlOfClientSet.Result ?? TimeSpan.Zero) <= expiresIn)
                {
                    await transaction.KeyExpireAsync(setKeyforClient, expiresIn);
                }
                    

                if (!grant.SessionId.IsNullOrEmpty() && (ttlofSessionSet.Result ?? TimeSpan.Zero) <= expiresIn)
                {
                    await transaction.KeyExpireAsync(setKetforSession, expiresIn);
                }

                await transaction.KeyExpireAsync(setKeyforType, expiresIn);

                await transaction.ExecuteAsync();
            }
            else
            {
                await _database.StringSetAsync(grantKey, data, expiresInRedis);
            }

            _logger.LogDebug("grant for subject {subjectId}, clientId {clientId}, grantType {grantType} and sessionId {session} persisted successfully", grant.SubjectId, grant.ClientId, grant.Type, grant.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "exception storing persisted grant to Redis database for subject {subjectId}, clientId {clientId}, grantType {grantType} and session {sessionId}", grant.SubjectId, grant.ClientId, grant.Type, grant.SessionId);
            throw;
        }
    }

    public virtual async Task<PersistedGrant> GetAsync(string key)
    {
        try
        {
            var data = await _database.StringGetAsync(GetKey(key));
            _logger.LogDebug("{key} found in database: {hasValue}", key, data.HasValue);

            return data.HasValue ? ConvertFromJson(data) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "exception retrieving grant for key {key}", key);
            throw;
        }
    }

    public virtual async Task<IEnumerable<PersistedGrant>> GetAllAsync(PersistedGrantFilter filter)
    {
        try
        {
            var setKey = GetSetKey(filter);
            var (grants, keysToDelete) = await GetGrants(setKey);

            if (keysToDelete.Any())
            {
                var keys = keysToDelete.ToArray();
                
                var transaction = _database.CreateTransaction();
                await transaction.SetRemoveAsync(GetSetKey(filter.SubjectId), keys);
                await transaction.SetRemoveAsync(GetSetKey(filter.SubjectId, filter.ClientId), keys);
                await transaction.SetRemoveAsync(GetSetKeyWithType(filter.SubjectId, filter.ClientId, filter.Type), keys);
                await transaction.SetRemoveAsync(GetSetKeyWithSession(filter.SubjectId, filter.ClientId, filter.SessionId), keys);
                await transaction.ExecuteAsync();
            }

            _logger.LogDebug("{grantsCount} persisted grants found for {subjectId}", grants.Count(), filter.SubjectId);

            return grants.Where(_ => _.HasValue).Select(_ => ConvertFromJson(_)).Where(_ => IsMatch(_, filter));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "exception while retrieving grants");
            throw;
        }
    }

    protected virtual async Task<(IEnumerable<RedisValue> grants, IEnumerable<RedisValue> keysToDelete)> GetGrants(string setKey)
    {
        var grantsKeys = await _database.SetMembersAsync(setKey);

        if (!grantsKeys.Any())
        {
            return (Enumerable.Empty<RedisValue>(), Enumerable.Empty<RedisValue>());
        }

        var grants = await _database.StringGetAsync(grantsKeys.Select(_ => (RedisKey)_.ToString()).ToArray());

        var keysToDelete = grantsKeys.Zip(grants, (key, value) => new KeyValuePair<RedisValue, RedisValue>(key, value))
            .Where(_ => !_.Value.HasValue).Select(_ => _.Key);

        return (grants, keysToDelete);
    }

    public virtual async Task RemoveAsync(string key)
    {
        try
        {
            var grant = await GetAsync(key);
            if (grant == null)
            {
                _logger.LogDebug("no {key} persisted grant found in database", key);
                return;
            }

            var grantKey = GetKey(key);

            _logger.LogDebug("removing {key} persisted grant from database", key);

            var transaction = _database.CreateTransaction();

            await transaction.KeyDeleteAsync(grantKey);
            await transaction.SetRemoveAsync(GetSetKey(grant.SubjectId), grantKey);
            await transaction.SetRemoveAsync(GetSetKey(grant.SubjectId, grant.ClientId), grantKey);
            await transaction.SetRemoveAsync(GetSetKeyWithType(grant.SubjectId, grant.ClientId, grant.Type), grantKey);
            await transaction.SetRemoveAsync(GetSetKeyWithSession(grant.SubjectId, grant.ClientId, grant.SessionId), grantKey);
            await transaction.ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "exception removing {key} persisted grant from database", key);
            throw;
        }
    }

    public virtual async Task RemoveAllAsync(PersistedGrantFilter filter)
    {
        try
        {
            filter.Validate();
            var setKey = GetSetKey(filter);
            var grants = await _database.SetMembersAsync(setKey);

            _logger.LogDebug("removing {grantKeysCount} persisted grants from database for subject {subjectId}, clientId {clientId}, grantType {type} and session {session}", grants.Count(), filter.SubjectId, filter.ClientId, filter.Type, filter.SessionId);
            
            if (!grants.Any())
            {
                return;
            }

            var transaction = _database.CreateTransaction();
            await transaction.KeyDeleteAsync(grants.Select(_ => (RedisKey)_.ToString()).Concat(new RedisKey[] { setKey }).ToArray());
            await transaction.SetRemoveAsync(GetSetKey(filter.SubjectId), grants);
            await transaction.SetRemoveAsync(GetSetKey(filter.SubjectId, filter.ClientId), grants);
            await transaction.SetRemoveAsync(GetSetKeyWithType(filter.SubjectId, filter.ClientId, filter.Type), grants);
            await transaction.SetRemoveAsync(GetSetKeyWithSession(filter.SubjectId, filter.ClientId, filter.SessionId), grants);
            await transaction.ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "exception removing persisted grants from database for subject {subjectId}, clientId {clientId}, grantType {type} and session {session}", filter.SubjectId, filter.ClientId, filter.Type, filter.SessionId);
            throw;
        }
    }

    protected virtual string GetSetKey(PersistedGrantFilter filter) =>
        (!filter.ClientId.IsNullOrEmpty(), !filter.SessionId.IsNullOrEmpty(), !filter.Type.IsNullOrEmpty()) switch
        {
            (true, true, false) => GetSetKeyWithSession(filter.SubjectId, filter.ClientId, filter.SessionId),
            (true, _, false) => GetSetKey(filter.SubjectId, filter.ClientId),
            (true, _, true) => GetSetKeyWithType(filter.SubjectId, filter.ClientId, filter.Type),
            _ => GetSetKey(filter.SubjectId),
        };

    protected bool IsMatch(PersistedGrant grant, PersistedGrantFilter filter)
    {
        return (filter.SubjectId.IsNullOrEmpty() ? true : grant.SubjectId == filter.SubjectId)
            && (filter.ClientId.IsNullOrEmpty() ? true : grant.ClientId == filter.ClientId)
            && (filter.SessionId.IsNullOrEmpty() ? true : grant.SessionId == filter.SessionId)
            && (filter.Type.IsNullOrEmpty() ? true : grant.Type == filter.Type);
    }

    #region Json
    protected static string ConvertToJson(PersistedGrant grant)
    {            
        return JsonSerializer.Serialize(grant);
    }

    protected static PersistedGrant ConvertFromJson(string data)
    {
        return JsonSerializer.Deserialize<PersistedGrant>(data);
    }
    #endregion
}