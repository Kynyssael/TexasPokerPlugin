namespace TexasPoker.Engine;

public enum Suit
{
    Clubs,
    Diamonds,
    Hearts,
    Spades,
}

/// <summary>A playing card. Rank runs from 2 to 14 (ace high).</summary>
public readonly record struct Card(int Rank, Suit Suit)
{
    public bool IsRed => Suit is Suit.Hearts or Suit.Diamonds;

    public string RankText => Rank switch
    {
        14 => "A",
        13 => "K",
        12 => "Q",
        11 => "J",
        _ => Rank.ToString(),
    };

    public string SuitText(bool symbols) => (symbols, Suit) switch
    {
        (true, Suit.Clubs) => "♣",
        (true, Suit.Diamonds) => "♦",
        (true, Suit.Hearts) => "♥",
        (true, Suit.Spades) => "♠",
        (false, Suit.Clubs) => "c",
        (false, Suit.Diamonds) => "d",
        (false, Suit.Hearts) => "h",
        _ => "s",
    };

    public string ToText(bool symbols) => RankText + SuitText(symbols);

    public override string ToString() => ToText(false);

    /// <summary>Formats cards as "[A♠][10♥]" (or "[As][10h]") for chat.</summary>
    public static string Join(IEnumerable<Card> cards, bool symbols) =>
        string.Concat(cards.Select(c => $"[{c.ToText(symbols)}]"));

    public static string RankName(int rank, bool plural = false)
    {
        var name = rank switch
        {
            14 => "Ace",
            13 => "King",
            12 => "Queen",
            11 => "Jack",
            10 => "Ten",
            9 => "Nine",
            8 => "Eight",
            7 => "Seven",
            6 => "Six",
            5 => "Five",
            4 => "Four",
            3 => "Three",
            2 => "Two",
            _ => throw new ArgumentOutOfRangeException(nameof(rank)),
        };
        return !plural ? name : rank == 6 ? "Sixes" : name + "s";
    }

    /// <summary>Parses short notation such as "As", "10h", "Td" (used by tests).</summary>
    public static Card Parse(string text)
    {
        text = text.Trim();
        var suit = char.ToLowerInvariant(text[^1]) switch
        {
            'c' => Suit.Clubs,
            'd' => Suit.Diamonds,
            'h' => Suit.Hearts,
            's' => Suit.Spades,
            _ => throw new FormatException($"Unknown suit in '{text}'."),
        };
        var rank = text[..^1].ToUpperInvariant() switch
        {
            "A" => 14,
            "K" => 13,
            "Q" => 12,
            "J" => 11,
            "T" => 10,
            var r when int.TryParse(r, out var n) && n is >= 2 and <= 10 => n,
            _ => throw new FormatException($"Unknown rank in '{text}'."),
        };
        return new Card(rank, suit);
    }

    public static List<Card> ParseMany(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();
}
