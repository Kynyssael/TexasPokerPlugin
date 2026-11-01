using TexasPoker.Engine;

namespace TexasPoker.Tests;

public class TableTests
{
    /// <summary>
    /// Builds a deck that deals the given hole cards (in deal order: first player left of the button
    /// first) and then the given five board cards, with burns in between.
    /// </summary>
    private static Func<Deck> Rigged(string[] holes, string board)
    {
        var hole = holes.Select(Card.ParseMany).ToList();
        var boardCards = Card.ParseMany(board);
        var used = hole.SelectMany(h => h).Concat(boardCards).ToHashSet();
        var spare = new Queue<Card>(Deck.Ordered().Where(c => !used.Contains(c)));

        var order = new List<Card>();
        order.AddRange(hole.Select(h => h[0]));
        order.AddRange(hole.Select(h => h[1]));
        order.Add(spare.Dequeue());
        order.AddRange(boardCards.Take(3));
        order.Add(spare.Dequeue());
        order.Add(boardCards[3]);
        order.Add(spare.Dequeue());
        order.Add(boardCards[4]);
        order.AddRange(spare);
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
    public void Heads_up_button_posts_small_blind_and_acts_first()
    {
        var table = NewTable(null, 1000, 1000);
        table.StartHand();

        Assert.Equal(0, table.ButtonIndex);
        Assert.Equal(0, table.SmallBlindIndex);
        Assert.Equal(1, table.BigBlindIndex);
        Assert.Same(table.Seats[0], table.Actor);
        Assert.Equal(950, table.Seats[0].Stack);
        Assert.Equal(900, table.Seats[1].Stack);
    }

    [Fact]
    public void Fold_to_big_blind_awards_blinds()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand(); // button P0, SB P1, BB P2, P0 acts first

        Assert.Same(table.Seats[0], table.Actor);
        table.Act(PlayerAction.Fold);
        table.Act(PlayerAction.Fold);

        Assert.Equal(Street.HandOver, table.Street);
        Assert.Equal(1050, table.Seats[2].Stack);
        Assert.Equal(950, table.Seats[1].Stack);
        Assert.Equal(3000, table.Seats.Sum(s => s.Stack));
    }

    [Fact]
    public void Big_blind_gets_option_after_limps()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand();
        table.Act(PlayerAction.Call); // P0
        table.Act(PlayerAction.Call); // P1 completes

        Assert.Same(table.Seats[2], table.Actor);
        var options = table.GetOptions();
        Assert.True(options.CanCheck);
        Assert.True(options.CanRaise);

        table.Act(PlayerAction.Check);
        Assert.Equal(Street.Flop, table.Street);
        Assert.Same(table.Seats[1], table.Actor); // first left of button
    }

    [Fact]
    public void Enforces_minimum_raise()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand();

        Assert.Throws<InvalidOperationException>(() => table.Act(PlayerAction.BetOrRaise, 150));
        table.Act(PlayerAction.BetOrRaise, 300); // raise of 200
        Assert.Equal(500, table.GetOptions().MinRaiseTo);
        Assert.Throws<InvalidOperationException>(() => table.Act(PlayerAction.BetOrRaise, 400));
    }

    [Fact]
    public void Short_all_in_does_not_reopen_betting()
    {
        // P0 button, P1 SB with only 250, P2 BB.
        var table = NewTable(null, 1000, 250, 1000);
        table.StartHand();
        table.Act(PlayerAction.BetOrRaise, 200); // P0 raises to 200
        table.Act(PlayerAction.AllIn);           // P1 all-in to 250: short raise of 50
        table.Act(PlayerAction.Call);            // P2 calls 250

        Assert.Same(table.Seats[0], table.Actor);
        var options = table.GetOptions();
        Assert.False(options.CanRaise);
        Assert.Equal(50, options.CallAmount);
        Assert.Throws<InvalidOperationException>(() => table.Act(PlayerAction.BetOrRaise, 1000));
    }

    [Fact]
    public void Side_pots_are_split_correctly()
    {
        // Deal order starts left of button: P1, P2, P0.
        // P1 (short stack) has the best hand, P2 second best, P0 worst.
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 200, 600);
        table.StartHand();

        table.Act(PlayerAction.AllIn); // P0 1000
        table.Act(PlayerAction.AllIn); // P1 200
        table.Act(PlayerAction.AllIn); // P2 600

        Assert.Equal(Street.HandOver, table.Street);
        Assert.Equal(600, table.Seats[1].Stack);  // main pot 200 x 3
        Assert.Equal(800, table.Seats[2].Stack);  // side pot 400 x 2
        Assert.Equal(400, table.Seats[0].Stack);  // 400 uncalled returned
        Assert.Equal(1800, table.Seats.Sum(s => s.Stack));
        Assert.Equal(["Main pot", "Side pot 1", "Side pot 2"], table.LastResults.Select(r => r.Label));
    }

    [Fact]
    public void Split_pot_gives_odd_chip_left_of_button()
    {
        var deck = Rigged(["2c 3d", "2d 3c", "4h 5h"], "As Ks Qs Js 10s");
        var table = new Table(deck) { SmallBlind = 25, BigBlind = 50 };
        table.AddSeat("P0", "", 1000);
        table.AddSeat("P1", "", 1000);
        table.AddSeat("P2", "", 1000);
        table.StartHand();

        table.Act(PlayerAction.Fold);   // P0 folds; pot has 75
        table.Act(PlayerAction.Call);   // P1 completes to 50
        table.Act(PlayerAction.Check);  // P2
        for (var street = 0; street < 3; street++)
        {
            table.Act(PlayerAction.Check);
            table.Act(PlayerAction.Check);
        }

        Assert.Equal(Street.HandOver, table.Street);
        Assert.Equal(1000, table.Seats[1].Stack);
        Assert.Equal(1000, table.Seats[2].Stack);
        Assert.Equal(2, table.LastResults[0].Winners.Count);
    }

    [Fact]
    public void Odd_chip_goes_to_first_winner_left_of_button()
    {
        var deck = Rigged(["2c 3d", "2d 3c", "4h 5h"], "As Ks Qs Js 10s");
        var table = new Table(deck) { SmallBlind = 25, BigBlind = 50 };
        table.AddSeat("P0", "", 1000);
        table.AddSeat("P1", "", 1000);
        table.AddSeat("P2", "", 1000);
        table.StartHand();

        table.Act(PlayerAction.Call);   // P0 50
        table.Act(PlayerAction.Fold);   // P1 folds 25 -> pot 125
        table.Act(PlayerAction.Check);  // P2
        for (var street = 0; street < 3; street++)
        {
            table.Act(PlayerAction.Check);
            table.Act(PlayerAction.Check);
        }

        // P2 is first left of the button among winners, so gets the odd chip.
        Assert.Equal(1013, table.Seats[2].Stack);
        Assert.Equal(1012, table.Seats[0].Stack);
    }

    [Fact]
    public void Undo_restores_previous_state()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand();
        table.Act(PlayerAction.BetOrRaise, 300);
        Assert.Equal(700, table.Seats[0].Stack);

        table.Undo();
        Assert.Equal(1000, table.Seats[0].Stack);
        Assert.Same(table.Seats[0], table.Actor);
        Assert.Equal(100, table.CurrentBet);
    }

    [Fact]
    public void Last_action_tracks_actions_and_undo()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand();
        Assert.Null(table.LastAction);

        table.Act(PlayerAction.BetOrRaise, 300);
        Assert.Equal("P0 raises to 300", table.LastAction);
        table.Act(PlayerAction.Call);
        Assert.Equal("P1 calls 300", table.LastAction);

        table.Undo();
        Assert.Equal("P0 raises to 300", table.LastAction);
    }

    [Fact]
    public void Cancel_returns_all_bets()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand();
        table.Act(PlayerAction.BetOrRaise, 300);
        table.CancelHand();

        Assert.All(table.Seats, s => Assert.Equal(1000, s.Stack));
        Assert.False(table.HandInProgress);
    }

    [Fact]
    public void Button_moves_each_hand_and_skips_busted_players()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.StartHand();
        table.CancelHand();
        table.StartHand();
        Assert.Equal(1, table.ButtonIndex);
        table.CancelHand();

        table.Seats[2].SittingOut = true;
        table.StartHand();
        Assert.Equal(0, table.ButtonIndex);
    }

    [Fact]
    public void Rake_is_not_taken_from_uncalled_bet()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.RakePercent = 5;
        table.StartHand();
        table.Act(PlayerAction.Call);  // P0
        table.Act(PlayerAction.Call);  // P1
        table.Act(PlayerAction.Check); // P2; pot 300, flop dealt

        table.Act(PlayerAction.BetOrRaise, 300); // P1 bets, nobody calls
        table.Act(PlayerAction.Fold);
        table.Act(PlayerAction.Fold);

        // 5% of the 300 contested; the uncalled 300 bet comes back untouched.
        Assert.Equal(15, table.HouseEarnings);
        Assert.Equal(1185, table.Seats[1].Stack);
        Assert.Equal(15, table.LastResults[0].Rake);
        Assert.Equal(3000, table.Seats.Sum(s => s.Stack) + table.HouseEarnings);
    }

    [Fact]
    public void No_flop_no_drop()
    {
        var table = NewTable(null, 1000, 1000, 1000);
        table.RakePercent = 5;
        table.StartHand();
        table.Act(PlayerAction.Fold);
        table.Act(PlayerAction.Fold);

        Assert.Equal(0, table.HouseEarnings);
        Assert.Equal(1050, table.Seats[2].Stack);
    }

    [Fact]
    public void Rake_is_capped_and_taken_from_main_pot_first()
    {
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 200, 600);
        table.RakePercent = 10;
        table.RakeCap = 100;
        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        // Contested 600 + 800 = 1,400; 10% would be 140, capped at 100, all from the main pot.
        Assert.Equal(100, table.HouseEarnings);
        Assert.Equal(500, table.Seats[1].Stack);
        Assert.Equal(800, table.Seats[2].Stack);
        Assert.Equal(400, table.Seats[0].Stack);
    }

    [Fact]
    public void Rake_off_by_default()
    {
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 200, 600);
        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        Assert.Equal(0, table.HouseEarnings);
        Assert.All(table.LastResults, r => Assert.Equal(0, r.Rake));
    }

    [Fact]
    public void Street_is_announced_one_phrase_per_line()
    {
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 1000, 1000);
        table.SuitSymbols = true;
        var batches = new List<IReadOnlyList<string>>();
        table.Announced += batches.Add;

        table.StartHand();
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Check);

        Assert.Equal(["Hand #1", "Blinds: 50/100", "Button: P0", "P1 posts small blind 50", "P2 posts big blind 100", "Action to P0: 100 to call"], batches[0]);
        Assert.Equal(["P2 checks", "Flop: [A♦][K♦][9♣]", "Pot: 300", "Action to P1: Check or bet"], batches[^1]);
    }

    [Fact]
    public void Custom_messages_replace_the_wording()
    {
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 1000, 1000);
        table.Messages.Set(MessageKind.Call, "[{player}] calls to {amount}{allin}");
        table.Messages.Set(MessageKind.Flop, "/em spreads the flop.\n\nFlop: {cards}  (pot {pot})");
        table.Messages.Set(MessageKind.ActionToCheck, string.Empty);
        var batches = new List<IReadOnlyList<string>>();
        table.Announced += batches.Add;

        table.StartHand();
        table.Act(PlayerAction.Call);
        Assert.Equal(["[P0] calls to 100", "Action to P1: 50 to call"], batches[^1]);
        Assert.Equal("P0 calls 100", table.LastAction); // the dealer's UI keeps the plain wording

        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Check);
        Assert.Equal(["P2 checks", "/em spreads the flop.", "Flop: [Ad][Kd][9c]  (pot 300)"], batches[^1]);
    }

    [Fact]
    public void Turn_message_can_show_just_the_new_card()
    {
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 1000, 1000);
        table.Messages.Set(MessageKind.Turn, "Let's get this party on the road~\nTurn: {cards}\nBoard: {board}");
        var lines = new List<string>();
        table.Announced += lines.AddRange;

        table.StartHand();
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Check);
        for (var i = 0; i < 3; i++)
            table.Act(PlayerAction.Check);

        Assert.Contains("Let's get this party on the road~", lines);
        Assert.Contains("Turn: [4s]", lines);
        Assert.Contains("Board: [Ad][Kd][9c][4s]", lines);
    }

    [Fact]
    public void Setting_the_default_text_clears_the_override()
    {
        var messages = new MessageTemplates();
        messages.Set(MessageKind.Fold, "{player} mucks");
        Assert.True(messages.IsCustom(MessageKind.Fold));

        messages.Set(MessageKind.Fold, MessageTemplates.Info(MessageKind.Fold).Default);
        Assert.False(messages.IsCustom(MessageKind.Fold));
        Assert.Empty(messages.Overrides);
    }

    [Fact]
    public void Street_dealt_fires_after_announcement_with_current_hand()
    {
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 1000, 1000);
        var events = new List<string>();
        table.Announced += lines => events.Add("announce");
        table.StreetDealt += street => events.Add($"{street}: {table.CurrentHand(table.Seats[1])!.Value.Describe()}");

        table.StartHand();
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Check);

        Assert.Equal(["announce", "announce", "announce", "announce", "Flop: Three of a kind, Aces"], events);
    }

    [Fact]
    public void Street_dealt_does_not_fire_during_all_in_run_out()
    {
        var table = NewTable(null, 1000, 1000);
        var streets = new List<Street>();
        table.StreetDealt += streets.Add;

        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.Call);

        Assert.Equal(Street.HandOver, table.Street);
        Assert.Empty(streets);
    }

    [Fact]
    public void Showdown_explains_kicker_when_hands_read_the_same()
    {
        // Heads-up deal order: P1 then P0. Both make a pair of eights; P0's king plays.
        var deck = Rigged(["5h Qh", "Qd Kc"], "8c 8d 2s 3h 7c");
        var table = NewTable(deck, 1000, 1000);
        var lines = new List<string>();
        table.Announced += lines.AddRange;

        table.StartHand();
        table.Act(PlayerAction.Call);
        table.Act(PlayerAction.Check);
        for (var street = 0; street < 3; street++)
        {
            table.Act(PlayerAction.Check);
            table.Act(PlayerAction.Check);
        }

        Assert.Contains("P1 shows [5h][Qh]: Pair of Eights, kickers Q 7 5", lines);
        Assert.Contains("P0 shows [Qd][Kc]: Pair of Eights, kickers K Q 7", lines);
        Assert.Contains("Main pot 200 to P0 (Pair of Eights, King kicker)", lines);
    }

    [Fact]
    public void Showdown_keeps_it_short_when_hands_differ()
    {
        var deck = Rigged(["As Ah", "Ks Kh", "2c 7d"], "Ad Kd 9c 4s 3h");
        var table = NewTable(deck, 1000, 200, 600);
        var lines = new List<string>();
        table.Announced += lines.AddRange;

        table.StartHand();
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);
        table.Act(PlayerAction.AllIn);

        Assert.Contains("P1 shows [As][Ah]: Three of a kind, Aces", lines);
        Assert.Contains("Main pot 600 to P1 (Three of a kind, Aces)", lines);
    }

    [Fact]
    public void Announces_once_per_action()
    {
        var table = NewTable(null, 1000, 1000);
        var batches = new List<IReadOnlyList<string>>();
        table.Announced += batches.Add;

        table.StartHand();
        table.Act(PlayerAction.Call);

        Assert.Equal(2, batches.Count);
        Assert.Contains(batches[1], l => l.StartsWith("P0 calls"));
    }
}
