using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TexasPoker.Engine;

namespace TexasPoker;

/// <summary>A tournament level: blinds, ante and minutes, or a break.</summary>
public sealed record TournamentLevel(long SmallBlind, long BigBlind, long Ante, int Minutes, bool IsBreak)
{
    public static List<TournamentLevel> ParseAll(JsonElement list) =>
        list.ValueKind != JsonValueKind.Array
            ? []
            : list.EnumerateArray().Select(l => new TournamentLevel(
                Json.Long(l, "sb"), Json.Long(l, "bb"), Json.Long(l, "ante"), (int)Json.Long(l, "minutes"),
                l.TryGetProperty("break", out var b) && b.ValueKind == JsonValueKind.True)).ToList();

    public string Blinds => Ante > 0 ? $"{SmallBlind:N0}/{BigBlind:N0}, ante {Ante:N0}" : $"{SmallBlind:N0}/{BigBlind:N0}";
}

/// <summary>The tournament clock as the server sent it: the level (index into the levels) and the seconds left in it.</summary>
public sealed record TournamentClock(bool Started, int Index, double Left, bool Paused)
{
    public static TournamentClock? Parse(JsonElement c) =>
        c.ValueKind != JsonValueKind.Object
            ? null
            : new TournamentClock(c.TryGetProperty("started", out var s) && s.ValueKind == JsonValueKind.True, (int)Json.Long(c, "index"),
                                  Json.Long(c, "left"), c.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True);
}

/// <summary>A finishing place, prize in gil.</summary>
public sealed record TournamentPlace(string Name, string World, int Place, long Prize);

/// <summary>A player as an instruction names them, with chips when they come with some.</summary>
public sealed record TournamentPlayer(string Name, string World, long Chips);

/// <summary>
/// Something the server asks this table's dealer to do: "seat" the drawn players, "send" one to another table, or
/// "receive" one coming from another table.
/// </summary>
public sealed record TournamentInstruction(
    int Id, string Kind, List<TournamentPlayer> Players, int Button, TournamentPlayer? Player, long Chips, int To, string ToDealer, int From,
    string FromDealer)
{
    public static TournamentInstruction Parse(JsonElement i)
    {
        static TournamentPlayer P(JsonElement p) => new(Json.Str(p, "name"), Json.Str(p, "world"), Json.Long(p, "chips"));
        return new TournamentInstruction(
            (int)Json.Long(i, "id"), Json.Str(i, "kind"),
            i.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.EnumerateArray().Select(P).ToList() : [],
            (int)Json.Long(i, "button"),
            i.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.Object ? P(p) : null,
            Json.Long(i, "chips"), (int)Json.Long(i, "to"), Json.Str(i, "toDealer"), (int)Json.Long(i, "from"), Json.Str(i, "fromDealer"));
    }
}

/// <summary>The tournament this table deals, as the server last described it.</summary>
public sealed record TournamentInfo(
    string Id, string Name, string Status, long Chips, int TableNo, int Tables, bool Seated, List<TournamentLevel> Levels,
    TournamentClock? Clock, int PlayersLeft, int Entries, bool MayDeal, bool HandForHand, List<TournamentInstruction> Instructions,
    List<(int Seq, string Text)> Feed, int ExpectedHand, List<TournamentPlayer> Expected, List<TournamentPlace> Results, string Reason)
{
    /// <summary>The commentator browser's public key, while the commentator view is on and open: seal updates to it.</summary>
    public string? CommentatorKey { get; init; }

    public static TournamentInfo? Parse(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object)
            return null;
        static bool True(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var results = t.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => new TournamentPlace(Json.Str(x, "name"), Json.Str(x, "world"), (int)Json.Long(x, "place"), Json.Long(x, "prize"))).ToList()
            : [];
        var instructions = t.TryGetProperty("instructions", out var ins) && ins.ValueKind == JsonValueKind.Array
            ? ins.EnumerateArray().Select(TournamentInstruction.Parse).ToList()
            : [];
        var feed = t.TryGetProperty("feed", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => ((int)Json.Long(x, "seq"), Json.Str(x, "text"))).ToList()
            : [];
        var expectedHand = 0;
        var expected = new List<TournamentPlayer>();
        if (t.TryGetProperty("expected", out var ex) && ex.ValueKind == JsonValueKind.Object)
        {
            expectedHand = (int)Json.Long(ex, "hand");
            if (ex.TryGetProperty("players", out var eps) && eps.ValueKind == JsonValueKind.Array)
                expected = eps.EnumerateArray().Select(x => new TournamentPlayer(Json.Str(x, "name"), Json.Str(x, "world"), Json.Long(x, "chips"))).ToList();
        }

        return new TournamentInfo(
            Json.Str(t, "id"), Json.Str(t, "name"), Json.Str(t, "status"), Json.Long(t, "chips"), (int)Json.Long(t, "table"),
            (int)Json.Long(t, "tables"), True(t, "seated"),
            t.TryGetProperty("levels", out var levels) ? TournamentLevel.ParseAll(levels) : [],
            t.TryGetProperty("clock", out var clock) ? TournamentClock.Parse(clock) : null,
            (int)Json.Long(t, "playersLeft"), (int)Json.Long(t, "entries"), True(t, "mayDeal"), True(t, "handForHand"),
            instructions, feed, expectedHand, expected, results, Json.Str(t, "reason"))
        {
            CommentatorKey = t.TryGetProperty("commentator", out var c) && c.ValueKind == JsonValueKind.Object && Json.Str(c, "pub") is { Length: > 0 } pub
                ? pub
                : null,
        };
    }
}

/// <summary>Small helpers for reading the server's JSON.</summary>
public static class Json
{
    public static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    public static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
        : e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;

    public static string Ordinal(int n) =>
        n + (n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

/// <summary>
/// Runs this table for a tournament (the Tournament License). The server keeps the clock and the field; this seats the
/// drawn players, sets the blinds and ante of the level before each hand, calls the levels in chat, sends players to
/// other tables and seats the ones who arrive, waits for the other tables in hand-for-hand, reports each hand's stacks
/// and busts, announces the table's news (who's out, in what place), and ends the tournament at the table.
/// Everything reaches the server through the web table, so it must be on.
/// </summary>
public sealed class TournamentClient : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Plugin plugin;
    private readonly IFramework framework;
    private DateTime received = DateTime.UtcNow;
    private int lastReportedHand;
    private readonly Queue<string> reports = new();
    private bool reporting;
    private int reportTries;
    private DateTime nextReportTry = DateTime.MinValue;
    private int announcedIndex = -1;
    private int warnedIndex = -1;
    private int posting;
    private DateTime nextSeatRetry = DateTime.MinValue;

    public TournamentClient(Plugin plugin, IFramework framework)
    {
        this.plugin = plugin;
        this.framework = framework;
        lastReportedHand = plugin.Table.HandNumber;
        framework.Update += OnUpdate;
    }

    public void Dispose() => framework.Update -= OnUpdate;

    /// <summary>What the server last said about this table's tournament, or null if it deals none.</summary>
    public TournamentInfo? Current { get; private set; }

    /// <summary>The players at the table are a tournament's: chips, not gil.</summary>
    public bool Active => Config.TournamentId.Length > 0;

    /// <summary>A tournament was started (or a table given to you), and its players are waiting to be seated.</summary>
    public TournamentInstruction? SeatingWaiting =>
        !Active && Current is { Status: "running" } t ? t.Instructions.FirstOrDefault(i => i.Kind == "seat") : null;

    /// <summary>What this table's dealer is asked to do now (send or seat a player), if anything.</summary>
    public TournamentInstruction? NextMove =>
        Active && Current is { Status: "running" } t && t.Id == Config.TournamentId
            ? t.Instructions.FirstOrDefault(i => i.Kind is "send" or "receive")
            : null;

    /// <summary>
    /// Between hands, the players and chips here that differ from the server's after the same hand (a crash, a reload,
    /// a mistake), or null when they agree.
    /// </summary>
    public string? Mismatch { get; private set; }

    /// <summary>The last thing to tell the dealer about the tournament (a refused report, a warning).</summary>
    public string? Notice { get; set; }

    private Configuration Config => plugin.Configuration;

    private Table Table => plugin.Table;

    private static string BaseUrl => WebPublisher.ServerUrl.TrimEnd('/');

    /// <summary>The server's answer to a table update (any thread): the tournament part, if any.</summary>
    public void FromPushAnswer(string body)
    {
        TournamentInfo? info = null;
        var known = false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tournament", out var t))
            {
                known = true;
                info = TournamentInfo.Parse(t);
            }
        }
        catch (JsonException)
        {
        }

        if (known)
            framework.RunOnFrameworkThread(() => Apply(info));
    }

    private void Apply(TournamentInfo? info)
    {
        Current = info;
        received = DateTime.UtcNow;
        if (info is not null)
            TellDrawn(info);
        if (info is null || !Active || info.Id != Config.TournamentId)
            return;
        AnnounceFeed(info);
        if (info.Status is "finished" or "cancelled" or "released")
        {
            End(info);
            return;
        }

        // Seated here, but the server didn't hear it (it was offline): tell it again, so the clock can start.
        if (info.Instructions.FirstOrDefault(i => i.Kind == "seat") is { } seat && DateTime.UtcNow >= nextSeatRetry && posting == 0)
        {
            nextSeatRetry = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            Done(seat, 0);
        }
    }

    /// <summary>
    /// The tournament started (or a table was given to this dealer): each player drawn here gets a tell saying which
    /// table is theirs and to come over, once per draw.
    /// </summary>
    private void TellDrawn(TournamentInfo info)
    {
        if (Active || info.Status != "running" || info.Instructions.FirstOrDefault(i => i.Kind == "seat") is not { } seat)
            return;
        var key = $"{info.Id}:{seat.Id}";
        if (Config.TournamentToldDraw == key)
            return;
        Config.TournamentToldDraw = key;
        plugin.Save();

        var where = info.Tables > 1 ? $"you're at table {info.TableNo} of {info.Tables}, with me as your dealer" : "you're at my table";
        var told = 0;
        foreach (var p in seat.Players)
        {
            if (plugin.IsLocalCharacter(p.Name, p.World))
                continue;
            var chips = p.Chips > 0 && p.Chips != info.Chips ? $" with your {p.Chips:N0} chips" : string.Empty;
            plugin.TellTournamentPlayer(p.Name, p.World, $"{info.Name}: {where}. Come to my table and I'll seat you{chips}.");
            told++;
        }

        if (told > 0)
            plugin.Chat.PrintLocal($"Told your {told} player{(told == 1 ? string.Empty : "s")} by tell to come to your table: {string.Join(", ", seat.Players.Select(p => p.Name))}.");
    }

    /// <summary>The table chat news from the server (who's out, hand-for-hand, a table breaking), each line once.</summary>
    private void AnnounceFeed(TournamentInfo info)
    {
        var news = info.Feed.Where(f => f.Seq > Config.TournamentFeedSeq).OrderBy(f => f.Seq).ToList();
        if (news.Count == 0)
            return;
        Config.TournamentFeedSeq = news[^1].Seq;
        var lines = news.Select(f => f.Text).Where(x => x.Length > 0).ToList();
        if (info.Status == "running" && lines.Any(l => l.Contains(" is out in ")))
            lines.Add($"{info.PlayersLeft} players left.");
        plugin.Chat.SayToTable(lines);
        foreach (var line in lines)
            plugin.Record(line);
        plugin.Save();
    }

    // ---- the clock -----------------------------------------------------------------------------

    /// <summary>The level now (index into the levels), the seconds left in it, and whether the clock runs.</summary>
    public (int Index, double Left, bool Paused, bool Started) LevelNow(DateTime now) =>
        Current is { } t ? Advance(t.Levels, t.Clock, received, now) : (0, 0, false, false);

    /// <summary>
    /// Where a clock the server sent at <paramref name="received"/> is now: it moves on through the levels by itself
    /// between the server's updates.
    /// </summary>
    public static (int Index, double Left, bool Paused, bool Started) Advance(
        IReadOnlyList<TournamentLevel> levels, TournamentClock? c, DateTime received, DateTime now)
    {
        if (c is null || levels.Count == 0)
            return (0, 0, false, false);
        if (!c.Started)
            return (c.Index, 0, false, false);
        var index = Math.Clamp(c.Index, 0, levels.Count - 1);
        var left = c.Paused ? c.Left : c.Left - (now - received).TotalSeconds;
        while (!c.Paused && left <= 0 && index < levels.Count - 1)
        {
            index++;
            left += levels[index].Minutes * 60;
        }

        return (index, Math.Max(0, left), c.Paused, true);
    }

    /// <summary>"Level 3" (breaks aren't counted), or "Break".</summary>
    public string LevelName(int index) => Current is { } t ? LevelName(t.Levels, index) : string.Empty;

    public static string LevelName(IReadOnlyList<TournamentLevel> levels, int index) =>
        index >= levels.Count ? string.Empty
        : levels[index].IsBreak ? "Break" : $"Level {levels.Take(index + 1).Count(l => !l.IsBreak)}";

    /// <summary>The level whose blinds apply at this index: itself, or for a break the one after it.</summary>
    public TournamentLevel? BlindsAt(int index) =>
        Current?.Levels.Skip(index).FirstOrDefault(l => !l.IsBreak);

    public static string Clock(double seconds)
    {
        var s = (int)Math.Ceiling(seconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// <summary>Before each tournament hand: the blinds and ante of the level now. Throws if a hand can't be dealt yet.</summary>
    public void SetBlindsForHand()
    {
        if (Current is not { Status: "running" } t || t.Id != Config.TournamentId)
            throw new InvalidOperationException("Waiting for the tournament server. Is the web table on (Web tab)?");
        if (t.Instructions.FirstOrDefault(i => i.Kind == "send") is { Player: { } mover } send)
            throw new InvalidOperationException($"Send {mover.Name} to table {send.To} first (top of the Table tab).");
        if (!t.MayDeal)
            throw new InvalidOperationException("Hand-for-hand: wait until every other table has finished its hand.");
        var (index, left, paused, started) = LevelNow(DateTime.UtcNow);
        if (!started)
            throw new InvalidOperationException("The tournament clock hasn't started yet: it starts once every table is seated.");
        if (paused)
            throw new InvalidOperationException("The tournament clock is paused. The owner resumes it.");
        if (t.Levels[index].IsBreak)
            throw new InvalidOperationException($"On a break: {Clock(left)} left.");
        var level = t.Levels[index];
        Table.SmallBlind = level.SmallBlind;
        Table.BigBlind = level.BigBlind;
        Table.Ante = level.Ante;
        Table.TournamentMode = true;
    }

    // ---- instructions ----------------------------------------------------------------------------

    /// <summary>Seats the players the server drew (or the players of a table given to you), in its order and with its button.</summary>
    public void SeatPlayers()
    {
        if (Current is not { Status: "running" } t || SeatingWaiting is not { } seat)
            throw new InvalidOperationException("No tournament is waiting for this table.");
        var drawn = seat.Players;
        if (Table.HandInProgress)
            throw new InvalidOperationException("Finish the hand first.");
        if (Table.Seats.Count > 0)
            throw new InvalidOperationException("A tournament table starts empty: cash out everyone seated first.");
        if (drawn.Count > Table.MaxSeats)
            throw new InvalidOperationException($"{drawn.Count} players don't fit at one table.");
        if (drawn.Any(p => plugin.IsLocalCharacter(p.Name, p.World)))
            throw new InvalidOperationException("You're registered to play, and dealers don't play at their own table. Ask the owner.");

        foreach (var p in drawn)
            Table.AddSeat(p.Name, p.World, p.Chips > 0 ? p.Chips : t.Chips);
        Table.ButtonIndex = Math.Clamp(seat.Button, 0, drawn.Count - 1) - 1; // the next hand's button moves on to the drawn seat
        Table.TournamentMode = true;
        Table.Ante = 0;
        Config.TournamentId = t.Id;
        Config.TournamentFeedSeq = t.Feed.Count > 0 ? t.Feed.Max(f => f.Seq) : 0;
        lastReportedHand = Table.HandNumber;
        announcedIndex = -1;
        warnedIndex = -1;

        var where = t.Tables > 1 ? $"Table {t.TableNo} of {t.Tables}. " : string.Empty;
        plugin.Chat.SayToTable([
            $"Tournament: {t.Name}. {where}{t.Entries} players, {t.Chips:N0} chips each. No rebuys.",
            $"Seats: {string.Join(", ", drawn.Select((p, i) => $"{i + 1}. {p.Name}"))}",
        ]);
        plugin.Record($"=== Tournament {t.Name}, table {t.TableNo}: seated {string.Join(", ", drawn.Select(p => $"{p.Name}@{p.World} ({p.Chips:N0})"))} ===");
        plugin.Save();
        Done(seat, 0);
    }

    /// <summary>The next move: sends the player to their new table, or seats the one who arrived.</summary>
    public void DoMove()
    {
        if (NextMove is not { Player: { } who } move)
            throw new InvalidOperationException("Nothing to do.");
        if (Table.HandInProgress)
            throw new InvalidOperationException("Finish the hand first: players move between hands.");

        if (move.Kind == "send")
        {
            var seat = Table.Seats.FirstOrDefault(s => s.Name.Equals(who.Name, StringComparison.OrdinalIgnoreCase) &&
                                                       s.World.Equals(who.World, StringComparison.OrdinalIgnoreCase));
            var chips = seat?.Stack ?? 0;
            if (seat is not null)
            {
                Table.RemoveSeat(seat);
                plugin.TellTournamentMove(seat, $"You move to table {move.To}: go to {move.ToDealer}'s table, with your {chips:N0} chips.");
            }

            plugin.Chat.SayToTable([$"{who.Name} moves to table {move.To} ({move.ToDealer})."]);
            plugin.Record($"Tournament: sent {who.Name}@{who.World} to table {move.To} with {chips:N0} chips.");
            plugin.Save();
            Done(move, chips);
            return;
        }

        // Already seated (the server didn't hear it the first time): just say so again.
        if (Table.Seats.Any(s => s.Name.Equals(who.Name, StringComparison.OrdinalIgnoreCase) && s.World.Equals(who.World, StringComparison.OrdinalIgnoreCase)))
        {
            Done(move, move.Chips);
            return;
        }

        var arrived = Table.AddSeatBeforeButton(who.Name, who.World, move.Chips);
        plugin.Chat.SayToTable([$"Welcome {arrived.Name}, from table {move.From}, with {move.Chips:N0} chips."]);
        plugin.Record($"Tournament: seated {arrived.DisplayName} from table {move.From} with {move.Chips:N0} chips.");
        plugin.Save();
        Done(move, move.Chips);
    }

    /// <summary>Sets the table to the server's players and chips (after a crash or a mistake here).</summary>
    public void UseServerStacks()
    {
        if (Current is not { } t || Table.HandInProgress)
            return;
        foreach (var seat in Table.Seats.ToList())
        {
            if (!t.Expected.Any(p => Same(p, seat)))
                Table.RemoveSeat(seat);
        }

        foreach (var p in t.Expected)
        {
            if (Table.Seats.FirstOrDefault(s => Same(p, s)) is { } seat)
            {
                if (seat.Stack != p.Chips)
                    Table.AdjustChips(seat, p.Chips - seat.Stack);
            }
            else
            {
                Table.AddSeatBeforeButton(p.Name, p.World, p.Chips);
            }
        }

        plugin.Record($"Tournament: stacks set to the server's: {string.Join(", ", t.Expected.Select(p => $"{p.Name} {p.Chips:N0}"))}");
        Mismatch = null;
        plugin.Save();
    }

    private static bool Same(TournamentPlayer p, Seat s) =>
        p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase) && p.World.Equals(s.World, StringComparison.OrdinalIgnoreCase);

    private void Done(TournamentInstruction instruction, long chips)
    {
        if (Current is not { } t)
            return;
        Post("done", JsonSerializer.Serialize(new { t = t.Id, id = instruction.Id, chips }), ApplyAnswer);
    }

    private void ApplyAnswer(string answer)
    {
        using var doc = JsonDocument.Parse(answer);
        if (doc.RootElement.TryGetProperty("tournament", out var view))
            Apply(TournamentInfo.Parse(view));
    }

    // ---- hands -----------------------------------------------------------------------------------

    private void OnUpdate(IFramework _)
    {
        if (!Active)
            return;

        // A finished hand: who went out, and everyone's chips.
        if (Table.Street == Street.HandOver && Table.HandNumber != lastReportedHand)
        {
            lastReportedHand = Table.HandNumber;
            if (!Table.LastHandCancelled)
                QueueHand();
        }

        if (reports.Count > 0 && !reporting && DateTime.UtcNow >= nextReportTry)
            SendReport();

        CallLevels();
        CheckStacks();
        SendCommentary();
    }

    // ---- the commentator view --------------------------------------------------------------------

    private string lastCommentary = string.Empty;
    private bool sendingCommentary;
    private string? queuedCommentary;
    private string? equityKey;
    private IReadOnlyList<PlayerEquity> equity = [];

    /// <summary>
    /// While the commentator view is open, every change at this table goes to the commentator: every player's cards,
    /// their chances and their hand, sealed so only the commentator's browser can read them. The server passes it on
    /// after the delay. One update is on its way at a time; a newer one waits for it (the latest only), so the last
    /// update of a hand always goes, even the tournament's last hand, whose report ends the tournament here.
    /// </summary>
    private void SendCommentary()
    {
        if (Current is not { Status: "running", CommentatorKey: { } key } t || t.Id != Config.TournamentId)
            return;
        var state = $"{Table.Sequence}|{Table.HandNumber}|{Table.Street}|{string.Join(",", Table.Seats.Select(s => $"{s.Name}:{s.Stack}:{s.SittingOut}"))}";
        if (state == lastCommentary)
            return;
        lastCommentary = state;

        string sealedText;
        try
        {
            sealedText = CommentatorSealer.Seal(key, CommentaryJson(t));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return; // not a key this can seal to
        }

        var json = JsonSerializer.Serialize(new { t = t.Id, hand = Table.HandNumber, over = !Table.HandInProgress, c = sealedText });
        if (sendingCommentary)
            queuedCommentary = json;
        else
            PostCommentary(json);
    }

    private void PostCommentary(string json)
    {
        sendingCommentary = true;
        Post("commentary", json, _ => NextCommentary(), _ => NextCommentary());
    }

    private void NextCommentary()
    {
        sendingCommentary = false;
        if (queuedCommentary is not { } next)
            return;
        queuedCommentary = null;
        PostCommentary(next);
    }

    private string CommentaryJson(TournamentInfo t)
    {
        static object CardOf(Card c) => new { r = c.RankText, s = c.SuitText(false) };
        var live = Table.Seats.Where(s => s.InHand && !s.Folded && s.HoleCards.Count == 2).ToList();
        var board = Table.Board.ToList();
        var key = $"{Table.HandNumber}|{board.Count}|{string.Join(",", live.Select(s => s.Name))}";
        if (key != equityKey)
        {
            equityKey = key;
            equity = live.Count >= 2 && Table.HandNumber > 0 ? Equity.Calculate(live.Select(s => s.HoleCards).ToList(), board) : [];
        }

        var showing = Table.HandInProgress || Table.Street == Street.HandOver;
        return JsonSerializer.Serialize(new
        {
            table = t.TableNo,
            hand = Table.HandNumber,
            street = Table.Street.ToString(),
            inProgress = Table.HandInProgress,
            board = board.Select(CardOf),
            pot = Table.Pot,
            sb = Table.SmallBlind,
            bb = Table.BigBlind,
            ante = Table.Ante,
            button = Table.ButtonIndex,
            actor = Table.Actor?.Name,
            lastAction = Table.LastAction,
            results = Table.HandInProgress ? [] : Table.LastResults.Select(r =>
                $"{r.Label}: {string.Join(", ", r.Winners.Select(w => w.Name))} wins {r.Amount:N0}" + (r.Hand is { } h ? $" ({h})" : string.Empty)),
            players = Table.Seats.Select(s =>
            {
                var i = live.IndexOf(s);
                var chances = i >= 0 && i < equity.Count ? equity[i] : null;
                var hand = showing && s.HoleCards.Count == 2 ? Table.CurrentHand(s)?.Describe() : null;
                return new
                {
                    name = s.Name,
                    stack = s.Stack,
                    bet = s.StreetBet,
                    cards = showing && s.InHand ? s.HoleCards.Select(CardOf) : [],
                    inHand = s.InHand && showing,
                    folded = s.Folded,
                    allIn = s.AllIn,
                    away = s.SittingOut,
                    win = chances?.Win,
                    tie = chances?.Tie,
                    outs = chances?.Outs ?? 0,
                    outsTo = chances?.OutsTo is { } to ? HandName(to) : null,
                    hand,
                };
            }),
        });
    }

    private static string HandName(HandCategory category) => category switch
    {
        HandCategory.HighCard => "high card",
        HandCategory.OnePair => "a pair",
        HandCategory.TwoPair => "two pair",
        HandCategory.ThreeOfAKind => "three of a kind",
        HandCategory.Straight => "a straight",
        HandCategory.Flush => "a flush",
        HandCategory.FullHouse => "a full house",
        HandCategory.FourOfAKind => "four of a kind",
        _ => "a straight flush",
    };

    private void QueueHand()
    {
        var busts = Table.Seats.Where(s => s.InHand && s.Stack == 0).ToList();
        var kept = Table.Seats.Where(s => s.Stack > 0).ToList();

        // Who has the button and who'd be big blind next hand, so the server moves the right player.
        Seat? NextWithChips(int from)
        {
            for (var n = 1; n <= Table.Seats.Count; n++)
            {
                var seat = Table.Seats[((from + n) % Table.Seats.Count + Table.Seats.Count) % Table.Seats.Count];
                if (seat.Stack > 0)
                    return seat;
            }

            return null;
        }

        var button = Table.ButtonIndex >= 0 && Table.ButtonIndex < Table.Seats.Count && Table.Seats[Table.ButtonIndex].Stack > 0
            ? Table.Seats[Table.ButtonIndex]
            : NextWithChips(Table.ButtonIndex);
        var nextBigBlind = Table.BigBlindIndex >= 0 ? NextWithChips(Table.BigBlindIndex) : null;
        object? Who(Seat? s) => s is null ? null : new { name = s.Name, world = s.World };

        var json = JsonSerializer.Serialize(new
        {
            t = Config.TournamentId,
            hand = Table.HandNumber,
            stacks = kept.Select(s => new { name = s.Name, world = s.World, chips = s.Stack }),
            busts = busts.Select(s => new { name = s.Name, world = s.World, start = s.StartingStack }),
            button = Who(button),
            nextBB = Who(nextBigBlind),
        });
        foreach (var seat in busts)
        {
            Table.RemoveSeat(seat);
            plugin.Record($"{seat.DisplayName} is out (hand #{Table.HandNumber}).");
        }

        reports.Enqueue(json);
        if (busts.Count > 0)
            plugin.Save();
    }

    private void SendReport()
    {
        reporting = true;
        var json = reports.Peek();
        Post("hand", json, answer =>
        {
            reports.Dequeue();
            reportTries = 0;
            using var doc = JsonDocument.Parse(answer);
            var warning = Json.Str(doc.RootElement, "warning");
            if (warning.Length > 0)
            {
                Notice = $"Chip check: {warning} Tell the owner.";
                plugin.Chat.PrintLocal(Notice);
            }

            if (doc.RootElement.TryGetProperty("tournament", out var view))
                Apply(TournamentInfo.Parse(view));
        }, failed: refused =>
        {
            if (refused || ++reportTries >= 30)
            {
                reports.Dequeue();
                reportTries = 0;
                Notice = "The tournament server refused a hand report: tell the owner to check the standings.";
                plugin.Chat.PrintLocal(Notice);
            }
            else
            {
                nextReportTry = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            }
        });
    }

    /// <summary>Between hands, compares this table with the server's players and chips after the same hand.</summary>
    private void CheckStacks()
    {
        Mismatch = null;
        if (Current is not { Status: "running" } t || t.Id != Config.TournamentId || Table.HandInProgress || reports.Count > 0 ||
            reporting || posting > 0 || t.Instructions.Count > 0 || t.ExpectedHand != Table.HandNumber || t.ExpectedHand == 0)
            return;
        var differences = new List<string>();
        foreach (var p in t.Expected)
        {
            var seat = Table.Seats.FirstOrDefault(s => Same(p, s));
            if (seat is null)
                differences.Add($"{p.Name} isn't seated here (server: {p.Chips:N0})");
            else if (seat.Stack != p.Chips)
                differences.Add($"{p.Name} has {seat.Stack:N0} here, {p.Chips:N0} on the server");
        }

        foreach (var seat in Table.Seats.Where(s => !t.Expected.Any(p => Same(p, s))))
            differences.Add($"{seat.Name} is seated here but not on the server");
        if (differences.Count > 0)
            Mismatch = string.Join("; ", differences) + ".";
    }

    /// <summary>Calls each new level in chat, and warns a minute before the blinds go up.</summary>
    private void CallLevels()
    {
        if (Current is not { Status: "running" } t || t.Id != Config.TournamentId || t.Levels.Count == 0)
            return;
        var (index, left, paused, started) = LevelNow(DateTime.UtcNow);
        if (!started || paused)
            return;

        if (index != announcedIndex)
        {
            var first = announcedIndex < 0;
            announcedIndex = index;
            var level = t.Levels[index];
            var line = level.IsBreak
                ? $"Break: {level.Minutes} minutes. Play starts again at {BlindsAt(index)?.Blinds ?? "the next level"}."
                : $"{LevelName(index)}: blinds {level.Blinds}{(first ? string.Empty : ", from the next hand")}.";
            plugin.Chat.SayToTable([line]);
            plugin.Record(line);
        }

        if (left <= 60 && warnedIndex != index && index < t.Levels.Count - 1 && BlindsAt(index + 1) is { } next && !t.Levels[index].IsBreak)
        {
            warnedIndex = index;
            plugin.Chat.SayToTable([t.Levels[index + 1].IsBreak
                ? "Break in 1 minute."
                : $"Blinds go up to {next.Blinds} in 1 minute."]);
        }
    }

    /// <summary>
    /// The tournament is over, cancelled, or done for this table (it closed, or went to another dealer): its players
    /// leave, and the table is a cash table again.
    /// </summary>
    private void End(TournamentInfo info)
    {
        if (Table.HandInProgress)
            Table.CancelHand();
        foreach (var seat in Table.Seats.ToList())
            Table.RemoveSeat(seat);
        Config.TournamentId = string.Empty;
        Config.TournamentFeedSeq = 0;
        Table.TournamentMode = false;
        Table.Ante = 0;
        Table.SmallBlind = Config.SmallBlind;
        Table.BigBlind = Config.BigBlind;
        reports.Clear();
        Mismatch = null;

        var lines = new List<string>();
        if (info.Status == "released")
        {
            Notice = info.Reason.Length > 0 ? info.Reason : "Your tournament table is done.";
            plugin.Chat.PrintLocal(Notice);
        }
        else if (info.Status == "cancelled")
        {
            lines.Add($"The tournament {info.Name} was cancelled by the owner.");
        }
        else
        {
            var paid = info.Results.Where(r => r.Prize > 0).Select(r => $"{Json.Ordinal(r.Place)} {r.Name} {r.Prize:N0}").ToList();
            if (paid.Count > 0)
                lines.Add($"Prizes (paid by the owner): {string.Join(", ", paid)}");
        }

        plugin.Chat.SayToTable(lines);
        foreach (var line in lines)
            plugin.Record(line);
        plugin.Record("=== Tournament over at this table: back to a cash table ===");
        plugin.Save();
    }

    private void Post(string what, string json, Action<string> done, Action<bool>? failed = null)
    {
        var url = $"{BaseUrl}/api/t/{Config.WebTableId}/tournament/{what}";
        var session = Config.AuthSessionKey;
        var quiet = what == "commentary"; // many, and nothing to tell the dealer if one doesn't arrive
        if (!quiet)
            posting++;
        _ = Task.Run(async () =>
        {
            string? answer = null;
            var refused = false;
            var error = string.Empty;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    answer = text;
                else
                {
                    refused = (int)response.StatusCode is >= 400 and < 500 and not 429;
                    error = text;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            await framework.RunOnFrameworkThread(() =>
            {
                if (!quiet)
                    posting--;
                if (what == "hand")
                    reporting = false;
                if (answer is not null)
                {
                    try
                    {
                        done(answer);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                    {
                        // An answer this plugin doesn't understand.
                    }
                }
                else
                {
                    if (!quiet && (what != "hand" || refused))
                    {
                        Notice = $"Tournament server: {error}";
                        plugin.Chat.PrintLocal(Notice);
                    }

                    failed?.Invoke(refused);
                }
            }).ConfigureAwait(false);
        });
    }
}
