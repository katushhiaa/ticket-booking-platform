using Npgsql;

namespace TicketBooking.Api.Infrastructure.Replication;

/// <summary>
/// Стан репліки на рівні інстансу API (singleton). Після помилки з'єднання репліка на
/// <see cref="ReplicationSettings.ReplicaFailoverSeconds"/> виключається з маршрутизації, і всі читання йдуть на Primary,
/// щоб кожен запит не чекав таймауту підключення до мертвого вузла (circuit breaker).
/// </summary>
public sealed class ReplicaHealth(Microsoft.Extensions.Options.IOptions<ReplicationSettings> options, ILogger<ReplicaHealth> logger)
{
    private long _downUntilTicks;

    public bool IsAvailable => DateTime.UtcNow.Ticks >= Interlocked.Read(ref _downUntilTicks);

    public void MarkDown(Exception ex)
    {
        var until = DateTime.UtcNow.AddSeconds(options.Value.ReplicaFailoverSeconds);
        Interlocked.Exchange(ref _downUntilTicks, until.Ticks);
        logger.LogWarning("Replica unavailable ({Error}: {Message}); читання переведено на Primary до {Until:O}",
            ex.GetType().Name, ex.Message, until);
    }

    /// <summary>Помилка означає, що до репліки не можна достукатися (а не що запит некоректний).</summary>
    public static bool IsConnectionFailure(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState.StartsWith("08") // connection_exception
                                || pg.SqlState is PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.AdminShutdown
                                    or PostgresErrorCodes.CrashShutdown,
        NpgsqlException => true, // мережева помилка / таймаут підключення (не помилка SQL)
        System.Net.Sockets.SocketException or TimeoutException or IOException => true,
        _ => ex.InnerException is not null && IsConnectionFailure(ex.InnerException)
    };

    /// <summary>
    /// Запит на standby скасовано через конфлікт із програванням WAL (canceling statement due to conflict with recovery).
    /// Репліка жива, просто цей запит варто повторити на Primary.
    /// </summary>
    public static bool IsRecoveryConflict(Exception ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or "40P01" };
}
