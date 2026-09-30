using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace TicketBooking.IntegrationTests;

/// <summary>
/// Піднімає справжній PostgreSQL у Docker (Testcontainers) і запускає API в пам'яті через WebApplicationFactory.
/// Контейнер один на весь набір тестів; між тестами дані скидаються методом <see cref="ResetAsync"/>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
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
                ["ConnectionStrings:DefaultConnection"] = _postgres.GetConnectionString()
            }));
    }

    /// <summary>Повертає дані до стану після сідера: усі квитки Available, жодних бронювань, лише seed-користувач.</summary>
    public async Task ResetAsync()
    {
        await ExecuteSqlAsync("""
            UPDATE "Tickets" SET "Status" = 'Available', "BookingId" = NULL, "Barcode" = NULL, "Version" = 0;
            DELETE FROM "Bookings";
            DELETE FROM "Users" WHERE "Email" <> 'katerina@example.com';
            """);
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
