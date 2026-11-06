using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace TexasPoker.Engine;

/// <summary>
/// Short PINs for players who can't easily type their private link (on a phone). The dealer tells a player a
/// 4-digit PIN; on the table page they tap their seat and enter it, and get their private link.
///
/// The plugin sends the server each live PIN as a lookup and the player's token sealed with
/// <see cref="Secret"/> (see <see cref="CardSealer"/>), so the server never sees the token itself. The server
/// hands the sealed token over once, for the right PIN, and spends a seat's PINs after 5 wrong tries.
/// PINs are kept in memory only: they work for <see cref="Lifetime"/> and are gone after a reload.
/// </summary>
public sealed class SeatPins
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (string Pin, DateTime Until)> pins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the PIN seals the token with. The page builds the same: "pin-v1:Name@World:1234".</summary>
    public static string Secret(string seat, string pin) => $"pin-v1:{seat}:{pin}";

    /// <summary>A new PIN for this player (the old one, if any, stops working).</summary>
    public string Issue(string player, DateTime now)
    {
        var pin = RandomNumberGenerator.GetInt32(10000).ToString("D4");
        pins[player] = (pin, now + Lifetime);
        return pin;
    }

    /// <summary>The player's PIN while it works, or null.</summary>
    public string? PinOf(string player, DateTime now) =>
        pins.TryGetValue(player, out var entry) && entry.Until > now ? entry.Pin : null;

    /// <summary>The PINs that still work (player, PIN, until when), dropping the ones that ran out.</summary>
    public IReadOnlyList<(string Player, string Pin, DateTime Until)> Live(DateTime now)
    {
        foreach (var player in pins.Where(p => p.Value.Until <= now).Select(p => p.Key).ToList())
            pins.Remove(player);
        return pins.Select(p => (p.Key, p.Value.Pin, p.Value.Until)).ToList();
    }

    /// <summary>Drops the PINs of players who aren't seated any more.</summary>
    public void KeepOnly(IEnumerable<string> players)
    {
        var keep = players.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var player in pins.Keys.Where(p => !keep.Contains(p)).ToList())
            pins.Remove(player);
    }

    /// <summary>Drops the player's PIN (the server stopped it).</summary>
    public void Forget(string player) => pins.Remove(player);

    public void Clear() => pins.Clear();
}
