using TexasPoker.Engine;

namespace TexasPoker.Tests;

public sealed class TournamentTableTests
{
    private static Table NewTable(params long[] stacks)
    {
        var table = new Table { SmallBlind = 50, BigBlind = 100, TournamentMode = true };
        for (var i = 0; i < stacks.Length; i++)
            table.AddSeat($"P{i}", "World", stacks[i]);
        return table;
    }

    [Fact]
    public void Antes_are_dead_money_on_top_of_the_blinds()
    {
        var table = NewTable(1000, 1000, 1000);
        table.Ante = 10;

        table.StartHand();

        Assert.Equal(30 + 50 + 100, table.Pot);
        Assert.Equal(990, table.Seats[0].Stack);
        Assert.Equal(940, table.Seats[1].Stack);
        Assert.Equal(890, table.Seats[2].Stack);
        // The button still calls the full big blind: the ante isn't part of anyone's bet.
        Assert.Equal(100, table.GetOptions().ToCall);
        Assert.Contains(table.HandActions, a => a.Text == "Antes: 10 each. Pot: 30");
        Assert.All(table.Seats, s => Assert.Equal(1000, s.StartingStack));
    }

    [Fact]
    public void A_player_who_can_only_pay_the_ante_is_all_in()
    {
        var table = NewTable(1000, 1000, 1000, 5);
        table.Ante = 10;

        table.StartHand();

        Assert.True(table.Seats[3].AllIn);
        Assert.Equal(0, table.Seats[3].Stack);
        Assert.Equal(3 * 10 + 5 + 50 + 100, table.Pot);
    }

    [Fact]
    public void A_player_sitting_out_is_dealt_in_and_folded_on_their_turn()
    {
        var table = NewTable(1000, 1000, 1000);
        table.Seats[0].SittingOut = true; // the button, first to act three-handed

        table.StartHand();

        Assert.True(table.Seats[0].InHand);
        Assert.True(table.Seats[0].Folded);
        Assert.Equal(table.Seats[1], table.Actor);
        Assert.Contains(table.HandActions, a => a.Text == "P0 folds (sitting out)");
    }

    [Fact]
    public void A_sitting_out_big_blind_still_posts_it()
    {
        var table = NewTable(1000, 1000, 1000);
        table.Seats[2].SittingOut = true;

        table.StartHand();

        Assert.Equal(900, table.Seats[2].Stack);
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Call);
        // The big blind's option: folded for them, so the other two see a flop.
        Assert.True(table.Seats[2].Folded);
        Assert.Equal(Street.Flop, table.Street);
    }

    [Fact]
    public void A_moved_player_sits_just_before_the_button()
    {
        var table = NewTable(1000, 1000, 1000);
        table.StartHand();
        table.CancelHand();
        var button = table.Seats[table.ButtonIndex];

        var moved = table.AddSeatBeforeButton("New", "World", 2000);

        Assert.Same(button, table.Seats[table.ButtonIndex]);
        Assert.Same(moved, table.Seats[table.ButtonIndex - 1]);
        table.StartHand();
        Assert.True(moved.InHand);
        // The button moved on one seat, past the new player: they pay the blinds when they reach them.
        Assert.NotEqual(moved, table.Seats[table.SmallBlindIndex]);
    }

    [Fact]
    public void No_rake_and_no_bomb_pots()
    {
        var table = NewTable(1000, 1000);
        table.RakePercent = 5;

        Assert.Throws<InvalidOperationException>(() => table.StartHand(200));
        table.StartHand();
        table.Act(PlayerAction.Fold);

        Assert.Equal(0, table.HouseEarnings);
        Assert.Equal(1050, table.Seats.Single(s => !s.Folded).Stack);
    }
}
