namespace TexasPoker.Engine;

public enum HandCategory
{
    HighCard,
    OnePair,
    TwoPair,
    ThreeOfAKind,
    Straight,
    Flush,
    FullHouse,
    FourOfAKind,
    StraightFlush,
}

/// <summary>
/// The strength of a five-card hand. A higher <see cref="Score"/> wins; equal scores split.
/// </summary>
public readonly record struct HandValue(HandCategory Category, int Score, IReadOnlyList<int> Ranks) : IComparable<HandValue>
{
    public int CompareTo(HandValue other) => Score.CompareTo(other.Score);

    public string Describe() => Category switch
    {
        HandCategory.HighCard => $"High card {Card.RankName(Ranks[0])}",
        HandCategory.OnePair => $"Pair of {Card.RankName(Ranks[0], true)}",
        HandCategory.TwoPair => $"Two pair, {Card.RankName(Ranks[0], true)} and {Card.RankName(Ranks[1], true)}",
        HandCategory.ThreeOfAKind => $"Three of a kind, {Card.RankName(Ranks[0], true)}",
        HandCategory.Straight => $"Straight, {Card.RankName(Ranks[0])} high",
        HandCategory.Flush => $"Flush, {Card.RankName(Ranks[0])} high",
        HandCategory.FullHouse => $"Full house, {Card.RankName(Ranks[0], true)} full of {Card.RankName(Ranks[1], true)}",
        HandCategory.FourOfAKind => $"Four of a kind, {Card.RankName(Ranks[0], true)}",
        HandCategory.StraightFlush when Ranks[0] == 14 => "Royal flush",
        HandCategory.StraightFlush => $"Straight flush, {Card.RankName(Ranks[0])} high",
        _ => Category.ToString(),
    };

    /// <summary>The description plus the remaining cards, e.g. "Pair of Eights, kickers K Q 10".</summary>
    public string DescribeWithKickers()
    {
        var kickers = Ranks.Skip(MainRankCount).ToList();
        if (kickers.Count == 0)
            return Describe();

        var cards = string.Join(" ", kickers.Select(r => new Card(r, Suit.Spades).RankText));
        return $"{Describe()}, {(kickers.Count == 1 ? "kicker" : "kickers")} {cards}";
    }

    /// <summary>
    /// When this hand beats <paramref name="other"/> but they read the same (e.g. both "Pair of
    /// Eights"), names the card that decided it, e.g. "King kicker". Otherwise null.
    /// </summary>
    public string? DecidingKicker(HandValue other)
    {
        if (Score <= other.Score || Describe() != other.Describe())
            return null;

        for (var i = 0; i < Ranks.Count && i < other.Ranks.Count; i++)
        {
            if (Ranks[i] != other.Ranks[i])
                return $"{Card.RankName(Ranks[i])} kicker";
        }

        return null;
    }

    // How many leading ranks the description already names; the rest are kickers.
    private int MainRankCount => Category is HandCategory.TwoPair or HandCategory.FullHouse ? 2 : 1;
}

public static class HandEvaluator
{
    /// <summary>Returns the best five-card hand out of 5 to 7 cards.</summary>
    public static HandValue Evaluate(IReadOnlyList<Card> cards)
    {
        if (cards.Count is < 5 or > 7)
            throw new ArgumentException("Need between 5 and 7 cards.", nameof(cards));

        HandValue? best = null;
        var hand = new Card[5];
        foreach (var combo in Combinations(cards.Count, 5))
        {
            for (var i = 0; i < 5; i++)
                hand[i] = cards[combo[i]];

            var value = Evaluate5(hand);
            if (best is null || value.Score > best.Value.Score)
                best = value;
        }

        return best!.Value;
    }

    private static HandValue Evaluate5(Card[] hand)
    {
        var ranks = hand.Select(c => c.Rank).OrderByDescending(r => r).ToArray();
        var flush = hand.All(c => c.Suit == hand[0].Suit);
        var straightTop = StraightTop(ranks);

        // Rank groups ordered by size, then rank: e.g. a full house K-K-K-5-5 gives [K, 5].
        var groups = ranks.GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .Select(g => (Rank: g.Key, Count: g.Count()))
            .ToList();
        var groupRanks = groups.Select(g => g.Rank).ToArray();

        if (flush && straightTop > 0)
            return Make(HandCategory.StraightFlush, [straightTop]);
        if (groups[0].Count == 4)
            return Make(HandCategory.FourOfAKind, groupRanks);
        if (groups[0].Count == 3 && groups[1].Count == 2)
            return Make(HandCategory.FullHouse, groupRanks);
        if (flush)
            return Make(HandCategory.Flush, ranks);
        if (straightTop > 0)
            return Make(HandCategory.Straight, [straightTop]);
        if (groups[0].Count == 3)
            return Make(HandCategory.ThreeOfAKind, groupRanks);
        if (groups[0].Count == 2 && groups[1].Count == 2)
            return Make(HandCategory.TwoPair, groupRanks);
        if (groups[0].Count == 2)
            return Make(HandCategory.OnePair, groupRanks);
        return Make(HandCategory.HighCard, ranks);
    }

    /// <summary>Top card of a straight in descending ranks, 5 for the A-2-3-4-5 wheel, or 0.</summary>
    private static int StraightTop(int[] desc)
    {
        if (desc.Distinct().Count() != 5)
            return 0;
        if (desc[0] - desc[4] == 4)
            return desc[0];
        if (desc[0] == 14 && desc[1] == 5)
            return 5;
        return 0;
    }

    // Packs category and up to five tiebreak ranks (4 bits each) into one comparable integer.
    private static HandValue Make(HandCategory category, int[] ranks)
    {
        var score = (int)category;
        for (var i = 0; i < 5; i++)
            score = (score << 4) | (i < ranks.Length ? ranks[i] : 0);
        return new HandValue(category, score, ranks);
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return idx;

            var i = k - 1;
            while (i >= 0 && idx[i] == n - k + i)
                i--;
            if (i < 0)
                yield break;

            idx[i]++;
            for (var j = i + 1; j < k; j++)
                idx[j] = idx[j - 1] + 1;
        }
    }
}
