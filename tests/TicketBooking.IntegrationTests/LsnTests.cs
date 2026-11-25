using TicketBooking.Api.Infrastructure.Replication;

namespace TicketBooking.IntegrationTests;

/// <summary>LSN (позиція в WAL), за яким Read-Your-Writes порівнює Primary і репліку.</summary>
public class LsnTests
{
    [Theory]
    [InlineData("0/0", 0UL)]
    [InlineData("0/56023D90", 0x56023D90UL)]
    [InlineData("16/B374D848", 0x16_B374D848UL)]
    public void Parse_TextFormat(string text, ulong expected)
    {
        Assert.True(Lsn.TryParse(text, out var value));
        Assert.Equal(expected, value);
        Assert.Equal(text, Lsn.Format(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("56023D90")]
    [InlineData("x/1")]
    public void Parse_Invalid(string? text) => Assert.False(Lsn.TryParse(text, out _));

    [Fact]
    public void Ordering_FollowsWalPosition()
    {
        // Молодша частина переповнилась у старшу: 0/FFFFFFFF < 1/0
        Lsn.TryParse("0/FFFFFFFF", out var before);
        Lsn.TryParse("1/0", out var after);
        Assert.True(before < after);
    }
}
