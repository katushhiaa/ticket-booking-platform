using System.IO.Compression;

namespace TicketBooking.Api.Infrastructure.Caching;

/// <summary>
/// Значення кешу у форматі зберігання. Великі JSON зберігаються стиснутими (gzip):
/// карта залу на 1000 місць — ~149 КБ JSON → ~5 КБ у Redis.
/// Без стиснення під навантаженням єдине мультиплексоване з'єднання StackExchange.Redis забивалось
/// мегабайтами вхідних даних і команди падали з RedisTimeoutException (див. README, ЛР5).
/// Формат: перший байт — 0 = raw UTF-8 JSON, 1 = gzip(JSON). Малі значення не стискаємо.
/// </summary>
public sealed class CachedPayload
{
    private const byte RawMarker = 0;
    private const byte GzipMarker = 1;
    public const int CompressThresholdBytes = 1024;

    private readonly byte[] _stored;
    private byte[]? _json;

    private CachedPayload(byte[] stored, byte[]? json)
    {
        _stored = stored;
        _json = json;
    }

    /// <summary>Байти, що зберігаються в Redis (маркер + тіло).</summary>
    public byte[] Stored => _stored;

    public bool IsGzip => _stored.Length > 0 && _stored[0] == GzipMarker;

    /// <summary>Тіло без маркера: gzip-потік (якщо <see cref="IsGzip"/>) або сирий JSON.</summary>
    public ReadOnlyMemory<byte> Body => _stored.AsMemory(Math.Min(1, _stored.Length));

    public static CachedPayload FromStored(byte[] stored) => new(stored, null);

    public static CachedPayload FromJson(byte[] json)
    {
        if (json.Length < CompressThresholdBytes)
        {
            return new CachedPayload([RawMarker, .. json], json);
        }

        using var output = new MemoryStream(json.Length / 16 + 16);
        output.WriteByte(GzipMarker);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(json);
        }
        return new CachedPayload(output.ToArray(), json);
    }

    /// <summary>Розпакований JSON (для клієнтів без Accept-Encoding: gzip).</summary>
    public byte[] GetJsonBytes()
    {
        if (_json is not null)
        {
            return _json;
        }

        if (!IsGzip)
        {
            return _json = Body.ToArray();
        }

        using var input = new MemoryStream(_stored, 1, _stored.Length - 1);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return _json = output.ToArray();
    }
}
