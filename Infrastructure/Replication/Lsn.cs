namespace TicketBooking.Api.Infrastructure.Replication;

/// <summary>
/// PostgreSQL Log Sequence Number: позиція в журналі WAL. Текстовий формат "16/B374D848" —
/// старші та молодші 32 біти 64-бітного зсуву в hex. Більший LSN = пізніша позиція в журналі.
/// </summary>
public static class Lsn
{
    public static bool TryParse(string? text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('/');
        if (parts.Length != 2 ||
            !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var hi) ||
            !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var lo))
        {
            return false;
        }

        value = ((ulong)hi << 32) | lo;
        return true;
    }

    public static string Format(ulong value) => $"{value >> 32:X}/{value & 0xFFFFFFFF:X}";
}
