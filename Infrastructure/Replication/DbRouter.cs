using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using TicketBooking.Api.Data;

namespace TicketBooking.Api.Infrastructure.Replication;

/// <summary>
/// Read/Write Separation на рівні Data Access Layer (ЛР6, Multi-Pool Manager).
///
/// - Запис: лише Primary (контролери працюють з <see cref="Primary"/>, як і раніше).
/// - Читання: <see cref="ReadAsync{T}"/> обирає вузол за вимогою узгодженості сценарію:
///   Eventual → репліка; ReadYourWrites → репліка, якщо вона вже програла WAL до LSN останнього запису
///   цієї сесії (користувача/події), інакше Primary; Strong → Primary.
/// - Primary Fallback: репліка недоступна → читання переходить на Primary, репліка виключається на кілька секунд.
///
/// Обраний вузол і причина віддаються в заголовках X-Db-Node / X-Db-Route (для демонстрації та налагодження).
/// </summary>
public sealed class DbRouter(
    AppDbContext primary,
    ReplicaDbOptions replicaOptions,
    ReplicaHealth replicaHealth,
    IDistributedCache cache,
    IOptions<ReplicationSettings> options,
    IHttpContextAccessor httpContextAccessor,
    ILogger<DbRouter> logger)
{
    public const string NodeHeader = "X-Db-Node";
    public const string RouteHeader = "X-Db-Route";

    /// <summary>Лише для демонстрації Stale Read: клієнт може явно попросити рівень узгодженості.</summary>
    public const string ConsistencyOverrideHeader = "X-Read-Consistency";

    private readonly ReplicationSettings _settings = options.Value;

    /// <summary>Контекст Primary: усі мутації та транзакції.</summary>
    public AppDbContext Primary => primary;

    private bool ReplicaConfigured => _settings.Enabled && replicaOptions.Options is not null;

    public static string UserScope(Guid userId) => $"user:{userId:N}";
    public static string EventScope(Guid eventId) => $"event:{eventId:N}";

    /// <summary>Виконує читання на вузлі, який відповідає вимозі узгодженості.</summary>
    /// <param name="scope">Сесія для Read-Your-Writes (<see cref="UserScope"/> / <see cref="EventScope"/>).</param>
    public async Task<T> ReadAsync<T>(
        ReadConsistency consistency,
        string? scope,
        Func<AppDbContext, CancellationToken, Task<T>> query,
        CancellationToken ct)
    {
        consistency = ApplyDemoOverride(consistency);

        if (!ReplicaConfigured)
        {
            return await OnPrimary(query, _settings.Enabled ? "no-replica" : "single-primary", ct);
        }

        if (consistency == ReadConsistency.Strong)
        {
            return await OnPrimary(query, "strong", ct);
        }

        if (!replicaHealth.IsAvailable)
        {
            return await OnPrimary(query, "replica-down", ct);
        }

        ulong? fence = null;
        if (consistency == ReadConsistency.ReadYourWrites && scope is not null)
        {
            var (known, value) = await GetWriteFenceAsync(scope, ct);
            if (!known)
            {
                // Невідомо, чи писала сесія нещодавно (Redis недоступний) — безпечніше прочитати з Primary
                return await OnPrimary(query, "session-unknown", ct);
            }
            fence = value;
        }

        await using var replica = new AppDbContext(replicaOptions.Options!);
        try
        {
            var route = "eventual";
            if (fence is not null)
            {
                var replayed = await ReplayLsnAsync(replica, ct);
                if (replayed is not null && replayed < fence)
                {
                    logger.LogInformation("Replica behind for {Scope}: replay {Replay} < write {Fence} ({Bytes} B)",
                        scope, Lsn.Format(replayed.Value), Lsn.Format(fence.Value), fence - replayed);
                    return await OnPrimary(query, "replica-behind", ct);
                }
                route = "session-caught-up";
            }
            else if (consistency == ReadConsistency.ReadYourWrites)
            {
                route = "session-no-writes";
            }

            var result = await query(replica, ct);
            Mark("replica", route);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ReplicaHealth.IsConnectionFailure(ex))
        {
            replicaHealth.MarkDown(ex);
            return await OnPrimary(query, "replica-down", ct);
        }
        catch (Exception ex) when (ReplicaHealth.IsRecoveryConflict(ex))
        {
            logger.LogInformation("Recovery conflict on replica, retry on primary: {Message}", ex.Message);
            return await OnPrimary(query, "replica-conflict", ct);
        }
    }

    /// <summary>
    /// Фіксує «межу запису» після коміту: поточний LSN Primary зберігається в Redis для кожної сесії (scope)
    /// на <see cref="ReplicationSettings.ReadYourWritesWindowSeconds"/>. Викликати ПІСЛЯ коміту і ДО інвалідації кешу,
    /// щоб повторне заповнення кешу не прочитало з репліки стан до запису.
    /// </summary>
    public async Task RecordWriteAsync(IEnumerable<string> scopes, CancellationToken ct = default)
    {
        if (!ReplicaConfigured)
        {
            return;
        }

        try
        {
            var lsnText = await primary.Database
                .SqlQueryRaw<string>("SELECT pg_current_wal_lsn()::text AS \"Value\"")
                .SingleAsync(ct);

            var entry = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_settings.ReadYourWritesWindowSeconds)
            };
            await Task.WhenAll(scopes.Distinct().Select(s => cache.SetStringAsync(FenceKey(s), lsnText, entry, ct)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Запис уже закомічено; без межі наступне читання сесії може бути застарілим, але не помилковим
            logger.LogWarning("Failed to record write fence for {Scopes}: {Message}", string.Join(", ", scopes), ex.Message);
        }
    }

    private async Task<T> OnPrimary<T>(Func<AppDbContext, CancellationToken, Task<T>> query, string route, CancellationToken ct)
    {
        var result = await query(primary, ct);
        Mark("primary", route);
        return result;
    }

    private async Task<(bool Known, ulong? Value)> GetWriteFenceAsync(string scope, CancellationToken ct)
    {
        try
        {
            var text = await cache.GetStringAsync(FenceKey(scope), ct);
            return (true, Lsn.TryParse(text, out var value) ? value : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Failed to read write fence for {Scope}: {Message}", scope, ex.Message);
            return (false, null);
        }
    }

    /// <summary>До якої позиції WAL репліка вже програла журнал (NULL, якщо вузол не в режимі recovery).</summary>
    private static async Task<ulong?> ReplayLsnAsync(AppDbContext replica, CancellationToken ct)
    {
        var text = await replica.Database
            .SqlQueryRaw<string>("SELECT pg_last_wal_replay_lsn()::text AS \"Value\"")
            .SingleOrDefaultAsync(ct);
        return Lsn.TryParse(text, out var value) ? value : null;
    }

    private ReadConsistency ApplyDemoOverride(ReadConsistency consistency)
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers[ConsistencyOverrideHeader].ToString();
        return header?.ToLowerInvariant() switch
        {
            "eventual" => ReadConsistency.Eventual,
            "session" => ReadConsistency.ReadYourWrites,
            "strong" => ReadConsistency.Strong,
            _ => consistency
        };
    }

    private void Mark(string node, string route)
    {
        var response = httpContextAccessor.HttpContext?.Response;
        if (response is null || response.HasStarted)
        {
            return;
        }
        response.Headers[NodeHeader] = node;
        response.Headers[RouteHeader] = route;
    }

    private static string FenceKey(string scope) => $"tb:v1:replication:write-fence:{scope}:lsn";
}
