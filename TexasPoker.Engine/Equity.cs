namespace TexasPoker.Engine;

/// <summary>A player's chances in a hand: the share of run-outs they win alone, and the share they split.</summary>
/// <param name="Outs">On the turn, for a player behind: the river cards that make them win outright.</param>
/// <param name="OutsTo">What most of those outs make, e.g. a flush.</param>
public sealed record PlayerEquity(double Win, double Tie, int Outs, HandCategory? OutsTo);

/// <summary>
/// Win and tie chances for the players still in a hand, for the commentator view. Every run-out of the board is
/// counted exactly once two cards or fewer are to come (from the flop on); before the flop a random sample of
/// run-outs is used, which is accurate to well under one percent.
/// </summary>
public static class Equity
{
    /// <summary>Run-outs sampled before the flop: about ±0.5% at worst.</summary>
    public const int PreflopSamples = 30_000;

    /// <param name="holes">Each live player's two hole cards.</param>
    /// <param name="board">The board so far: 0, 3, 4 or 5 cards.</param>
    /// <param name="seed">For tests: the same sample every time.</param>
    public static IReadOnlyList<PlayerEquity> Calculate(IReadOnlyList<IReadOnlyList<Card>> holes, IReadOnlyList<Card> board, int? seed = null)
    {
        var n = holes.Count;
        if (n == 0)
            return [];
        var used = new HashSet<Card>(board);
        foreach (var hole in holes)
            used.UnionWith(hole);
        var deck = Deck.Ordered().Where(c => !used.Contains(c)).ToArray();
        var toCome = 5 - board.Count;

        var wins = new double[n];
        var ties = new double[n];
        var scores = new int[n];
        var cards = new Card[7];
        var runs = 0;

        void Score(ReadOnlySpan<Card> extra)
        {
            var best = int.MinValue;
            for (var p = 0; p < n; p++)
            {
                cards[0] = holes[p][0];
                cards[1] = holes[p][1];
                for (var i = 0; i < board.Count; i++)
                    cards[2 + i] = board[i];
                for (var i = 0; i < extra.Length; i++)
                    cards[2 + board.Count + i] = extra[i];
                scores[p] = FastScore(cards.AsSpan(0, 2 + board.Count + extra.Length));
                best = Math.Max(best, scores[p]);
            }

            var count = 0;
            for (var p = 0; p < n; p++)
                if (scores[p] == best)
                    count++;
            for (var p = 0; p < n; p++)
            {
                if (scores[p] != best)
                    continue;
                if (count == 1)
                    wins[p]++;
                else
                    ties[p]++;
            }

            runs++;
        }

        if (toCome <= 2)
        {
            Span<Card> extra = stackalloc Card[toCome];
            if (toCome == 0)
                Score(extra);
            else if (toCome == 1)
                foreach (var a in deck)
                {
                    extra[0] = a;
                    Score(extra);
                }
            else
                for (var i = 0; i < deck.Length; i++)
                    for (var j = i + 1; j < deck.Length; j++)
                    {
                        extra[0] = deck[i];
                        extra[1] = deck[j];
                        Score(extra);
                    }
        }
        else
        {
            var random = seed is { } s ? new Random(s) : Random.Shared;
            var pool = deck.ToArray();
            Span<Card> extra = stackalloc Card[toCome];
            for (var k = 0; k < PreflopSamples; k++)
            {
                // A partial shuffle: the first toCome cards are a random run-out.
                for (var i = 0; i < toCome; i++)
                {
                    var j = random.Next(i, pool.Length);
                    (pool[i], pool[j]) = (pool[j], pool[i]);
                    extra[i] = pool[i];
                }

                Score(extra);
            }
        }

        var outs = toCome == 1 ? Outs(holes, board, deck) : null;
        return Enumerable.Range(0, n)
            .Select(p => new PlayerEquity(wins[p] / runs, ties[p] / runs, outs?[p].Count ?? 0, outs?[p].To))
            .ToList();
    }

    /// <summary>
    /// On the turn: for each player who isn't ahead now (with four board cards), the river cards that make them win
    /// outright, and what most of those make.
    /// </summary>
    private static (int Count, HandCategory? To)[] Outs(IReadOnlyList<IReadOnlyList<Card>> holes, IReadOnlyList<Card> board, Card[] deck)
    {
        var n = holes.Count;
        var result = new (int, HandCategory?)[n];
        var cards = new Card[7];

        int ScoreOf(int p, Card? river)
        {
            cards[0] = holes[p][0];
            cards[1] = holes[p][1];
            for (var i = 0; i < board.Count; i++)
                cards[2 + i] = board[i];
            var count = 2 + board.Count;
            if (river is { } r)
                cards[count++] = r;
            return FastScore(cards.AsSpan(0, count));
        }

        var now = Enumerable.Range(0, n).Select(p => ScoreOf(p, null)).ToArray();
        var leader = now.Max();
        for (var p = 0; p < n; p++)
        {
            if (now[p] == leader && now.Count(s => s == leader) == 1)
                continue; // ahead already: no outs to count
            var made = new Dictionary<HandCategory, int>();
            var count = 0;
            foreach (var river in deck)
            {
                var mine = ScoreOf(p, river);
                var beaten = true;
                for (var q = 0; q < n && beaten; q++)
                    if (q != p && ScoreOf(q, river) >= mine)
                        beaten = false;
                if (!beaten)
                    continue;
                count++;
                var category = (HandCategory)(mine >> 20);
                made[category] = made.GetValueOrDefault(category) + 1;
            }

            result[p] = (count, count > 0 ? made.MaxBy(kv => kv.Value).Key : null);
        }

        return result;
    }

    /// <summary>
    /// The score of the best five-card hand in 5 to 7 cards, the same number <see cref="HandEvaluator.Evaluate"/>
    /// gives (category, then up to five ranks, four bits each), worked out without trying every five.
    /// </summary>
    public static int FastScore(ReadOnlySpan<Card> cards)
    {
        Span<int> counts = stackalloc int[15];
        Span<int> suitMask = stackalloc int[4];
        Span<int> suitCount = stackalloc int[4];
        var mask = 0;
        foreach (var c in cards)
        {
            counts[c.Rank]++;
            var s = (int)c.Suit;
            suitMask[s] |= 1 << c.Rank;
            suitCount[s]++;
            mask |= 1 << c.Rank;
        }

        static int Pack(HandCategory category, ReadOnlySpan<int> ranks)
        {
            var score = (int)category;
            for (var i = 0; i < 5; i++)
                score = (score << 4) | (i < ranks.Length ? ranks[i] : 0);
            return score;
        }

        static int StraightTop(int m)
        {
            for (var top = 14; top >= 6; top--)
                if (((m >> (top - 4)) & 0x1F) == 0x1F)
                    return top;
            // A-2-3-4-5: the ace (bit 14) plays low.
            return (m & 0b111100) == 0b111100 && (m & (1 << 14)) != 0 ? 5 : 0;
        }

        Span<int> ranks = stackalloc int[5];
        for (var s = 0; s < 4; s++)
        {
            if (suitCount[s] < 5)
                continue;
            var sf = StraightTop(suitMask[s]);
            if (sf > 0)
                return Pack(HandCategory.StraightFlush, [sf]);
            // Quads and full houses beat a flush: checked below before using it.
            var flush = 0;
            for (var r = 14; r >= 2 && flush < 5; r--)
                if ((suitMask[s] & (1 << r)) != 0)
                    ranks[flush++] = r;
            var quadsOrBoat = false;
            int trips = 0, pairs = 0;
            for (var r = 14; r >= 2; r--)
            {
                if (counts[r] == 4)
                    quadsOrBoat = true;
                else if (counts[r] == 3)
                    trips++;
                else if (counts[r] == 2)
                    pairs++;
            }

            if (trips >= 2 || (trips == 1 && pairs >= 1))
                quadsOrBoat = true;
            if (!quadsOrBoat)
                return Pack(HandCategory.Flush, ranks);
        }

        int quad = 0, trip = 0, trip2 = 0, pair1 = 0, pair2 = 0;
        for (var r = 14; r >= 2; r--)
        {
            switch (counts[r])
            {
                case 4:
                    quad = quad == 0 ? r : quad;
                    break;
                case 3:
                    if (trip == 0)
                        trip = r;
                    else if (trip2 == 0)
                        trip2 = r;
                    break;
                case 2:
                    if (pair1 == 0)
                        pair1 = r;
                    else if (pair2 == 0)
                        pair2 = r;
                    break;
            }
        }

        static int KickerOf(ReadOnlySpan<int> counts, int skipA, int skipB, int skipC)
        {
            for (var r = 14; r >= 2; r--)
                if (counts[r] > 0 && r != skipA && r != skipB && r != skipC)
                    return r;
            return 0;
        }

        if (quad > 0)
            return Pack(HandCategory.FourOfAKind, [quad, KickerOf(counts, quad, 0, 0)]);
        if (trip > 0 && (trip2 > 0 || pair1 > 0))
            return Pack(HandCategory.FullHouse, [trip, Math.Max(trip2, pair1)]);
        // A flush that wasn't beaten by quads or a full house was returned above.
        var straight = StraightTop(mask);
        if (straight > 0)
            return Pack(HandCategory.Straight, [straight]);
        if (trip > 0)
        {
            var k1 = KickerOf(counts, trip, 0, 0);
            return Pack(HandCategory.ThreeOfAKind, [trip, k1, KickerOf(counts, trip, k1, 0)]);
        }

        if (pair1 > 0 && pair2 > 0)
            return Pack(HandCategory.TwoPair, [pair1, pair2, KickerOf(counts, pair1, pair2, 0)]);
        if (pair1 > 0)
        {
            var k1 = KickerOf(counts, pair1, 0, 0);
            var k2 = KickerOf(counts, pair1, k1, 0);
            return Pack(HandCategory.OnePair, [pair1, k1, k2, KickerOf(counts, pair1, k1, k2)]);
        }

        var high = 0;
        for (var r = 14; r >= 2 && high < 5; r--)
            if (counts[r] > 0)
                ranks[high++] = r;
        return Pack(HandCategory.HighCard, ranks);
    }
}
