using TexasPoker.Engine;

namespace TexasPoker.Tests;

public sealed class GilTradesTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 21, 0, 0, DateTimeKind.Utc);

    private static GilTrade Trade(long amount, string name = "Aya Ren", int minutes = 1) => new(name, "Twintania", amount, T0.AddMinutes(minutes));

    [Fact]
    public void A_buy_in_already_entered_is_only_noted()
    {
        var trades = new GilTrades();
        trades.Expect("Aya Ren", 500_000, T0);

        var result = trades.Settle(Trade(500_000));

        Assert.False(result.Warning);
        Assert.Contains("buy-in is collected", result.Message);
        Assert.Empty(trades.Waiting);
        Assert.Equal(0, trades.ExpectedWith("Aya Ren", T0.AddMinutes(2)));
    }

    [Fact]
    public void Gil_nobody_expected_waits_for_the_dealer()
    {
        var trades = new GilTrades();

        trades.Settle(Trade(250_000));

        var waiting = Assert.Single(trades.Waiting);
        Assert.Equal(250_000, waiting.Amount);
        trades.Dismiss(waiting);
        Assert.Empty(trades.Waiting);
    }

    [Fact]
    public void A_big_buy_in_arrives_in_parts()
    {
        var trades = new GilTrades();
        trades.Expect("Aya Ren", 1_500_000, T0);

        var first = trades.Settle(Trade(1_000_000));
        var second = trades.Settle(Trade(500_000, minutes: 2));

        Assert.Contains("500,000 still to collect", first.Message);
        Assert.Contains("collected", second.Message);
        Assert.Empty(trades.Waiting);
    }

    [Fact]
    public void Extra_gil_on_top_of_a_buy_in_waits()
    {
        var trades = new GilTrades();
        trades.Expect("Aya Ren", 500_000, T0);

        trades.Settle(Trade(600_000));

        Assert.Equal(100_000, Assert.Single(trades.Waiting).Amount);
    }

    [Fact]
    public void A_cash_out_paid_in_full_or_short_or_over()
    {
        var trades = new GilTrades();
        trades.Expect("Aya Ren", -800_000, T0);
        Assert.Contains("200,000 still to pay", trades.Settle(Trade(-600_000)).Message);
        Assert.Contains("cash-out is paid", trades.Settle(Trade(-200_000)).Message);

        trades.Expect("Aya Ren", -100_000, T0);
        var over = trades.Settle(Trade(-150_000));
        Assert.True(over.Warning);
        Assert.Contains("50,000 too much", over.Message);
    }

    [Fact]
    public void Paying_someone_nothing_is_owed_to_is_a_warning()
    {
        var trades = new GilTrades();

        var result = trades.Settle(Trade(-50_000));

        Assert.True(result.Warning);
        Assert.Empty(trades.Waiting);
    }

    [Fact]
    public void An_undone_add_waits_again()
    {
        var trades = new GilTrades();
        trades.Settle(Trade(250_000));
        var trade = Assert.Single(trades.Waiting);
        trade.AddWhenHandEnds = true;
        trades.Dismiss(trade);

        trades.Hold(trade);
        trades.Hold(trade);

        Assert.Same(trade, Assert.Single(trades.Waiting));
        Assert.False(trade.AddWhenHandEnds);
    }

    [Fact]
    public void Old_expectations_are_forgotten()
    {
        var trades = new GilTrades();
        trades.Expect("Aya Ren", 500_000, T0);

        trades.Settle(Trade(500_000, minutes: 45));

        Assert.Single(trades.Waiting);
    }
}
