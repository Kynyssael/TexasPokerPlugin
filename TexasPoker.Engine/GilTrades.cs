namespace TexasPoker.Engine;

/// <summary>
/// A finished in-game trade where gil changed hands. <see cref="Amount"/> is above 0 when they gave you gil,
/// below 0 when you gave them gil.
/// </summary>
public sealed class GilTrade(string name, string world, long amount, DateTime at)
{
    public string Name { get; } = name;

    public string World { get; } = world;

    public long Amount { get; } = amount;

    public DateTime At { get; } = at;

    public string DisplayName => World.Length > 0 ? $"{Name}@{World}" : Name;

    /// <summary>The dealer asked for it to be added to the stack once the player's hand is over.</summary>
    public bool AddWhenHandEnds { get; set; }
}

/// <summary>What a trade meant for the table, for the dealer's notice and the session log.</summary>
public sealed record TradeResult(string Message, bool Warning);

/// <summary>
/// Matches gil trades against what the table expects to change hands, so a trade for a buy-in the dealer already
/// entered isn't counted twice. Seating someone, a rebuy or an add means "collect this much"; a cash-out or take-out
/// means "pay this much". A trade that settles one is only noted. Gil received for nothing expected waits for the
/// dealer to add it to a stack, seat the player with it, or ignore it. Gil paid for nothing expected is a warning.
/// A trade carries at most 1,000,000 gil, so bigger amounts arrive in parts: what's left stays expected.
/// </summary>
public sealed class GilTrades
{
    /// <summary>How long an expected collection or payout is remembered.</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(30);

    private readonly List<GilTrade> waiting = [];

    // Per player name: above 0, gil still to collect from them; below 0, gil still to pay them.
    private readonly Dictionary<string, (long Amount, DateTime At)> expected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gil received that no buy-in explains, waiting for the dealer.</summary>
    public IReadOnlyList<GilTrade> Waiting => waiting;

    /// <summary>
    /// The table says this much gil changes hands with the player: above 0 you collect it, below 0 you pay it.
    /// Adds to anything already expected.
    /// </summary>
    public void Expect(string name, long amount, DateTime now)
    {
        Forget(now);
        if (amount == 0)
            return;

        var total = amount + (expected.TryGetValue(name, out var old) ? old.Amount : 0);
        if (total == 0)
            expected.Remove(name);
        else
            expected[name] = (total, now);
    }

    /// <summary>What's still expected with the player (see <see cref="Expect"/>), or 0.</summary>
    public long ExpectedWith(string name, DateTime now)
    {
        Forget(now);
        return expected.TryGetValue(name, out var e) ? e.Amount : 0;
    }

    /// <summary>Takes a finished trade into account and says what it meant.</summary>
    public TradeResult Settle(GilTrade trade)
    {
        Forget(trade.At);
        var amount = trade.Amount;
        var owed = expected.TryGetValue(trade.Name, out var e) ? e.Amount : 0;
        var given = amount > 0;
        var verb = given ? $"{trade.DisplayName} gave you {amount:N0} gil" : $"You gave {trade.DisplayName} {-amount:N0} gil";

        // Nothing expected the same way: received gil waits for the dealer, paid gil is a warning.
        if (owed == 0 || Math.Sign(owed) != Math.Sign(amount))
        {
            if (given)
            {
                waiting.Add(trade);
                return new TradeResult($"{verb}. Add it to their stack, seat them with it, or ignore it.", Warning: false);
            }

            return new TradeResult($"{verb}, but no cash-out is waiting for them. If it was one, click Cash out on their row.", Warning: true);
        }

        var left = owed - amount;
        if (left == 0)
        {
            expected.Remove(trade.Name);
            return new TradeResult(given ? $"{verb}: their buy-in is collected." : $"{verb}: their cash-out is paid.", Warning: false);
        }

        // Still more to come: a trade carries at most 1,000,000 gil.
        if (Math.Sign(left) == Math.Sign(owed))
        {
            expected[trade.Name] = (left, trade.At);
            return new TradeResult(given ? $"{verb}. {left:N0} still to collect." : $"{verb}. {-left:N0} still to pay.", Warning: false);
        }

        // More than expected.
        expected.Remove(trade.Name);
        if (given)
        {
            var extra = new GilTrade(trade.Name, trade.World, -left, trade.At);
            waiting.Add(extra);
            return new TradeResult($"{verb}: their buy-in is collected, with {-left:N0} more. Add the extra to their stack, or ignore it.",
                                   Warning: false);
        }

        return new TradeResult($"{verb}, but the table said {owed * -1:N0}: {left:N0} too much.", Warning: true);
    }

    /// <summary>The dealer dealt with a waiting trade (added it, seated the player, or ignored it).</summary>
    public void Dismiss(GilTrade trade) => waiting.Remove(trade);

    /// <summary>Puts a trade back to wait for the dealer (an automatic add was undone).</summary>
    public void Hold(GilTrade trade)
    {
        trade.AddWhenHandEnds = false;
        if (!waiting.Contains(trade))
            waiting.Add(trade);
    }

    private void Forget(DateTime now)
    {
        foreach (var name in expected.Where(e => now - e.Value.At > Memory).Select(e => e.Key).ToList())
            expected.Remove(name);
    }
}
