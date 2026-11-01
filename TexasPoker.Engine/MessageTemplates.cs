using System;
using System.Collections.Generic;
using System.Linq;

namespace TexasPoker.Engine;

/// <summary>Every kind of line the table announces. Each one has its own editable template.</summary>
public enum MessageKind
{
    HandStart,
    SmallBlind,
    BigBlind,
    Fold,
    Check,
    Call,
    Bet,
    Raise,
    ActionToCall,
    ActionToCheck,
    Flop,
    Turn,
    River,
    Shows,
    PotWon,
    PotSplit,
    PotReturned,
    WinUncontested,
    Correction,
    HandCancelled,
}

/// <summary>A message's default text, a name for the editor, and the placeholders it can use.</summary>
public sealed record MessageInfo(MessageKind Kind, string Group, string Title, string Default, IReadOnlyList<string> Placeholders);

/// <summary>
/// The wording of table announcements. Each template can hold several lines (one chat message each),
/// with {placeholders} filled in from the table. An empty template sends nothing for that event.
/// </summary>
public sealed class MessageTemplates
{
    private static readonly string[] ActionKeys = ["player", "amount", "allin", "stack"];
    private static readonly string[] StreetKeys = ["board", "cards", "pot", "street", "hand"];
    private static readonly string[] PotKeys = ["pot_name", "amount", "player", "hand", "rake"];

    public static readonly IReadOnlyList<MessageInfo> All =
    [
        new(MessageKind.HandStart, "Hand start", "New hand", "Hand #{hand}\nBlinds: {sb}/{bb}\nButton: {button}",
            ["hand", "sb", "bb", "button", "players"]),
        new(MessageKind.SmallBlind, "Hand start", "Small blind posted", "{player} posts small blind {amount}{allin}", ActionKeys),
        new(MessageKind.BigBlind, "Hand start", "Big blind posted", "{player} posts big blind {amount}{allin}", ActionKeys),

        new(MessageKind.Fold, "Actions", "Fold", "{player} folds", ["player", "stack"]),
        new(MessageKind.Check, "Actions", "Check", "{player} checks", ["player", "stack"]),
        new(MessageKind.Call, "Actions", "Call", "{player} calls {amount}{allin}", ActionKeys),
        new(MessageKind.Bet, "Actions", "Bet", "{player} bets {amount}{allin}", ActionKeys),
        new(MessageKind.Raise, "Actions", "Raise", "{player} raises to {amount}{allin}", ActionKeys),
        new(MessageKind.ActionToCall, "Actions", "Action to a player (facing a bet)", "Action to {player}: {amount} to call",
            ["player", "amount", "stack", "pot"]),
        new(MessageKind.ActionToCheck, "Actions", "Action to a player (can check)", "Action to {player}: Check or bet",
            ["player", "stack", "pot"]),

        new(MessageKind.Flop, "Streets", "Flop", "Flop: {board}\nPot: {pot}", StreetKeys),
        new(MessageKind.Turn, "Streets", "Turn", "Turn: {board}\nPot: {pot}", StreetKeys),
        new(MessageKind.River, "Streets", "River", "River: {board}\nPot: {pot}", StreetKeys),

        new(MessageKind.Shows, "Showdown", "Player shows their hand", "{player} shows {cards}: {hand}", ["player", "cards", "hand"]),
        new(MessageKind.PotWon, "Showdown", "Pot won", "{pot_name} {amount} to {player} ({hand}){rake}", PotKeys),
        new(MessageKind.PotSplit, "Showdown", "Pot split", "{pot_name} {amount} split between {player} ({hand}){rake}", PotKeys),
        new(MessageKind.PotReturned, "Showdown", "Uncalled chips returned", "{amount} goes back to {player} (uncontested)",
            ["amount", "player"]),
        new(MessageKind.WinUncontested, "Showdown", "Everyone else folded", "{player} wins {amount}{rake}", ["player", "amount", "rake"]),

        new(MessageKind.Correction, "Dealer", "Undo", "Dealer correction: back to {player} to act", ["player"]),
        new(MessageKind.HandCancelled, "Dealer", "Hand cancelled", "Hand #{hand} cancelled; all bets returned", ["hand"]),
    ];

    /// <summary>What each placeholder means, for the editor.</summary>
    public static readonly IReadOnlyDictionary<string, string> PlaceholderHelp = new Dictionary<string, string>
    {
        ["player"] = "the player's name (several names for a split pot)",
        ["amount"] = "the chips involved",
        ["allin"] = "\" (all-in)\" when the player is all-in, otherwise nothing",
        ["stack"] = "the player's stack after the action",
        ["pot"] = "the total pot",
        ["board"] = "all board cards",
        ["cards"] = "the new card(s) just dealt, or the player's hole cards",
        ["street"] = "Flop, Turn or River",
        ["hand"] = "the hand number, or the hand's name (e.g. Pair of Eights) at showdown",
        ["sb"] = "the small blind",
        ["bb"] = "the big blind",
        ["button"] = "the player on the button",
        ["players"] = "how many players are dealt in",
        ["pot_name"] = "Main pot or Side pot N",
        ["rake"] = "\" (rake N)\" when rake was taken, otherwise nothing",
    };

    private static readonly Dictionary<MessageKind, MessageInfo> ByKind = All.ToDictionary(m => m.Kind);

    private readonly Dictionary<MessageKind, string> overrides = new();

    /// <summary>Only the templates the dealer changed, for saving.</summary>
    public IReadOnlyDictionary<MessageKind, string> Overrides => overrides;

    public static MessageInfo Info(MessageKind kind) => ByKind[kind];

    public string Get(MessageKind kind) => overrides.TryGetValue(kind, out var text) ? text : ByKind[kind].Default;

    public bool IsCustom(MessageKind kind) => overrides.ContainsKey(kind);

    public void Set(MessageKind kind, string text)
    {
        text = text.Replace("\r", string.Empty);
        if (text == ByKind[kind].Default)
            overrides.Remove(kind);
        else
            overrides[kind] = text;
    }

    public void Reset(MessageKind kind) => overrides.Remove(kind);

    public void ResetAll() => overrides.Clear();

    /// <summary>The chat lines for this event: one per template line, blank lines dropped.</summary>
    public IReadOnlyList<string> Format(MessageKind kind, IReadOnlyDictionary<string, string> values) =>
        Fill(Get(kind), values);

    /// <summary>The event in the built-in wording, for the dealer's own UI.</summary>
    public static string FormatDefault(MessageKind kind, IReadOnlyDictionary<string, string> values) =>
        string.Join(" / ", Fill(ByKind[kind].Default, values));

    public static IReadOnlyList<string> Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var lines = new List<string>();
        foreach (var raw in template.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw;
            foreach (var (key, value) in values)
                line = line.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
            if (line.Trim().Length > 0)
                lines.Add(line.Trim());
        }

        return lines;
    }
}
