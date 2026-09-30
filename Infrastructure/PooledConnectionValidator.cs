using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace TicketBooking.Api.Infrastructure;

/// <summary>
/// Перевіряє з'єднання, взяте з пулу Npgsql, коротким пінгом перед використанням.
/// Після рестарту PostgreSQL усі з'єднання в пулі «мертві»; без перевірки перший запит падає з 500/503.
/// Якщо пінг не пройшов — пул очищується й відкривається нове з'єднання (прозоро для контролерів).
/// </summary>
public sealed class PooledConnectionValidator : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is not NpgsqlConnection npgsql)
        {
            return;
        }

        try
        {
            await Ping(npgsql, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or System.Net.Sockets.SocketException)
        {
            NpgsqlConnection.ClearPool(npgsql);
            await npgsql.CloseAsync();
            await npgsql.OpenAsync(cancellationToken);
        }
    }

    private static async Task Ping(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(ct);
    }
}
