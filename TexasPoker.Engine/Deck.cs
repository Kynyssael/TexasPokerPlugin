using System.Security.Cryptography;

namespace TexasPoker.Engine;

/// <summary>A deck of cards; index 0 is the top card.</summary>
public sealed class Deck
{
    private readonly List<Card> cards;

    /// <summary>Creates a freshly shuffled 52-card deck.</summary>
    public Deck()
        : this(Shuffled())
    {
    }

    /// <summary>Creates a deck with a fixed order (top card first).</summary>
    public Deck(IEnumerable<Card> order)
    {
        cards = order.ToList();
    }

    public int Remaining => cards.Count;

    internal IReadOnlyList<Card> Cards => cards;

    public Card Draw()
    {
        if (cards.Count == 0)
            throw new InvalidOperationException("The deck is empty.");

        var card = cards[0];
        cards.RemoveAt(0);
        return card;
    }

    public static IEnumerable<Card> Ordered()
    {
        foreach (var suit in Enum.GetValues<Suit>())
        {
            for (var rank = 2; rank <= 14; rank++)
                yield return new Card(rank, suit);
        }
    }

    /// <summary>Fisher-Yates shuffle using a cryptographic RNG so the deal can't be predicted.</summary>
    public static List<Card> Shuffled()
    {
        var list = Ordered().ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
