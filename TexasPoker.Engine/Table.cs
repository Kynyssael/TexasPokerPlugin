namespace TexasPoker.Engine;

public enum Street
{
    WaitingForHand,
    Preflop,
    Flop,
    Turn,
    River,
    HandOver,
}

public enum PlayerAction
{
    Fold,
    Check,
    Call,
    BetOrRaise,
    AllIn,
}

/// <summary>What the player to act may do. Amounts are totals for the current street.</summary>
public sealed record ActionOptions(
    long ToCall,
    long CallAmount,
    bool CanCheck,
    bool CanRaise,
    bool IsBet,
    long MinRaiseTo,
    long MaxRaiseTo);

/// <summary>A pot paid out at the end of a hand. <see cref="Amount"/> is what the winners got, after <see cref="Rake"/>.</summary>
public sealed record PotResult(string Label, long Amount, IReadOnlyList<Seat> Winners, string? Hand, bool Returned, long Rake);

/// <summary>
/// A no-limit Texas Hold'em table run by a dealer. Every public method either succeeds completely or
/// throws <see cref="InvalidOperationException"/> without changing state.
/// </summary>
public sealed class Table
{
    private readonly Func<Deck> deckFactory;
    private readonly List<Seat> seats = new();
    private readonly List<Card> board = new();
    private readonly Stack<Snapshot> undo = new();
    private readonly List<string> pending = new();
    private string? lastAnnounced;
    private readonly List<Street> dealtStreets = new();
    private List<PotResult> lastResults = new();
    private Deck deck = new(Array.Empty<Card>());
    private long handBigBlind;

    public Table(Func<Deck>? deckFactory = null)
    {
        this.deckFactory = deckFactory ?? (() => new Deck());
    }

    /// <summary>Public table talk, batched per dealer operation (one call per click).</summary>
    public event Action<IReadOnlyList<string>>? Announced;

    /// <summary>Raised once per player when hole cards are dealt.</summary>
    public event Action<Seat, IReadOnlyList<Card>>? HoleCardsDealt;

    /// <summary>
    /// Raised after the flop, turn or river is dealt and announced, when there's still betting to do
    /// (not during an all-in run-out).
    /// </summary>
    public event Action<Street>? StreetDealt;

    public IReadOnlyList<Seat> Seats => seats;

    public IReadOnlyList<Card> Board => board;

    public IReadOnlyList<PotResult> LastResults => lastResults;

    /// <summary>Use ♠♥♦♣ instead of letters in announcements.</summary>
    public bool SuitSymbols { get; set; }

    /// <summary>The wording of every announcement; the dealer can edit these.</summary>
    public MessageTemplates Messages { get; } = new();

    /// <summary>Percentage of each contested pot kept by the house; 0 turns the rake off.</summary>
    public decimal RakePercent { get; set; }

    /// <summary>Most rake taken from one hand; 0 means no cap.</summary>
    public long RakeCap { get; set; }

    /// <summary>Take no rake from hands that end before the flop.</summary>
    public bool NoFlopNoDrop { get; set; } = true;

    /// <summary>Rake collected and not yet paid out by the dealer.</summary>
    public long HouseEarnings { get; set; }

    public long SmallBlind { get; set; } = 50;

    public long BigBlind { get; set; } = 100;

    public Street Street { get; private set; }

    public bool HandInProgress => Street is Street.Preflop or Street.Flop or Street.Turn or Street.River;

    public int HandNumber { get; set; }

    public int ButtonIndex { get; set; } = -1;

    public int SmallBlindIndex { get; private set; } = -1;

    public int BigBlindIndex { get; private set; } = -1;

    public int ActorIndex { get; private set; } = -1;

    public Seat? Actor => HandInProgress && ActorIndex >= 0 ? seats[ActorIndex] : null;

    public long CurrentBet { get; private set; }

    public long MinRaise { get; private set; }

    public long Pot => seats.Sum(s => s.Contributed);

    /// <summary>The most recent player action this hand, e.g. "Alice raises to 3,000".</summary>
    public string? LastAction { get; private set; }

    /// <summary>
    /// Goes up by one after every change to the hand (deal, action, undo, cancel) and never goes back,
    /// not even on undo. An action sent for one value can't be applied once the table has moved on.
    /// </summary>
    public int Sequence { get; private set; }

    public bool CanUndo => HandInProgress && undo.Count > 0;

    public Seat AddSeat(string name, string world, long buyIn)
    {
        name = name.Trim();
        world = world.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("Player name is required.");
        if (buyIn < 0)
            throw new InvalidOperationException("Buy-in can't be negative.");
        if (seats.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                           s.World.Equals(world, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{name} is already seated.");

        // New seats are appended, so indices held by an in-progress hand stay valid.
        var seat = new Seat(name, world, buyIn);
        seats.Add(seat);
        return seat;
    }

    /// <summary>Restores a seat from saved data without counting a new buy-in.</summary>
    public Seat RestoreSeat(string name, string world, long stack, long totalBuyIn, bool sittingOut)
    {
        var seat = AddSeat(name, world, stack);
        seat.TotalBuyIn = totalBuyIn;
        seat.SittingOut = sittingOut;
        return seat;
    }

    public void RemoveSeat(Seat seat)
    {
        if (HandInProgress)
            throw new InvalidOperationException("Finish or cancel the hand before removing players.");

        var index = seats.IndexOf(seat);
        if (index < 0)
            return;

        seats.RemoveAt(index);
        if (index <= ButtonIndex)
            ButtonIndex--;
        if (ButtonIndex >= seats.Count)
            ButtonIndex = seats.Count - 1;
    }

    public void MoveSeat(Seat seat, int offset)
    {
        if (HandInProgress)
            throw new InvalidOperationException("Can't change seating during a hand.");

        var from = seats.IndexOf(seat);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= seats.Count)
            return;

        seats.RemoveAt(from);
        seats.Insert(to, seat);
    }

    /// <summary>Adds gil to a player's stack (a rebuy). Negative amounts pay gil back out.</summary>
    public void AdjustChips(Seat seat, long amount)
    {
        if (HandInProgress && seat.InHand)
            throw new InvalidOperationException("Can't change the stack of a player who is in the hand.");
        if (seat.Stack + amount < 0)
            throw new InvalidOperationException($"{seat.Name} only has {seat.Stack:N0}.");

        seat.Stack += amount;
        seat.TotalBuyIn += amount;
    }

    public void StartHand()
    {
        if (HandInProgress)
            throw new InvalidOperationException("A hand is already in progress.");
        if (BigBlind <= 0 || SmallBlind < 0 || SmallBlind > BigBlind)
            throw new InvalidOperationException("Blinds must satisfy 0 <= small blind <= big blind, big blind > 0.");

        var eligible = seats.Count(s => !s.SittingOut && s.Stack > 0);
        if (eligible < 2)
            throw new InvalidOperationException("Need at least two players with chips who aren't sitting out.");

        foreach (var seat in seats)
        {
            seat.ResetHand();
            seat.InHand = !seat.SittingOut && seat.Stack > 0;
        }

        board.Clear();
        lastResults = new List<PotResult>();
        undo.Clear();
        LastAction = null;
        deck = deckFactory();
        handBigBlind = BigBlind;
        HandNumber++;
        Street = Street.Preflop;

        ButtonIndex = NextIndex(ButtonIndex, s => s.InHand);
        if (eligible == 2)
        {
            // Heads-up: the button posts the small blind and acts first preflop.
            SmallBlindIndex = ButtonIndex;
            BigBlindIndex = NextIndex(SmallBlindIndex, s => s.InHand);
        }
        else
        {
            SmallBlindIndex = NextIndex(ButtonIndex, s => s.InHand);
            BigBlindIndex = NextIndex(SmallBlindIndex, s => s.InHand);
        }

        Announce(MessageKind.HandStart, ("hand", $"{HandNumber}"), ("sb", SmallBlind), ("bb", BigBlind),
                 ("button", seats[ButtonIndex].Name), ("players", $"{eligible}"));
        PostBlind(seats[SmallBlindIndex], SmallBlind, MessageKind.SmallBlind);
        PostBlind(seats[BigBlindIndex], BigBlind, MessageKind.BigBlind);
        CurrentBet = BigBlind;
        MinRaise = BigBlind;

        for (var round = 0; round < 2; round++)
        {
            var index = ButtonIndex;
            for (var n = 0; n < eligible; n++)
            {
                index = NextIndex(index, s => s.InHand);
                seats[index].Hole.Add(deck.Draw());
            }
        }

        foreach (var seat in seats.Where(s => s.InHand))
            HoleCardsDealt?.Invoke(seat, seat.HoleCards);

        ProceedFrom(BigBlindIndex);
        Flush();
    }

    public ActionOptions GetOptions()
    {
        var seat = Actor ?? throw new InvalidOperationException("No one is waiting to act");
        var toCall = CurrentBet - seat.StreetBet;
        var max = seat.StreetBet + seat.Stack;
        var canRaise = seat.Stack > toCall && !(seat.HasActed && toCall > 0);
        return new ActionOptions(
            ToCall: toCall,
            CallAmount: Math.Min(toCall, seat.Stack),
            CanCheck: toCall == 0,
            CanRaise: canRaise,
            IsBet: CurrentBet == 0,
            MinRaiseTo: Math.Min(CurrentBet + MinRaise, max),
            MaxRaiseTo: max);
    }

    /// <summary>
    /// Applies an action for the player to act. For <see cref="PlayerAction.BetOrRaise"/>,
    /// <paramref name="raiseTo"/> is the player's total bet for this street ("raise to").
    /// </summary>
    public void Act(PlayerAction action, long raiseTo = 0)
    {
        var seat = Actor ?? throw new InvalidOperationException("No one is waiting to act");
        var snapshot = TakeSnapshot();
        var toCall = CurrentBet - seat.StreetBet;

        switch (action)
        {
            case PlayerAction.Fold:
                seat.Folded = true;
                Announce(MessageKind.Fold, ("player", seat.Name), ("stack", seat.Stack));
                break;

            case PlayerAction.Check:
                if (toCall > 0)
                    throw new InvalidOperationException($"{seat.Name} can't check; {toCall:N0} to call.");
                Announce(MessageKind.Check, ("player", seat.Name), ("stack", seat.Stack));
                break;

            case PlayerAction.Call:
                if (toCall <= 0)
                    throw new InvalidOperationException("Nothing to call; check instead.");
                Call(seat);
                break;

            case PlayerAction.BetOrRaise:
                RaiseTo(seat, raiseTo);
                break;

            case PlayerAction.AllIn:
                if (seat.StreetBet + seat.Stack <= CurrentBet)
                    Call(seat);
                else
                    RaiseTo(seat, seat.StreetBet + seat.Stack);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }

        seat.HasActed = true;
        LastAction = lastAnnounced;
        undo.Push(snapshot);
        ProceedFrom(ActorIndex);
        Flush();
    }

    /// <summary>Reverts the last action of the current hand (cards already dealt stay dealt).</summary>
    public void Undo()
    {
        if (!CanUndo)
            throw new InvalidOperationException("Nothing to undo.");

        var snapshot = undo.Pop();
        snapshot.Restore(this);
        Announce(MessageKind.Correction, ("player", Actor?.Name ?? "previous state"));
        Flush();
    }

    /// <summary>Misdeal: returns everyone's contributions and ends the hand.</summary>
    public void CancelHand()
    {
        if (!HandInProgress)
            throw new InvalidOperationException("No hand in progress.");

        foreach (var seat in seats)
        {
            seat.Stack += seat.Contributed;
            seat.Contributed = 0;
            seat.StreetBet = 0;
        }

        EndHand();
        Announce(MessageKind.HandCancelled, ("hand", $"{HandNumber}"));
        Flush();
    }

    /// <summary>A summary of the table for chat, one phrase per line.</summary>
    public IReadOnlyList<string> DescribeState()
    {
        var lines = new List<string>();
        if (!HandInProgress)
        {
            lines.Add("Waiting for next hand");
            lines.Add($"Blinds: {SmallBlind:N0}/{BigBlind:N0}");
            if (RakePercent > 0)
                lines.Add($"Rake: {RakePercent:0.##}%{(RakeCap > 0 ? $" (cap {RakeCap:N0})" : string.Empty)}");
            return lines;
        }

        lines.Add($"Hand #{HandNumber}");
        if (board.Count > 0)
            lines.Add($"{Street}: {Card.Join(board, SuitSymbols)}");
        lines.Add($"Pot: {Pot:N0}");
        if (Actor is { } actor)
            lines.AddRange(Messages.Format(PromptKind(actor), PromptValues(actor)));
        return lines;
    }

    private void PostBlind(Seat seat, long amount, MessageKind kind)
    {
        var posted = Math.Min(amount, seat.Stack);
        PutIn(seat, seat.StreetBet + posted);
        AnnounceAction(kind, seat, posted);
    }

    private void Call(Seat seat)
    {
        PutIn(seat, CurrentBet);
        AnnounceAction(MessageKind.Call, seat, seat.StreetBet);
    }

    private void RaiseTo(Seat seat, long target)
    {
        var max = seat.StreetBet + seat.Stack;
        var isBet = CurrentBet == 0;
        if (seat.HasActed && CurrentBet > seat.StreetBet)
            throw new InvalidOperationException("Betting wasn't reopened (short all-in); call or fold only.");
        if (target > max)
            throw new InvalidOperationException($"{seat.Name} only has {max:N0} available.");
        if (target <= CurrentBet)
            throw new InvalidOperationException($"Must {(isBet ? "bet" : "raise to")} more than {CurrentBet:N0}.");

        var size = target - CurrentBet;
        if (size < MinRaise && target < max)
            throw new InvalidOperationException($"Minimum {(isBet ? "bet" : "raise")} is to {CurrentBet + MinRaise:N0}.");

        PutIn(seat, target);
        if (size >= MinRaise)
        {
            // A full raise reopens the betting for everyone else.
            MinRaise = size;
            foreach (var other in seats.Where(s => s != seat && s.CanAct))
                other.HasActed = false;
        }

        CurrentBet = target;
        AnnounceAction(isBet ? MessageKind.Bet : MessageKind.Raise, seat, target);
    }

    private static void PutIn(Seat seat, long targetStreetBet)
    {
        var add = Math.Min(targetStreetBet - seat.StreetBet, seat.Stack);
        if (add <= 0)
            return;

        seat.Stack -= add;
        seat.StreetBet += add;
        seat.Contributed += add;
        if (seat.Stack == 0)
            seat.AllIn = true;
    }

    private bool NeedsAction(Seat seat)
    {
        if (!seat.CanAct)
            return false;
        if (seat.StreetBet < CurrentBet)
            return true;

        // Nobody left to bet against: a matched player doesn't need to act again.
        return !seat.HasActed && seats.Count(s => s.CanAct) > 1;
    }

    private void ProceedFrom(int lastIndex)
    {
        if (seats.Count(s => s.Contending) == 1)
        {
            AwardUncontested();
            return;
        }

        var next = NextIndexOrNone(lastIndex, NeedsAction);
        if (next >= 0)
        {
            ActorIndex = next;
            AnnouncePrompt(seats[next]);
            return;
        }

        EndStreet();
    }

    private void EndStreet()
    {
        foreach (var seat in seats)
        {
            seat.StreetBet = 0;
            seat.HasActed = false;
        }

        CurrentBet = 0;
        MinRaise = handBigBlind;

        var dealtFrom = board.Count;
        switch (Street)
        {
            case Street.Preflop:
                deck.Draw();
                board.AddRange([deck.Draw(), deck.Draw(), deck.Draw()]);
                Street = Street.Flop;
                break;
            case Street.Flop:
                deck.Draw();
                board.Add(deck.Draw());
                Street = Street.Turn;
                break;
            case Street.Turn:
                deck.Draw();
                board.Add(deck.Draw());
                Street = Street.River;
                break;
            default:
                Showdown();
                return;
        }

        var kind = Street switch { Street.Flop => MessageKind.Flop, Street.Turn => MessageKind.Turn, _ => MessageKind.River };
        Announce(kind, ("board", Card.Join(board, SuitSymbols)), ("cards", Card.Join(board.Skip(dealtFrom), SuitSymbols)),
                 ("pot", Pot), ("street", $"{Street}"), ("hand", $"{HandNumber}"));
        if (seats.Count(s => s.CanAct) > 1)
            dealtStreets.Add(Street);
        ProceedFrom(ButtonIndex);
    }

    /// <summary>The seat's best hand using the board so far, or null before the flop.</summary>
    public HandValue? CurrentHand(Seat seat) =>
        board.Count >= 3 && seat.HoleCards.Count == 2
            ? HandEvaluator.Evaluate(seat.HoleCards.Concat(board).ToList())
            : null;

    private void AwardUncontested()
    {
        var winner = seats.Single(s => s.Contending);

        // The part of the winner's bet nobody called is just handed back, so it isn't raked.
        var highestOther = seats.Where(s => s != winner).Select(s => s.Contributed).DefaultIfEmpty(0).Max();
        var uncalled = Math.Max(0, winner.Contributed - highestOther);
        var rake = TakeRake(Pot - uncalled);
        var amount = Pot - rake;

        winner.Stack += amount;
        lastResults = [new PotResult("Pot", amount, [winner], null, false, rake)];
        Announce(MessageKind.WinUncontested, ("player", winner.Name), ("amount", amount), ("rake", RakeSuffix(rake)));
        EndHand();
    }

    /// <summary>
    /// Works out this hand's rake on the contested amount, adds it to <see cref="HouseEarnings"/>, and
    /// returns it. Nothing is taken if the rake is off, or the hand ended before the flop with
    /// <see cref="NoFlopNoDrop"/> on.
    /// </summary>
    private long TakeRake(long contested)
    {
        if (RakePercent <= 0 || contested <= 0 || (NoFlopNoDrop && board.Count == 0))
            return 0;

        var rake = (long)Math.Floor(contested * RakePercent / 100m);
        if (RakeCap > 0)
            rake = Math.Min(rake, RakeCap);

        HouseEarnings += rake;
        return rake;
    }

    private static string RakeSuffix(long rake) => rake > 0 ? $" (rake {rake:N0})" : string.Empty;

    private void Showdown()
    {
        var contenders = seats.Where(s => s.Contending).ToList();
        var values = contenders.ToDictionary(s => s, s => HandEvaluator.Evaluate(s.HoleCards.Concat(board).ToList()));
        foreach (var seat in contenders)
        {
            // When two hands read the same (both "Pair of Eights"), show kickers so players can see why.
            var value = values[seat];
            var lookalike = contenders.Any(o => o != seat && values[o].Describe() == value.Describe());
            Announce(MessageKind.Shows, ("player", seat.Name), ("cards", Card.Join(seat.HoleCards, SuitSymbols)),
                     ("hand", lookalike ? value.DescribeWithKickers() : value.Describe()));
        }

        var results = new List<PotResult>();
        var pots = BuildPots();

        // The rake is worked out on all contested pots together and taken from the main pot first.
        var rakeLeft = TakeRake(pots.Where(p => p.Eligible.Count > 1).Sum(p => p.Amount));
        for (var i = 0; i < pots.Count; i++)
        {
            var (gross, eligible) = pots[i];
            if (eligible.Count == 1)
            {
                // No other live player could match this level (usually an uncalled bet above an all-in).
                eligible[0].Stack += gross;
                results.Add(new PotResult($"Side pot {i}", gross, eligible, null, true, 0));
                Announce(MessageKind.PotReturned, ("amount", gross), ("player", eligible[0].Name));
                continue;
            }

            var rake = Math.Min(rakeLeft, gross);
            rakeLeft -= rake;
            var amount = gross - rake;

            var best = eligible.Max(s => values[s].Score);
            var winners = OrderFromButton(eligible.Where(s => values[s].Score == best)).ToList();
            var share = amount / winners.Count;
            var oddChips = amount % winners.Count;
            for (var w = 0; w < winners.Count; w++)
                winners[w].Stack += share + (w < oddChips ? 1 : 0);

            var label = i == 0 ? "Main pot" : $"Side pot {i}";
            var hand = values[winners[0]].Describe();
            if (winners.Count == 1)
            {
                var runnerUp = eligible.Where(s => s != winners[0]).Select(s => values[s]).MaxBy(v => v.Score);
                if (values[winners[0]].DecidingKicker(runnerUp) is { } kicker)
                    hand += $", {kicker}";
            }

            results.Add(new PotResult(label, amount, winners, hand, false, rake));
            Announce(winners.Count == 1 ? MessageKind.PotWon : MessageKind.PotSplit, ("pot_name", label), ("amount", amount),
                     ("player", string.Join(", ", winners.Select(w => w.Name))), ("hand", hand), ("rake", RakeSuffix(rake)));
        }

        lastResults = results;
        EndHand();
    }

    /// <summary>Splits contributions into main and side pots by all-in level.</summary>
    private List<(long Amount, List<Seat> Eligible)> BuildPots()
    {
        var pots = new List<(long, List<Seat>)>();
        var levels = seats.Where(s => s.Contending && s.Contributed > 0)
            .Select(s => s.Contributed).Distinct().Order().ToList();

        long previous = 0;
        foreach (var level in levels)
        {
            var amount = seats.Sum(s => Math.Clamp(s.Contributed - previous, 0, level - previous));
            var eligible = seats.Where(s => s.Contending && s.Contributed >= level).ToList();
            pots.Add((amount, eligible));
            previous = level;
        }

        // Chips from folded players above the highest live level go to the last pot.
        var leftover = seats.Sum(s => Math.Max(0, s.Contributed - previous));
        if (leftover > 0 && pots.Count > 0)
            pots[^1] = (pots[^1].Item1 + leftover, pots[^1].Item2);

        return pots;
    }

    private IEnumerable<Seat> OrderFromButton(IEnumerable<Seat> group)
    {
        var set = group.ToHashSet();
        for (var n = 1; n <= seats.Count; n++)
        {
            var seat = seats[(ButtonIndex + n) % seats.Count];
            if (set.Contains(seat))
                yield return seat;
        }
    }

    private void EndHand()
    {
        foreach (var seat in seats)
        {
            seat.StreetBet = 0;
            seat.Contributed = 0;
            seat.HasActed = false;
        }

        CurrentBet = 0;
        ActorIndex = -1;
        Street = Street.HandOver;
        undo.Clear();
    }

    private void AnnouncePrompt(Seat seat) => Announce(PromptKind(seat), PromptValues(seat));

    private MessageKind PromptKind(Seat seat) =>
        CurrentBet > seat.StreetBet ? MessageKind.ActionToCall : MessageKind.ActionToCheck;

    private Dictionary<string, string> PromptValues(Seat seat) => Values(
        ("player", seat.Name), ("amount", Math.Min(CurrentBet - seat.StreetBet, seat.Stack)), ("stack", seat.Stack), ("pot", Pot));

    private void AnnounceAction(MessageKind kind, Seat seat, long amount) => Announce(kind,
        ("player", seat.Name), ("amount", amount), ("allin", seat.AllIn ? " (all-in)" : string.Empty), ("stack", seat.Stack));

    private int NextIndex(int from, Func<Seat, bool> predicate)
    {
        var index = NextIndexOrNone(from, predicate);
        return index >= 0 ? index : throw new InvalidOperationException("No matching seat.");
    }

    /// <summary>First seat after <paramref name="from"/> (wrapping, ending with itself) that matches.</summary>
    private int NextIndexOrNone(int from, Func<Seat, bool> predicate)
    {
        for (var n = 1; n <= seats.Count; n++)
        {
            var index = (((from + n) % seats.Count) + seats.Count) % seats.Count;
            if (predicate(seats[index]))
                return index;
        }

        return -1;
    }

    private void Announce(MessageKind kind, params (string Key, object Value)[] values) => Announce(kind, Values(values));

    /// <summary>Queues the event's lines in the dealer's wording, and remembers it in the built-in wording for the UI.</summary>
    private void Announce(MessageKind kind, Dictionary<string, string> values)
    {
        pending.AddRange(Messages.Format(kind, values));
        lastAnnounced = MessageTemplates.FormatDefault(kind, values);
    }

    private static Dictionary<string, string> Values(params (string Key, object Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value is long n ? n.ToString("N0") : v.Value.ToString() ?? string.Empty);

    // Announcements go out before street events, so players see the board before their private tell.
    private void Flush()
    {
        Sequence++;
        if (pending.Count > 0)
        {
            var lines = pending.ToArray();
            pending.Clear();
            Announced?.Invoke(lines);
        }

        var streets = dealtStreets.ToArray();
        dealtStreets.Clear();
        foreach (var street in streets)
            StreetDealt?.Invoke(street);
    }

    private Snapshot TakeSnapshot() => new(
        seats.Select(s => new SeatState(s.Stack, s.StreetBet, s.Contributed, s.Folded, s.AllIn, s.HasActed)).ToArray(),
        board.ToList(),
        deck.Cards.ToList(),
        Street,
        ActorIndex,
        CurrentBet,
        MinRaise,
        LastAction);

    private sealed record SeatState(long Stack, long StreetBet, long Contributed, bool Folded, bool AllIn, bool HasActed);

    private sealed record Snapshot(
        SeatState[] Seats,
        List<Card> Board,
        List<Card> Deck,
        Street Street,
        int ActorIndex,
        long CurrentBet,
        long MinRaise,
        string? LastAction)
    {
        public void Restore(Table table)
        {
            for (var i = 0; i < Seats.Length; i++)
            {
                var seat = table.seats[i];
                var state = Seats[i];
                seat.Stack = state.Stack;
                seat.StreetBet = state.StreetBet;
                seat.Contributed = state.Contributed;
                seat.Folded = state.Folded;
                seat.AllIn = state.AllIn;
                seat.HasActed = state.HasActed;
            }

            table.board.Clear();
            table.board.AddRange(Board);
            table.deck = new Deck(Deck);
            table.Street = Street;
            table.ActorIndex = ActorIndex;
            table.CurrentBet = CurrentBet;
            table.MinRaise = MinRaise;
            table.LastAction = LastAction;
        }
    }
}
