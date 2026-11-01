namespace TexasPoker.Engine;

/// <summary>A player sitting at the table, with their gil ledger and per-hand state.</summary>
public sealed class Seat
{
    internal readonly List<Card> Hole = new(2);

    public Seat(string name, string world, long buyIn)
    {
        Name = name;
        World = world;
        Stack = buyIn;
        TotalBuyIn = buyIn;
    }

    public string Name { get; }

    /// <summary>Home world, used to address /tells. May be empty for same-world players.</summary>
    public string World { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(World) ? Name : $"{Name}@{World}";

    public long Stack { get; internal set; }

    /// <summary>Total gil this player has handed to the dealer this session.</summary>
    public long TotalBuyIn { get; internal set; }

    /// <summary>Stack minus buy-ins: what the player is up or down. Only meaningful between hands.</summary>
    public long Net => Stack - TotalBuyIn;

    public bool SittingOut { get; set; }

    public IReadOnlyList<Card> HoleCards => Hole;

    public long StreetBet { get; internal set; }

    public long Contributed { get; internal set; }

    public bool InHand { get; internal set; }

    public bool Folded { get; internal set; }

    public bool AllIn { get; internal set; }

    /// <summary>
    /// Whether the player has acted since the last full bet or raise. A player who has acted and then
    /// faces only a short all-in raise may call or fold but not re-raise.
    /// </summary>
    public bool HasActed { get; internal set; }

    public bool Contending => InHand && !Folded;

    public bool CanAct => InHand && !Folded && !AllIn;

    internal void ResetHand()
    {
        Hole.Clear();
        StreetBet = 0;
        Contributed = 0;
        InHand = false;
        Folded = false;
        AllIn = false;
        HasActed = false;
    }
}
