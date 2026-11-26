using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TicketBooking.Api.Infrastructure.Replication;
using TicketBooking.Api.Models;

namespace TicketBooking.Api.Infrastructure.Sharding;

/// <summary>Час відповіді шарду; Node — фізичний вузол, з якого прочитано (Primary шарду або його репліка).</summary>
public sealed record ShardTiming(string Shard, double Ms, int Rows, string? Node = null);

public sealed record EventSales(Guid EventId, long Orders, long Tickets, decimal Revenue);

public sealed record ShardDistribution(string Shard, long Orders, double DataShare, long WriteOps, double WriteShare);

public sealed record InDoubtTransaction(string Shard, string Gid, DateTime PreparedAt);

public enum TransferOutcome
{
    NotFound,
    SameShard,
    TwoPhaseCommitted,
    InDoubt
}

/// <summary>
/// Доступ до шардованих замовлень (ЛР7): кожна операція йде на шард, який обчислив <see cref="ShardRouter"/>,
/// або (для аналітики) паралельно на всі шарди зі злиттям результатів (Scatter-Gather).
/// </summary>
public sealed class ShardedOrderStore(ShardRouter router, ILogger<ShardedOrderStore> logger)
{
    private const string OrderColumns = "\"Id\", \"UserId\", \"EventId\", \"BookingId\", \"TicketCount\", \"Amount\", \"CreatedAt\"";

    private readonly DbContextOptions<OrdersDbContext>[] _options = router.Shards
        .Select(s => new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(s.ConnectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options)
        .ToArray();

    private readonly DbContextOptions<OrdersDbContext>?[] _replicaOptions = router.Shards
        .Select(s => string.IsNullOrWhiteSpace(s.ReplicaConnectionString)
            ? null
            : new DbContextOptionsBuilder<OrdersDbContext>()
                .UseNpgsql(s.ReplicaConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options)
        .ToArray();

    public ShardRouter Router => router;

    public OrdersDbContext Open(int shard) => new(_options[shard]);

    private string ConnectionString(int shard) => router.Shards[shard].ConnectionString;

    /// <summary>Створює схему на кожному шарді (advisory lock — кілька інстансів API стартують одночасно).</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < router.Shards.Count; i++)
        {
            try
            {
                await using var lockConnection = new NpgsqlConnection(ConnectionString(i));
                await lockConnection.OpenAsync(ct);
                await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(20260007)", lockConnection))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using var db = Open(i);
                await db.Database.EnsureCreatedAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Шард недоступний на старті — API працює, замовлення цього шарду повертатимуть 503
                logger.LogWarning("Shard {Shard} schema init failed: {Message}", router.Shards[i].Name, ex.Message);
            }
        }
    }

    // ---------- шард-локальні операції ----------

    public async Task<ShardRoute> CreateAsync(Order order, CancellationToken ct)
    {
        var route = router.Route(order.UserId);
        await using var db = Open(route.Index);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return route;
    }

    /// <summary>Ідемпотентне створення замовлення з підтвердженого бронювання (повтор нічого не дублює).</summary>
    public async Task<ShardRoute> RecordPurchaseAsync(Order order, CancellationToken ct)
    {
        var route = router.Route(order.UserId);
        await using var connection = new NpgsqlConnection(ConnectionString(route.Index));
        await connection.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO \"Orders\" ({OrderColumns}) VALUES (@id, @user, @event, @booking, @count, @amount, @at) " +
            "ON CONFLICT (\"BookingId\") DO NOTHING", connection);
        AddOrderParameters(cmd, order);
        await cmd.ExecuteNonQueryAsync(ct);
        return route;
    }

    /// <summary>«Мої замовлення»: запит лише до шарду користувача.</summary>
    public async Task<(List<Order> Orders, List<ShardTiming> Timings)> GetUserOrdersAsync(Guid userId, int limit, CancellationToken ct)
    {
        var route = router.Route(userId);
        var (orders, timing) = await QueryUserOrders(route.Index, userId, limit, ct);
        return (orders, [timing]);
    }

    /// <summary>
    /// Той самий запит без знання шарду (як було б при ключі шардингу, не пов'язаному з користувачем):
    /// опитуємо всі шарди паралельно і зливаємо результати — для порівняння вартості fan-out.
    /// </summary>
    public async Task<(List<Order> Orders, List<ShardTiming> Timings)> GetUserOrdersScatterAsync(Guid userId, int limit, CancellationToken ct)
    {
        var parts = await Task.WhenAll(Enumerable.Range(0, router.Shards.Count)
            .Select(i => QueryUserOrders(i, userId, limit, ct)));
        var merged = parts.SelectMany(p => p.Orders).OrderByDescending(o => o.CreatedAt).Take(limit).ToList();
        return (merged, parts.Select(p => p.Timing).ToList());
    }

    private async Task<(List<Order> Orders, ShardTiming Timing)> QueryUserOrders(int shard, Guid userId, int limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var db = Open(shard);
        var orders = await db.Orders
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        return (orders, new ShardTiming(router.Shards[shard].Name, sw.Elapsed.TotalMilliseconds, orders.Count));
    }

    // ---------- cross-shard (Scatter-Gather) ----------

    /// <summary>
    /// Продажі по подіях: кожен шард рахує свою частину (GROUP BY локально), координатор сумує.
    /// Аналітика допускає eventual consistency, тому за замовчуванням читає з реплік шардів: важкий GROUP BY
    /// не конкурує із записом замовлень за CPU Primary. Репліка недоступна або конфлікт із програванням WAL —
    /// цей шард перечитується з Primary (Primary Fallback, як у ЛР6). strong = true — одразу Primary.
    /// </summary>
    public async Task<(List<EventSales> Sales, List<ShardTiming> Timings)> EventSalesAsync(
        Guid? eventId, int top, CancellationToken ct, bool strong = false)
    {
        var useReplicas = !strong && router.AnalyticsReads == AnalyticsReadTarget.Replica;
        var parts = await Task.WhenAll(Enumerable.Range(0, router.Shards.Count).Select(async shard =>
        {
            var sw = Stopwatch.StartNew();
            var node = router.Shards[shard];
            if (useReplicas && !string.IsNullOrWhiteSpace(node.ReplicaConnectionString))
            {
                try
                {
                    var fromReplica = await QueryEventSales(_replicaOptions[shard]!, eventId, ct);
                    return (fromReplica, new ShardTiming(node.Name, sw.Elapsed.TotalMilliseconds, fromReplica.Count, $"{node.Name}-replica"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                           && (IsReplicaUnreachable(ex) || ReplicaHealth.IsRecoveryConflict(ex)))
                {
                    logger.LogWarning("Replica of {Shard} unavailable for analytics, falling back to primary: {Message}", node.Name, ex.Message);
                    var fallback = await QueryEventSales(_options[shard], eventId, ct);
                    return (fallback, new ShardTiming(node.Name, sw.Elapsed.TotalMilliseconds, fallback.Count, $"{node.Name} (replica-down)"));
                }
            }

            var rows = await QueryEventSales(_options[shard], eventId, ct);
            return (rows, new ShardTiming(node.Name, sw.Elapsed.TotalMilliseconds, rows.Count, node.Name));
        }));

        var merged = parts.SelectMany(p => p.Item1)
            .GroupBy(r => r.EventId)
            .Select(g => new EventSales(g.Key, g.Sum(r => r.Orders), g.Sum(r => r.Tickets), g.Sum(r => r.Revenue)))
            .OrderByDescending(r => r.Revenue)
            .Take(top)
            .ToList();
        return (merged, parts.Select(p => p.Item2).ToList());
    }

    /// <summary>
    /// Репліка саме недоступна (вузол не відповідає / не приймає з'єднань), а не перевантажена.
    /// Вичерпаний пул або таймаут під навантаженням — НЕ привід іти на Primary: важкий GROUP BY переллється
    /// на вузол, що приймає записи, і перевантаження репліки зупинить запис (виміряно в README 12.9).
    /// Такий виняток іде далі → 503 (load shedding аналітики), Primary лишається для записів.
    /// </summary>
    private static bool IsReplicaUnreachable(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case TimeoutException:
                    return false;
                case System.Net.Sockets.SocketException:
                    return true;
                case PostgresException pg when pg.SqlState.StartsWith("08")
                                               || pg.SqlState is PostgresErrorCodes.CannotConnectNow
                                                   or PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown:
                    return true;
            }
        }
        return false;
    }

    private static async Task<List<EventSales>> QueryEventSales(DbContextOptions<OrdersDbContext> options, Guid? eventId, CancellationToken ct)
    {
        await using var db = new OrdersDbContext(options);
        var query = db.Orders.AsQueryable();
        if (eventId is not null)
        {
            query = query.Where(o => o.EventId == eventId);
        }
        return await query
            .GroupBy(o => o.EventId)
            .Select(g => new EventSales(g.Key, g.LongCount(), g.Sum(o => (long)o.TicketCount), g.Sum(o => o.Amount)))
            .ToListAsync(ct);
    }

    // ---------- cross-shard write: передача замовлення іншому користувачу ----------

    /// <summary>
    /// Передача замовлення: рядок переїжджає на шард нового власника. Якщо обидва користувачі на одному шарді —
    /// звичайна локальна ACID-транзакція. Інакше — двофазна фіксація (2PC) через PREPARE TRANSACTION PostgreSQL:
    /// фаза 1 — обидва шарди виконують зміни і «голосують» PREPARE; фаза 2 — COMMIT PREPARED на обох.
    /// simulateCoordinatorCrash = true зупиняє координатора між фазами (транзакції лишаються in-doubt).
    /// </summary>
    public async Task<(TransferOutcome Outcome, ShardRoute From, ShardRoute To, string? Gid)> TransferAsync(
        Guid orderId, Guid fromUserId, Guid toUserId, bool simulateCoordinatorCrash, CancellationToken ct)
    {
        var from = router.Route(fromUserId);
        var to = router.Route(toUserId);

        if (from.Index == to.Index)
        {
            await using var connection = new NpgsqlConnection(ConnectionString(from.Index));
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "UPDATE \"Orders\" SET \"UserId\" = @to WHERE \"Id\" = @id AND \"UserId\" = @from", connection);
            cmd.Parameters.AddWithValue("to", toUserId);
            cmd.Parameters.AddWithValue("id", orderId);
            cmd.Parameters.AddWithValue("from", fromUserId);
            var updated = await cmd.ExecuteNonQueryAsync(ct);
            return (updated == 0 ? TransferOutcome.NotFound : TransferOutcome.SameShard, from, to, null);
        }

        // Глобальний ідентифікатор транзакції + назва учасника: gid унікальний у межах сервера PostgreSQL,
        // а за спільним префіксом координатор після збою знаходить усіх учасників однієї транзакції
        var gid = $"tb-transfer-{orderId:N}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var sourceGid = $"{gid}:{from.Name}";
        var targetGid = $"{gid}:{to.Name}";
        await using var source = new NpgsqlConnection(ConnectionString(from.Index));
        await using var target = new NpgsqlConnection(ConnectionString(to.Index));
        await source.OpenAsync(ct);
        await target.OpenAsync(ct);

        var sourcePrepared = false;
        var targetPrepared = false;
        try
        {
            await Exec(source, "BEGIN", ct);
            await Exec(target, "BEGIN", ct);

            Order? order;
            await using (var select = new NpgsqlCommand(
                $"SELECT {OrderColumns} FROM \"Orders\" WHERE \"Id\" = @id AND \"UserId\" = @from FOR UPDATE", source))
            {
                select.Parameters.AddWithValue("id", orderId);
                select.Parameters.AddWithValue("from", fromUserId);
                await using var reader = await select.ExecuteReaderAsync(ct);
                order = await reader.ReadAsync(ct) ? ReadOrder(reader) : null;
            }

            if (order is null)
            {
                await Exec(source, "ROLLBACK", ct);
                await Exec(target, "ROLLBACK", ct);
                return (TransferOutcome.NotFound, from, to, null);
            }

            order.UserId = toUserId;
            await using (var insert = new NpgsqlCommand(
                $"INSERT INTO \"Orders\" ({OrderColumns}) VALUES (@id, @user, @event, @booking, @count, @amount, @at)", target))
            {
                AddOrderParameters(insert, order);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await using (var delete = new NpgsqlCommand("DELETE FROM \"Orders\" WHERE \"Id\" = @id", source))
            {
                delete.Parameters.AddWithValue("id", orderId);
                await delete.ExecuteNonQueryAsync(ct);
            }

            // Фаза 1 (prepare): після успішного PREPARE шард гарантує, що зможе закомітити навіть після рестарту
            await Exec(source, $"PREPARE TRANSACTION '{sourceGid}'", ct);
            sourcePrepared = true;
            await Exec(target, $"PREPARE TRANSACTION '{targetGid}'", ct);
            targetPrepared = true;

            if (simulateCoordinatorCrash)
            {
                logger.LogWarning("2PC {Gid}: координатор «впав» після PREPARE — транзакції in-doubt на {From} і {To}",
                    gid, from.Name, to.Name);
                return (TransferOutcome.InDoubt, from, to, gid);
            }

            // Фаза 2 (commit): рішення прийнято, комітимо всіх учасників
            await Exec(source, $"COMMIT PREPARED '{sourceGid}'", ct);
            await Exec(target, $"COMMIT PREPARED '{targetGid}'", ct);
            return (TransferOutcome.TwoPhaseCommitted, from, to, gid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Хтось не зміг підготуватися → відкат усіх (і вже підготовлених теж)
            logger.LogWarning("2PC {Gid} aborted: {Message}", gid, ex.Message);
            await SafeRollback(source, sourcePrepared, sourceGid);
            await SafeRollback(target, targetPrepared, targetGid);
            throw;
        }
    }

    /// <summary>Транзакції, що «зависли» між фазами 2PC (pg_prepared_xacts), на всіх шардах.</summary>
    public async Task<List<InDoubtTransaction>> ListInDoubtAsync(CancellationToken ct)
    {
        var result = new List<InDoubtTransaction>();
        for (var i = 0; i < router.Shards.Count; i++)
        {
            await using var connection = new NpgsqlConnection(ConnectionString(i));
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT gid, prepared FROM pg_prepared_xacts WHERE database = current_database() AND gid LIKE 'tb-transfer-%'", connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new InDoubtTransaction(router.Shards[i].Name, reader.GetString(0), reader.GetDateTime(1)));
            }
        }
        return result;
    }

    /// <summary>
    /// Відновлення після збою координатора: якщо транзакцію підготували ВСІ учасники (обидва проголосували «так»),
    /// її можна закомітити; якщо лише частина — відкотити (presumed abort).
    /// </summary>
    public async Task<List<(string Gid, string Decision)>> ResolveInDoubtAsync(CancellationToken ct)
    {
        var pending = await ListInDoubtAsync(ct);
        var decisions = new List<(string, string)>();
        foreach (var group in pending.GroupBy(t => t.Gid[..t.Gid.LastIndexOf(':')]))
        {
            var commit = group.Count() == 2;
            foreach (var tx in group)
            {
                var shard = router.Shards.ToList().FindIndex(s => s.Name == tx.Shard);
                await using var connection = new NpgsqlConnection(ConnectionString(shard));
                await connection.OpenAsync(ct);
                await Exec(connection, $"{(commit ? "COMMIT" : "ROLLBACK")} PREPARED '{tx.Gid}'", ct);
            }
            decisions.Add((group.Key, commit ? "commit" : "rollback"));
        }
        return decisions;
    }

    // ---------- лабораторні операції: генерація, розподіл, скидання ----------

    /// <summary>Масова вставка замовлень (COPY BINARY) з маршрутизацією кожного рядка через <see cref="ShardRouter"/>.</summary>
    public async Task<int[]> BulkInsertAsync(IEnumerable<Order> orders, CancellationToken ct)
    {
        var byShard = orders.GroupBy(o => router.Route(o.UserId).Index).ToDictionary(g => g.Key, g => g.ToList());
        var counts = new int[router.Shards.Count];
        await Task.WhenAll(byShard.Select(async pair =>
        {
            await using var connection = new NpgsqlConnection(ConnectionString(pair.Key));
            await connection.OpenAsync(ct);
            var writer = await connection.BeginBinaryImportAsync(
                $"COPY \"Orders\" ({OrderColumns}) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var o in pair.Value)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(o.Id, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(o.UserId, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(o.EventId, NpgsqlDbType.Uuid, ct);
                if (o.BookingId is null)
                {
                    await writer.WriteNullAsync(ct);
                }
                else
                {
                    await writer.WriteAsync(o.BookingId.Value, NpgsqlDbType.Uuid, ct);
                }
                await writer.WriteAsync(o.TicketCount, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(o.Amount, NpgsqlDbType.Numeric, ct);
                await writer.WriteAsync(o.CreatedAt, NpgsqlDbType.TimestampTz, ct);
            }
            await writer.CompleteAsync(ct);
            await writer.DisposeAsync();
            // З'єднання повертається в пул і лишається відкритим: PostgreSQL 15+ скидає лічильники pg_stat
            // з idle-бекенду із затримкою до 10 с, і Data Distribution одразу після генерації показала б 0 записів.
            // pg_stat_force_next_flush — скинути їх, щойно бекенд стане idle.
            await Exec(connection, "SELECT pg_stat_force_next_flush()", ct);
            counts[pair.Key] = pair.Value.Count;
        }));

        await Task.WhenAll(Enumerable.Range(0, router.Shards.Count).Select(i => Exec(i, "ANALYZE \"Orders\"", ct)));
        return counts;
    }

    /// <summary>Фактичний розподіл: записів на шарді і частка операцій запису (pg_stat_user_tables з моменту скидання).</summary>
    public async Task<List<ShardDistribution>> DistributionAsync(CancellationToken ct)
    {
        var raw = await Task.WhenAll(Enumerable.Range(0, router.Shards.Count).Select(async i =>
        {
            await using var connection = new NpgsqlConnection(ConnectionString(i));
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM "Orders"),
                       COALESCE((SELECT n_tup_ins + n_tup_upd + n_tup_del FROM pg_stat_user_tables WHERE relname = 'Orders'), 0)
                """, connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return (Name: router.Shards[i].Name, Rows: reader.GetInt64(0), Writes: reader.GetInt64(1));
        }));

        var totalRows = Math.Max(1, raw.Sum(r => r.Rows));
        var totalWrites = Math.Max(1, raw.Sum(r => r.Writes));
        return raw.Select(r => new ShardDistribution(r.Name, r.Rows, 100.0 * r.Rows / totalRows,
            r.Writes, 100.0 * r.Writes / totalWrites)).ToList();
    }

    /// <summary>Очищає замовлення на всіх шардах і скидає лічильники операцій (для чистого заміру розподілу).</summary>
    public Task ResetAsync(CancellationToken ct) =>
        Task.WhenAll(Enumerable.Range(0, router.Shards.Count).Select(async i =>
        {
            await Exec(i, "TRUNCATE \"Orders\"", ct);
            await Exec(i, "SELECT pg_stat_reset()", ct);
        }));

    // ---------- helpers ----------

    private async Task Exec(int shard, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(shard));
        await connection.OpenAsync(ct);
        await Exec(connection, sql, ct);
    }

    private static async Task Exec(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SafeRollback(NpgsqlConnection connection, bool prepared, string gid)
    {
        try
        {
            await Exec(connection, prepared ? $"ROLLBACK PREPARED '{gid}'" : "ROLLBACK", CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("2PC {Gid}: rollback failed ({Message}); транзакцію відкотить відновлення in-doubt", gid, ex.Message);
        }
    }

    private static void AddOrderParameters(NpgsqlCommand cmd, Order o)
    {
        cmd.Parameters.AddWithValue("id", o.Id);
        cmd.Parameters.AddWithValue("user", o.UserId);
        cmd.Parameters.AddWithValue("event", o.EventId);
        cmd.Parameters.Add(new NpgsqlParameter("booking", NpgsqlDbType.Uuid) { Value = (object?)o.BookingId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("count", o.TicketCount);
        cmd.Parameters.AddWithValue("amount", o.Amount);
        cmd.Parameters.AddWithValue("at", o.CreatedAt);
    }

    private static Order ReadOrder(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        UserId = r.GetGuid(1),
        EventId = r.GetGuid(2),
        BookingId = r.IsDBNull(3) ? null : r.GetGuid(3),
        TicketCount = r.GetInt32(4),
        Amount = r.GetDecimal(5),
        CreatedAt = r.GetDateTime(6)
    };
}
