using TexasPoker.Engine;

namespace TexasPoker.Tests;

/// <summary>Bomb pots, running it twice, the venue jackpot cut and what venue promotions read from a showdown.</summary>
public class PromotionTests
{
    /// <summary>A deck that deals exactly these cards in this order, then the rest of a fresh deck.</summary>
    private static Func<Deck> Exactly(string cards)
    {
        var order = Card.ParseMany(cards).ToList();
        var used = order.ToHashSet();
        order.AddRange(Deck.Ordered().Where(c => !used.Contains(c)));
        return () => new Deck(order);
    }

    private static Table NewTable(Func<Deck>? deck, params long[] stacks)
    {
        var table = new Table(deck) { SmallBlind = 50, BigBlind = 100 };
        for (var i = 0; i < stacks.Length; i++)
            table.AddSeat($"P{i}", "World", stacks[i]);
        return table;
    }

    [Fact]
    public void Bomb_pot_takes_the_same_from_everyone_and_starts_on_the_flop()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        var lines = new List<string>();
        table.Announced += lines.AddRange;

        table.StartHand(bombPot: 200);

        Assert.True(table.IsBombPot);
        Assert.Equal(Street.Flop, table.Street);
        Assert.Equal(3, table.Board.Count);
        Assert.Equal(600, table.Pot);
        Assert.All(table.Seats, s => Assert.Equal(800, s.Stack));
        Assert.Equal(-1, table.SmallBlindIndex);
        Assert.Same(table.Seats[1], table.Actor); // first player left of the button (P0)
        Assert.True(table.GetOptions().CanCheck);
        Assert.Equal("Bomb pot: everyone puts in 200", table.HandActions[0].Text);
        Assert.Contains(lines, l => l.Contains("BOMB POT"));
        Assert.Contains("Flop: " + Card.Join(table.Board, false), lines);
    }

    [Fact]
    public void Bomb_pot_short_stack_goes_all_in_for_what_they_have()
    {
        var table = NewTable(null, 1000, 150, 1000);
        var lines = new List<string>();
        table.Announced += lines.AddRange;

        table.StartHand(bombPot: 200);

        Assert.Equal(550, table.Pot);
        Assert.True(table.Seats[1].AllIn);
        Assert.Contains("P1 puts in 150 (all-in)", lines);
        Assert.Same(table.Seats[2], table.Actor); // P1 is all-in, so P2 acts first
    }

    [Fact]
    public void Bomb_pot_with_everyone_all_in_runs_out_to_showdown()
    {
        var table = NewTable(null, 100, 100);
        table.StartHand(bombPot: 200);

        Assert.Equal(Street.HandOver, table.Street);
        Assert.Equal(5, table.Board.Count);
        Assert.Equal(200, table.Seats.Sum(s => s.Stack));
    }

    [Fact]
    public void Next_hand_after_a_bomb_pot_has_blinds_again()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand(bombPot: 100);
        table.CancelHand();
        table.StartHand();

        Assert.False(table.IsBombPot);
        Assert.Equal(Street.Preflop, table.Street);
        Assert.Equal(150, table.Pot);
    }

    // Heads-up, button P0: P1 is dealt first. P1 has Aces, P0 Kings. Run 1 gives P0 trips, run 2 gives P1 trips.
    private const string TwoRuns = "As Ks Ah Kh  2s Kd 2c 3d  3s 7h  4s 9s  5s Ac 4c 5d  6s 8h  7s Ts";

    [Fact]
    public void Run_it_twice_splits_the_pot_between_the_two_boards()
    {
        var table = NewTable(Exactly(TwoRuns), 1000, 1000);
        table.RunItTwice = true;
        var lines = new List<string>();
        table.Announced += lines.AddRange;

        table.StartHand();
        table.Act(PlayerAction.AllIn); // P0 (button, small blind) acts first heads-up
        table.Act(PlayerAction.AllIn);

        Assert.Equal(Street.HandOver, table.Street);
        Assert.True(table.RanTwice);
        Assert.Equal("[Kd][2c][3d][7h][9s]", Card.Join(table.Board, false));
        Assert.Equal("[Ac][4c][5d][8h][10s]", Card.Join(table.SecondBoard, false));
        Assert.Equal(1000, table.Seats[0].Stack);
        Assert.Equal(1000, table.Seats[1].Stack);
        Assert.Contains("All-in: running it twice", lines);
        Assert.Contains("Main pot (run 1) 1,000 to P0 (Three of a kind, Kings)", lines);
        Assert.Contains("Main pot (run 2) 1,000 to P1 (Three of a kind, Aces)", lines);
        Assert.Contains("P1 shows [As][Ah]: Pair of Aces / Three of a kind, Aces", lines);
    }

    [Fact]
    public void Without_run_it_twice_there_is_one_board()
    {
        var table = NewTable(Exactly(TwoRuns), 1000, 1000);
        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        Assert.False(table.RanTwice);
        Assert.Empty(table.SecondBoard);
        Assert.Equal(2000, table.Seats[0].Stack); // Kings make trips on the first board
    }

    [Fact]
    public void Run_it_twice_only_when_nobody_can_bet_any_more()
    {
        var table = NewTable(null, 1000, 1000);
        table.RunItTwice = true;
        table.StartHand();
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Check);

        Assert.Equal(Street.Flop, table.Street);
        Assert.False(table.RanTwice);
    }

    [Fact]
    public void Run_it_twice_from_the_turn_keeps_the_board_so_far()
    {
        var table = NewTable(Exactly(TwoRuns), 1000, 1000);
        table.RunItTwice = true;
        table.StartHand();
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Check); // flop
        table.Act(PlayerAction.Check);
        table.Act(PlayerAction.Check); // turn
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        Assert.True(table.RanTwice);
        Assert.Equal(table.Board.Take(4), table.SecondBoard.Take(4));
        Assert.NotEqual(table.Board[4], table.SecondBoard[4]);
        Assert.Equal(2000, table.Seats.Sum(s => s.Stack));
    }

    [Fact]
    public void Jackpot_cut_on_top_of_the_rake_comes_out_of_the_pot()
    {
        var table = NewTable(Exactly(TwoRuns), 1000, 1000);
        table.RakePercent = 5;
        table.JackpotDropPercent = 1;
        var lines = new List<string>();
        table.Announced += lines.AddRange;
        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        Assert.Equal(20, table.LastJackpotDrop);
        Assert.Equal(100, table.HouseEarnings);
        Assert.Equal(1880, table.Seats[0].Stack);
        Assert.Equal(20, table.LastResults[0].Jackpot);
        Assert.Contains("Main pot 1,880 to P0 (Three of a kind, Kings) (rake 100, jackpot 20)", lines);
    }

    [Fact]
    public void Jackpot_cut_from_the_rake_is_paid_by_the_house()
    {
        var table = NewTable(Exactly(TwoRuns), 1000, 1000);
        table.RakePercent = 5;
        table.JackpotDropPercent = 1;
        table.JackpotDropFromRake = true;
        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        Assert.Equal(20, table.LastJackpotDrop);
        Assert.Equal(80, table.HouseEarnings);
        Assert.Equal(1900, table.Seats[0].Stack);
    }

    [Fact]
    public void Jackpot_cut_has_a_cap_and_skips_hands_without_a_flop()
    {
        var capped = NewTable(Exactly(TwoRuns), 1000, 1000);
        capped.JackpotDropPercent = 10;
        capped.JackpotDropCap = 50;
        capped.StartHand();
        capped.Act(PlayerAction.AllIn);
        capped.Act(PlayerAction.AllIn);
        Assert.Equal(50, capped.LastJackpotDrop);

        var noFlop = NewTable(null, 1000, 1000);
        noFlop.JackpotDropPercent = 10;
        noFlop.StartHand();
        noFlop.Act(PlayerAction.Fold);
        Assert.Equal(0, noFlop.LastJackpotDrop);
        Assert.Equal(2000, noFlop.Seats.Sum(s => s.Stack));
    }

    [Fact]
    public void Showdown_records_each_hand_and_whether_both_hole_cards_play()
    {
        // P1 holds 8-8 and makes quads with both; P0's best is the board's quads, which needs neither hole card.
        var table = NewTable(Exactly("8s Kh 8h 2h  2s 8d 8c Ad  3s Ks  4s 3c"), 1000, 1000);
        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        var p0 = table.LastShowdown.Single(h => h.Seat == table.Seats[0]);
        var p1 = table.LastShowdown.Single(h => h.Seat == table.Seats[1]);
        Assert.True(p1.WonMainPot);
        Assert.False(p0.WonMainPot);
        Assert.Equal(HandCategory.FourOfAKind, p1.WithBothHoleCards!.Value.Category);
        Assert.Equal(p1.Best.Score, p1.WithBothHoleCards.Value.Score);
        Assert.Equal(HandCategory.TwoPair, p0.Best.Category);
        Assert.True(p0.WithBothHoleCards!.Value.Score <= p0.Best.Score);
    }

    [Fact]
    public void Best_with_both_hole_cards_ignores_a_hand_made_on_the_board()
    {
        // The best hand is the board's four Eights; using both Kh and 2h, the best is three Eights.
        var hole = Card.ParseMany("Kh 2h");
        var board = Card.ParseMany("8d 8c 8s 8h Ks");
        Assert.Equal(HandCategory.FourOfAKind, HandEvaluator.Evaluate(hole.Concat(board).ToList()).Category);
        Assert.Equal(HandCategory.ThreeOfAKind, HandEvaluator.BestWithBothHoleCards(hole, board)!.Value.Category);
        Assert.Null(HandEvaluator.BestWithBothHoleCards(hole, Card.ParseMany("8d 8c")));
    }

    [Fact]
    public void Bonus_adds_to_the_stack_without_counting_as_a_buy_in()
    {
        var table = NewTable(null, 1000, 1000);
        table.AwardBonus(table.Seats[0], 500);

        Assert.Equal(1500, table.Seats[0].Stack);
        Assert.Equal(500, table.Seats[0].Net);

        table.StartHand();
        Assert.Throws<InvalidOperationException>(() => table.AwardBonus(table.Seats[0], 10));
    }

    [Fact]
    public void Cancelled_hand_is_marked_and_records_no_showdown()
    {
        var table = NewTable(null, 1000, 1000);
        table.StartHand();
        table.CancelHand();

        Assert.True(table.LastHandCancelled);
        Assert.Empty(table.LastShowdown);
        Assert.Equal(2, table.DealtIn);
    }
}
