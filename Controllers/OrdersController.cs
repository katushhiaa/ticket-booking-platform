using Microsoft.AspNetCore.Mvc;
using TicketBooking.Api.Infrastructure.Sharding;
using TicketBooking.Api.Models;

namespace TicketBooking.Api.Controllers;

public record CreateOrderRequest(Guid UserId, Guid EventId, int TicketCount, decimal Amount);

public record TransferOrderRequest(Guid FromUserId, Guid ToUserId, bool SimulateCoordinatorCrash = false);

public record GenerateOrdersRequest(
    int Count = 100_000,
    int Users = 20_000,
    // random — випадкові UUID v4; prefixed — c0ffee03-0000-4000-8000-{n} (як у k6-сценаріях)
    string UserIds = "random",
    int Events = 20,
    // частка замовлень на одну «мега-подію» (0 — рівномірно між подіями)
    double HotEventShare = 0,
    bool Reset = true);

/// <summary>
/// Шардовані замовлення (ЛР7). Ключ шардингу — UserId; номер шарду для кожного запиту обчислює ShardRouter.
/// Заголовки відповіді: X-Shard (обраний шард) або X-Shards-Queried (скільки шардів опитано при fan-out).
/// </summary>
[Route("api/v1")]
public class OrdersController(ShardedOrderStore store, ILogger<OrdersController> logger) : ApiControllerBase
{
    // POST /api/v1/orders — шард-локальний запис
    [HttpPost("orders")]
    public async Task<IActionResult> Create([FromBody] CreateOrderRequest request, CancellationToken ct)
    {
        if (!store.Router.Enabled) return ShardingDisabled();
        if (request.TicketCount <= 0 || request.Amount < 0)
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "Некоректне замовлення",
                "TicketCount має бути > 0, Amount — невід'ємний.");
        }

        var order = new Order
        {
            UserId = request.UserId,
            EventId = request.EventId,
            TicketCount = request.TicketCount,
            Amount = request.Amount,
            CreatedAt = DateTime.UtcNow
        };
        var route = await store.CreateAsync(order, ct);
        MarkShard(route);
        return Created($"/api/v1/users/{order.UserId}/orders", new { order, shard = route.Name, route.Bucket });
    }

    // GET /api/v1/users/{userId}/orders — шард-локальне читання (або ?scatter=true — fan-out для порівняння)
    [HttpGet("users/{userId:guid}/orders")]
    public async Task<IActionResult> UserOrders(Guid userId, [FromQuery] bool scatter = false, [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (!store.Router.Enabled) return ShardingDisabled();
        limit = Math.Clamp(limit, 1, 500);

        var (orders, timings) = scatter
            ? await store.GetUserOrdersScatterAsync(userId, limit, ct)
            : await store.GetUserOrdersAsync(userId, limit, ct);

        Response.Headers["X-Shards-Queried"] = timings.Count.ToString();
        if (!scatter)
        {
            MarkShard(store.Router.Route(userId));
        }
        return Ok(new { userId, orders, shards = timings });
    }

    // GET /api/v1/orders/stats — Cross-Shard Query (Scatter-Gather) по всіх шардах
    [HttpGet("orders/stats")]
    // ?consistency=strong — читати з Primary шардів замість реплік
    public async Task<IActionResult> Stats([FromQuery] Guid? eventId, [FromQuery] int top = 20,
        [FromQuery] string? consistency = null, CancellationToken ct = default)
    {
        if (!store.Router.Enabled) return ShardingDisabled();
        var strong = string.Equals(consistency, "strong", StringComparison.OrdinalIgnoreCase);
        var (sales, timings) = await store.EventSalesAsync(eventId, Math.Clamp(top, 1, 1000), ct, strong);
        Response.Headers["X-Shards-Queried"] = timings.Count.ToString();
        Response.Headers["X-Shard-Nodes"] = string.Join(",", timings.Select(t => t.Node));
        return Ok(new { events = sales, shards = timings });
    }

    // POST /api/v1/orders/{id}/transfer — Cross-Shard Write (2PC), якщо власники на різних шардах
    [HttpPost("orders/{id:guid}/transfer")]
    public async Task<IActionResult> Transfer(Guid id, [FromBody] TransferOrderRequest request, CancellationToken ct)
    {
        if (!store.Router.Enabled) return ShardingDisabled();
        var (outcome, from, to, gid) = await store.TransferAsync(id, request.FromUserId, request.ToUserId,
            request.SimulateCoordinatorCrash, ct);

        if (outcome == TransferOutcome.NotFound)
        {
            return ProblemResult(StatusCodes.Status404NotFound, "Замовлення не знайдено",
                $"Замовлення '{id}' не належить користувачу '{request.FromUserId}'.");
        }

        var body = new { orderId = id, outcome = outcome.ToString(), from = from.Name, to = to.Name, gid };
        return outcome == TransferOutcome.InDoubt ? StatusCode(StatusCodes.Status202Accepted, body) : Ok(body);
    }

    // ---------- лабораторні ендпоінти (демонстрація та заміри ЛР7) ----------

    // GET /api/v1/lab/sharding/route/{key} — куди потрапить ключ (детермінованість маршрутизації)
    [HttpGet("lab/sharding/route/{key:guid}")]
    public IActionResult Route(Guid key)
    {
        if (!store.Router.Enabled) return ShardingDisabled();
        var route = store.Router.Route(key);
        return Ok(new
        {
            key,
            strategy = route.Strategy.ToString(),
            shard = route.Name,
            shardIndex = route.Index,
            bucket = route.Bucket,
            buckets = store.Router.BucketCount,
            hash = ShardHash.Of(key).ToString("x16"),
            firstSegment = ShardHash.FirstSegment(key)
        });
    }

    // GET /api/v1/lab/sharding/distribution — Data Distribution Matrix
    [HttpGet("lab/sharding/distribution")]
    public async Task<IActionResult> Distribution(CancellationToken ct)
    {
        if (!store.Router.Enabled) return ShardingDisabled();
        return Ok(new { strategy = store.Router.Strategy.ToString(), shards = await store.DistributionAsync(ct) });
    }

    // POST /api/v1/lab/sharding/generate — тестовий масив даних
    [HttpPost("lab/sharding/generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateOrdersRequest request, CancellationToken ct)
    {
        if (!store.Router.Enabled) return ShardingDisabled();
        if (request.Count is <= 0 or > 2_000_000 || request.Users <= 0 || request.Events <= 0)
        {
            return ProblemResult(StatusCodes.Status400BadRequest, "Некоректні параметри генерації");
        }

        if (request.Reset)
        {
            await store.ResetAsync(ct);
        }

        var rnd = new Random(42);
        var users = Enumerable.Range(1, request.Users)
            .Select(n => request.UserIds == "prefixed" ? PrefixedUserId(n) : RandomGuid(rnd))
            .ToArray();
        var events = Enumerable.Range(1, request.Events).Select(EventId).ToArray();
        var start = DateTime.UtcNow.AddDays(-30);

        var orders = Enumerable.Range(0, request.Count).Select(_ =>
        {
            var tickets = rnd.Next(1, 5);
            return new Order
            {
                Id = RandomGuid(rnd),
                UserId = users[rnd.Next(users.Length)],
                EventId = rnd.NextDouble() < request.HotEventShare ? events[0] : events[rnd.Next(events.Length)],
                TicketCount = tickets,
                Amount = tickets * (500 + rnd.Next(0, 5) * 100),
                CreatedAt = start.AddSeconds(rnd.Next(0, 30 * 24 * 3600))
            };
        });

        var counts = await store.BulkInsertAsync(orders, ct);
        logger.LogInformation("Generated {Count} orders: {Counts}", request.Count, string.Join("/", counts));
        return Ok(new { strategy = store.Router.Strategy.ToString(), inserted = counts, shards = await store.DistributionAsync(ct) });
    }

    // GET /api/v1/lab/sharding/in-doubt — «завислі» 2PC-транзакції
    [HttpGet("lab/sharding/in-doubt")]
    public async Task<IActionResult> InDoubt(CancellationToken ct) =>
        store.Router.Enabled ? Ok(await store.ListInDoubtAsync(ct)) : ShardingDisabled();

    // POST /api/v1/lab/sharding/in-doubt/resolve — відновлення координатора
    [HttpPost("lab/sharding/in-doubt/resolve")]
    public async Task<IActionResult> ResolveInDoubt(CancellationToken ct) =>
        store.Router.Enabled
            ? Ok((await store.ResolveInDoubtAsync(ct)).Select(d => new { gid = d.Gid, decision = d.Decision }))
            : ShardingDisabled();

    // ---------- helpers ----------

    /// <summary>Ті самі id, що й у k6 (load-tests/lib/ids.js): фіксований перший сегмент, номер — у кінці.</summary>
    internal static Guid PrefixedUserId(int n) => Guid.Parse($"c0ffee03-0000-4000-8000-{n:D12}");

    private static Guid EventId(int n) => Guid.Parse($"c0ffee0e-0000-4000-8000-{n:D12}");

    private static Guid RandomGuid(Random rnd)
    {
        Span<byte> bytes = stackalloc byte[16];
        rnd.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // версія 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // варіант RFC 4122
        return new Guid(bytes);
    }

    private void MarkShard(ShardRoute route)
    {
        Response.Headers["X-Shard"] = route.Name;
        if (route.Bucket is not null)
        {
            Response.Headers["X-Shard-Bucket"] = route.Bucket.Value.ToString();
        }
    }

    private ObjectResult ShardingDisabled() =>
        ProblemResult(StatusCodes.Status503ServiceUnavailable, "Шардинг не налаштовано",
            "Модуль замовлень потребує Sharding:Shards у конфігурації.");
}
