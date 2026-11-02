using System;
using System.Text.Json;

namespace TexasPoker;

/// <summary>
/// The promotions of the venue a table is shown under, as the server sends them with each update: the high hand
/// and the bad beat jackpot, each null when the venue's owner has it turned off.
/// </summary>
public sealed record VenuePromotions(string VenueId, string Venue, HighHandPromotion? HighHand, BadBeatPromotion? BadBeat)
{
    /// <summary>From the server's answer to an update: JSON with "promotions" (newer servers) or "ok" (older ones).</summary>
    public static VenuePromotions? FromPushAnswer(string body)
    {
        if (!body.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("promotions", out var p) ? Parse(p) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static VenuePromotions? Parse(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
            return null;

        HighHandPromotion? high = null;
        if (p.TryGetProperty("highHand", out var hh) && hh.ValueKind == JsonValueKind.Object)
        {
            var best = hh.TryGetProperty("best", out var b) && b.ValueKind == JsonValueKind.Object ? b : (JsonElement?)null;
            high = new HighHandPromotion(Str(hh, "min"), Str(hh, "prize"), best is { } x ? Str(x, "player") : null, best is { } y ? Str(y, "hand") : null);
        }

        BadBeatPromotion? bad = null;
        if (p.TryGetProperty("badBeat", out var bb) && bb.ValueKind == JsonValueKind.Object)
        {
            bad = new BadBeatPromotion(
                Str(bb, "qualifier"),
                bb.TryGetProperty("dropPercent", out var d) && d.TryGetDecimal(out var pct) ? Math.Clamp(pct, 0, 10) : 0,
                bb.TryGetProperty("dropCap", out var c) && c.TryGetInt64(out var cap) ? Math.Max(0, cap) : 0,
                bb.TryGetProperty("fromRake", out var r) && r.ValueKind == JsonValueKind.True,
                bb.TryGetProperty("minPlayers", out var m) && m.TryGetInt32(out var min) ? min : 2,
                bb.TryGetProperty("balance", out var bal) && bal.TryGetInt64(out var balance) ? balance : 0);
        }

        return new VenuePromotions(Str(p, "venueId"), Str(p, "venue"), high, bad);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}

/// <summary>The venue's high hand: the minimum hand, the prize, and the best hand so far (null before one).</summary>
public sealed record HighHandPromotion(string Min, string Prize, string? BestPlayer, string? BestHand);

/// <summary>The venue's bad beat jackpot: what qualifies, the cut each pot puts in, and the jackpot now.</summary>
public sealed record BadBeatPromotion(string Qualifier, decimal DropPercent, long DropCap, bool FromRake, int MinPlayers, long Balance);
