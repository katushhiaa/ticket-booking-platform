namespace TicketBooking.Api.Infrastructure.Sharding;

/// <summary>
/// Детермінована хеш-функція ключа шардингу. Не string.GetHashCode (у .NET він рандомізований для кожного процесу —
/// різні інстанси API порахували б різні шарди). FNV-1a 64 + фіналізатор SplitMix64; та сама функція є в
/// scripts/sharding/shardlib.py для офлайн-аналізу розподілу.
/// </summary>
public static class ShardHash
{
    private const ulong FnvOffset = 0xCBF29CE484222325;
    private const ulong FnvPrime = 0x100000001B3;

    public static ulong Of(Guid key)
    {
        // Канонічна форма ключа: "N" (32 hex-символи, нижній регістр) — однакова в C# і Python
        Span<char> chars = stackalloc char[32];
        key.TryFormat(chars, out _, "N");

        var hash = FnvOffset;
        foreach (var c in chars)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
        }
        return Mix(hash);
    }

    /// <summary>SplitMix64: перемішує біти, щоб остача від ділення на N була рівномірною.</summary>
    private static ulong Mix(ulong x)
    {
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }

    /// <summary>Перший сегмент UUID як 32-бітне число (те, що бере «наївний» modulo).</summary>
    public static uint FirstSegment(Guid key)
    {
        Span<char> chars = stackalloc char[32];
        key.TryFormat(chars, out _, "N");
        return uint.Parse(chars[..8], System.Globalization.NumberStyles.HexNumber);
    }
}
