using Microsoft.EntityFrameworkCore;
using TicketBooking.Api.Data;

namespace TicketBooking.Api.Infrastructure.Replication;

/// <summary>Опції EF Core для підключення до Read Replica (singleton). Options = null, якщо репліку не налаштовано.</summary>
public sealed class ReplicaDbOptions(DbContextOptions<AppDbContext>? options)
{
    public DbContextOptions<AppDbContext>? Options { get; } = options;
}
