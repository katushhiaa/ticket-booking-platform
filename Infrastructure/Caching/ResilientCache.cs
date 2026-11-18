using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace TicketBooking.Api.Infrastructure.Caching;

/// <summary>Джерело відповіді: з кешу, з БД із записом у кеш, або з БД в обхід кешу (кеш вимкнено чи недоступний).</summary>
public enum CacheResult
{
    Hit,
    Miss,
    Bypass
}

/// <summary>
/// Cache-Aside поверх Redis (IDistributedCache) з graceful degradation:
/// будь-яка помилка Redis не валить запит — він обслуговується з БД (BYPASS). Втрата з'єднання з Redis
/// додатково переводить інстанс у режим прямої роботи з БД на <see cref="CacheSettings.CircuitBreakSeconds"/>
/// (circuit breaker), щоб кожен запит не чекав на підключення. Таймаут команди circuit breaker не відкриває.
/// </summary>
public sealed class ResilientCache(
    IDistributedCache cache,
    IOptions<CacheSettings> options,
    ILogger<ResilientCache> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CacheSettings _settings = options.Value;

    // Операційний стан конкретного інстансу (не бізнес-дані): до якого моменту Redis вважаємо недоступним
    private long _circuitOpenUntilTicks;

    public bool Enabled => _settings.Enabled;

    public TimeSpan CatalogTtl => TimeSpan.FromSeconds(_settings.CatalogTtlSeconds);
    public TimeSpan TicketsTtl => TimeSpan.FromSeconds(_settings.TicketsTtlSeconds);

    /// <summary>
    /// Cache-Aside: 1) пошук за ключем; 2) HIT — повертаємо збережене значення без звернення до БД;
    /// 3) MISS — читаємо з БД (factory), зберігаємо з TTL і повертаємо.
    /// factory повертає null, коли сутності немає: такий результат не кешується (Payload = null).
    /// </summary>
    public async Task<(CachedPayload? Payload, CacheResult Result)> GetOrCreateAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken ct) where T : class
    {
        var cacheUsable = _settings.Enabled && !IsCircuitOpen;

        if (cacheUsable)
        {
            try
            {
                var bytes = await cache.GetAsync(key, ct);
                if (bytes is not null)
                {
                    logger.LogInformation("Cache HIT {Key}", key);
                    return (CachedPayload.FromStored(bytes), CacheResult.Hit);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnRedisError(ex, "GET", key);
                cacheUsable = false;
            }
        }

        var value = await factory(ct);
        var payload = value is null ? null : CachedPayload.FromJson(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

        if (!cacheUsable)
        {
            if (_settings.Enabled)
            {
                logger.LogWarning("Cache BYPASS {Key}: Redis недоступний, відповідь з БД", key);
            }
            return (payload, CacheResult.Bypass);
        }

        if (payload is not null)
        {
            try
            {
                await cache.SetAsync(key, payload.Stored,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = WithJitter(ttl) }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnRedisError(ex, "SET", key);
                return (payload, CacheResult.Bypass);
            }
        }

        logger.LogInformation("Cache MISS {Key}", key);
        return (payload, CacheResult.Miss);
    }

    /// <summary>
    /// Явна інвалідація після мутації (викликати ПІСЛЯ коміту транзакції).
    /// Додатково ключі видаляються вдруге через <see cref="CacheSettings.InvalidationRetryDelayMs"/> (double delete):
    /// паралельний читач міг прочитати БД до коміту й записати в кеш уже застаріле значення.
    /// </summary>
    public async Task InvalidateAsync(IReadOnlyCollection<string> keys, string reason)
    {
        if (!_settings.Enabled || keys.Count == 0)
        {
            return;
        }

        await RemoveAsync(keys, reason, "immediate");

        if (_settings.InvalidationRetryDelayMs > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(_settings.InvalidationRetryDelayMs);
                await RemoveAsync(keys, reason, "delayed");
            });
        }
    }

    private async Task RemoveAsync(IReadOnlyCollection<string> keys, string reason, string phase)
    {
        if (IsCircuitOpen)
        {
            logger.LogWarning("Cache INVALIDATE skipped ({Phase}, {Reason}): Redis недоступний, ключі протухнуть за TTL", phase, reason);
            return;
        }

        try
        {
            await Task.WhenAll(keys.Select(k => cache.RemoveAsync(k)));
            logger.LogInformation("Cache INVALIDATE ({Phase}, {Reason}): {Keys}", phase, reason, string.Join(", ", keys));
        }
        catch (Exception ex)
        {
            OnRedisError(ex, "DEL", string.Join(", ", keys));
        }
    }

    private bool IsCircuitOpen => DateTime.UtcNow.Ticks < Interlocked.Read(ref _circuitOpenUntilTicks);

    private void OnRedisError(Exception ex, string operation, string key)
    {
        // Таймаут означає повільну відповідь (найчастіше — перевантажений сам інстанс API), а не мертвий Redis:
        // цей запит обслуговуємо з БД, але кеш не вимикаємо, інакше весь трафік інстансу піде в БД
        // і перевантаження стане самопідтримним (metastable failure, ЛР5)
        if (ex is RedisTimeoutException)
        {
            logger.LogWarning("Redis {Operation} {Key} timeout, запит обслуговано з БД: {Message}", operation, key, ex.Message);
            return;
        }

        var until = DateTime.UtcNow.AddSeconds(_settings.CircuitBreakSeconds);
        Interlocked.Exchange(ref _circuitOpenUntilTicks, until.Ticks);
        logger.LogWarning("Redis {Operation} {Key} failed ({Error}: {Message}); кеш вимкнено до {Until:O}, працюємо напряму з БД",
            operation, key, ex.GetType().Name, ex.Message, until);
    }

    private TimeSpan WithJitter(TimeSpan ttl)
    {
        var jitter = _settings.TtlJitterPercent / 100.0;
        var factor = 1 + (Random.Shared.NextDouble() * 2 - 1) * jitter;
        return TimeSpan.FromMilliseconds(ttl.TotalMilliseconds * factor);
    }
}
