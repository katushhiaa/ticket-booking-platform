using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace TicketBooking.IntegrationTests;

/// <summary>
/// Піднімає справжні PostgreSQL і Redis у Docker (Testcontainers) і запускає API в пам'яті через WebApplicationFactory.
/// Контейнери спільні на весь набір тестів; між тестами дані й кеш скидаються методом <see cref="ResetAsync"/>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        // 2PC між шардами (ЛР7) потребує PREPARE TRANSACTION
        .WithCommand("-c", "max_prepared_transactions=10")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    /// <summary>Рядок підключення до тестового PostgreSQL (у тестах ЛР6 ним же імітується «репліка»).</summary>
    public string PostgresConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        // Два логічні шарди замовлень (ЛР7) — окремі бази в тому самому PostgreSQL
        await ExecuteSqlAsync("CREATE DATABASE shard_a");
        await ExecuteSqlAsync("CREATE DATABASE shard_b");
    }

    public string ShardConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Без SQL-логів EF у виводі тестів
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        // Program.cs читає рядок підключення при старті, тому підміняємо його через конфігурацію
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString(),
                ["ConnectionStrings:RedisConnection"] = _redis.GetConnectionString(),
                // У тестах повторне видалення ключів (double delete) не потрібне й заважало б перевіркам
                ["Cache:InvalidationRetryDelayMs"] = "0",
                ["Sharding:Shards:0:Name"] = "shard-a",
                ["Sharding:Shards:0:ConnectionString"] = ShardConnectionString("shard_a"),
                ["Sharding:Shards:1:Name"] = "shard-b",
                ["Sharding:Shards:1:ConnectionString"] = ShardConnectionString("shard_b")
            }));
    }

    /// <summary>
    /// Повертає дані до стану після сідера: усі квитки Available, жодних бронювань, лише seed-користувач.
    /// SQL оминає API (а отже й інвалідацію), тому кеш очищуємо повністю.
    /// </summary>
    public async Task ResetAsync()
    {
        await ExecuteSqlAsync("""
            UPDATE "Tickets" SET "Status" = 'Available', "BookingId" = NULL, "Barcode" = NULL, "Version" = 0;
            DELETE FROM "Bookings";
            DELETE FROM "Users" WHERE "Email" <> 'katerina@example.com';
            """);
        await FlushCacheAsync();
        await ExecuteSqlAsync("TRUNCATE \"Orders\"", ShardConnectionString("shard_a"));
        await ExecuteSqlAsync("TRUNCATE \"Orders\"", ShardConnectionString("shard_b"));
    }

    public async Task FlushCacheAsync()
    {
        var result = await _redis.ExecAsync(["redis-cli", "FLUSHALL"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"redis-cli FLUSHALL failed: {result.Stderr}");
        }
    }

    public async Task ExecuteSqlAsync(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? _postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
