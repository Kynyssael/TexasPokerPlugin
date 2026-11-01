using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TexasPoker.Engine;

/// <summary>An action read from a player's chat line. <see cref="Amount"/> is the "raise to" total.</summary>
public sealed record ParsedAction(PlayerAction Action, long Amount);

/// <summary>
/// Reads a poker action from what a player typed in chat: "call", "fold", "check", "all in",
/// "bet 5000", "raise to 20k", "raise by 10k". Only short lines that are clearly an action match, so
/// ordinary table talk is ignored.
/// </summary>
public static partial class ChatActionParser
{
    private static readonly HashSet<string> Fillers = ["i", "ill", "im", "will", "go", "going", "please", "pls", "plz", "and"];
    private static readonly HashSet<string> FoldWords = ["fold", "folds", "f", "muck"];
    private static readonly HashSet<string> CheckWords = ["check", "checks", "x", "chk", "knock"];
    private static readonly HashSet<string> CallWords = ["call", "calls", "c"];
    private static readonly HashSet<string> AllInWords = ["allin", "shove", "jam"];
    private static readonly HashSet<string> RaiseWords = ["bet", "bets", "b", "raise", "raises", "r"];

    /// <summary>The action, or null if the line isn't an action that's legal right now.</summary>
    public static ParsedAction? Parse(string message, ActionOptions options, long currentBet)
    {
        var words = Tokenize(message);
        if (words.Count == 0)
            return null;

        var first = words[0];
        var rest = words.Skip(1).ToList();

        if (FoldWords.Contains(first) && rest.Count == 0)
            return new ParsedAction(PlayerAction.Fold, 0);

        if (CheckWords.Contains(first) && rest.Count == 0)
            return options.CanCheck ? new ParsedAction(PlayerAction.Check, 0) : null;

        if (CallWords.Contains(first) && rest.Count == 0)
        {
            // "Call" with nothing to call is a check.
            return options.ToCall > 0 ? new ParsedAction(PlayerAction.Call, 0)
                : options.CanCheck ? new ParsedAction(PlayerAction.Check, 0)
                : null;
        }

        if (AllInWords.Contains(first) && rest.Count == 0)
            return new ParsedAction(PlayerAction.AllIn, 0);

        if (RaiseWords.Contains(first))
            return ParseRaise(rest, options, currentBet);

        return null;
    }

    private static ParsedAction? ParseRaise(List<string> rest, ActionOptions options, long currentBet)
    {
        var by = false;
        if (rest.Count > 0 && rest[0] is "to" or "by")
        {
            by = rest[0] == "by";
            rest.RemoveAt(0);
        }

        long target;
        if (rest.Count == 0)
        {
            if (by)
                return null;
            target = options.MinRaiseTo; // just "raise": the minimum
        }
        else if (rest.Count == 1 && ParseAmount(rest[0]) is { } amount)
        {
            target = by ? currentBet + amount : amount;
        }
        else if (rest.Count == 1 && AllInWords.Contains(rest[0]))
        {
            return new ParsedAction(PlayerAction.AllIn, 0);
        }
        else
        {
            return null;
        }

        if (target >= options.MaxRaiseTo)
            return new ParsedAction(PlayerAction.AllIn, 0);
        if (!options.CanRaise)
            return null;

        return new ParsedAction(PlayerAction.BetOrRaise, Math.Max(target, options.MinRaiseTo));
    }

    /// <summary>"5000", "5,000", "5k", "1.5k", "2m".</summary>
    public static long? ParseAmount(string text)
    {
        var match = AmountRegex().Match(text);
        if (!match.Success)
            return null;

        var digits = match.Groups[1].Value;
        var suffix = match.Groups[2].Value;
        if (suffix.Length == 0)
        {
            // No suffix: commas and dots are thousands separators.
            return long.TryParse(digits.Replace(",", string.Empty).Replace(".", string.Empty), out var whole) ? whole : null;
        }

        if (!decimal.TryParse(digits.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return null;
        return (long)(value * (suffix == "k" ? 1_000m : 1_000_000m));
    }

    private static List<string> Tokenize(string message)
    {
        var text = message.ToLowerInvariant().Replace("all-in", "allin").Replace("all in", "allin");
        var clean = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or ',')
                clean.Append(c);
            else if (char.IsWhiteSpace(c))
                clean.Append(' ');
            // Apostrophes, ! and ? are dropped: "I'll call!" -> "ill call".
        }

        return clean.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ','))
            .Where(w => w.Length > 0 && !Fillers.Contains(w))
            .ToList();
    }

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)*)([km]?)$")]
    private static partial Regex AmountRegex();
}
