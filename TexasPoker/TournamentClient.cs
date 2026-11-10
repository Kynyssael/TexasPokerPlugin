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

/// <summary>The tournament this table deals, as the server last described it.</summary>
public sealed record TournamentInfo(
    string Id, string Name, string Status, long Chips, bool Seated, List<(string Name, string World)>? Seating, int Button,
    List<TournamentLevel> Levels, TournamentClock? Clock, int PlayersLeft, int Entries, List<TournamentPlace> Results)
{
    public static TournamentInfo? Parse(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object)
            return null;
        List<(string, string)>? seating = null;
        if (t.TryGetProperty("seating", out var s) && s.ValueKind == JsonValueKind.Array)
            seating = s.EnumerateArray().Select(x => (Json.Str(x, "name"), Json.Str(x, "world"))).ToList();
        var results = t.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => new TournamentPlace(Json.Str(x, "name"), Json.Str(x, "world"), (int)Json.Long(x, "place"), Json.Long(x, "prize"))).ToList()
            : [];
        return new TournamentInfo(
            Json.Str(t, "id"), Json.Str(t, "name"), Json.Str(t, "status"), Json.Long(t, "chips"),
            t.TryGetProperty("seated", out var seated) && seated.ValueKind == JsonValueKind.True, seating, (int)Json.Long(t, "button"),
            t.TryGetProperty("levels", out var levels) ? TournamentLevel.ParseAll(levels) : [],
            t.TryGetProperty("clock", out var clock) ? TournamentClock.Parse(clock) : null,
            (int)Json.Long(t, "playersLeft"), (int)Json.Long(t, "entries"), results);
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
/// drawn players, sets the blinds and ante of the level before each hand, calls the levels in chat, reports each hand's
/// stacks and busts, announces each player out with their place, and ends the tournament at the table.
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
    private DateTime nextSeatedRetry = DateTime.MinValue;

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

    /// <summary>A tournament was started for this table, and its players are waiting to be seated.</summary>
    public bool WaitingToSeat => !Active && Current is { Status: "running", Seated: false, Seating: not null };

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
        if (info is null || !Active || info.Id != Config.TournamentId)
            return;
        if (info.Status is "finished" or "cancelled")
        {
            End(info);
            return;
        }

        // Seated here, but the server didn't hear it (it was offline): tell it again, so the clock starts.
        if (info is { Status: "running", Seated: false } && DateTime.UtcNow >= nextSeatedRetry)
        {
            nextSeatedRetry = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            Post("seated", JsonSerializer.Serialize(new { t = info.Id }), answer =>
            {
                using var doc = JsonDocument.Parse(answer);
                if (doc.RootElement.TryGetProperty("tournament", out var view))
                    Apply(TournamentInfo.Parse(view));
            });
        }
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
        var (index, left, paused, started) = LevelNow(DateTime.UtcNow);
        if (!started)
            throw new InvalidOperationException("The tournament clock hasn't started yet.");
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

    // ---- seating ---------------------------------------------------------------------------------

    /// <summary>Seats the players the server drew, in its order and with its button, and tells the server.</summary>
    public void SeatPlayers()
    {
        if (Current is not { Status: "running", Seating: { } drawn } t)
            throw new InvalidOperationException("No tournament is waiting for this table.");
        if (Table.HandInProgress)
            throw new InvalidOperationException("Finish the hand first.");
        if (Table.Seats.Count > 0)
            throw new InvalidOperationException("A tournament table starts empty: cash out everyone seated first.");
        if (drawn.Count > Table.MaxSeats)
            throw new InvalidOperationException($"{drawn.Count} players don't fit at one table.");
        if (drawn.Any(p => plugin.IsLocalCharacter(p.Name, p.World)))
            throw new InvalidOperationException("You're registered to play, and dealers don't play at their own table. Ask the owner.");

        foreach (var (name, world) in drawn)
            Table.AddSeat(name, world, t.Chips);
        Table.ButtonIndex = Math.Clamp(t.Button, 0, drawn.Count - 1) - 1; // the next hand's button moves on to the drawn seat
        Table.TournamentMode = true;
        Table.Ante = 0;
        Config.TournamentId = t.Id;
        lastReportedHand = Table.HandNumber;
        announcedIndex = -1;
        warnedIndex = -1;

        plugin.Chat.SayToTable([
            $"Tournament: {t.Name}. {drawn.Count} players, {t.Chips:N0} chips each. No rebuys.",
            $"Seats: {string.Join(", ", drawn.Select((p, i) => $"{i + 1}. {p.Name}"))}",
        ]);
        plugin.Record($"=== Tournament {t.Name}: seated {string.Join(", ", drawn.Select(p => $"{p.Name}@{p.World}"))} with {t.Chips:N0} chips each ===");
        plugin.Save();
        Post("seated", JsonSerializer.Serialize(new { t = t.Id }), answer =>
        {
            using var doc = JsonDocument.Parse(answer);
            if (doc.RootElement.TryGetProperty("tournament", out var view))
                Apply(TournamentInfo.Parse(view));
        });
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
    }

    private void QueueHand()
    {
        var busts = Table.Seats.Where(s => s.InHand && s.Stack == 0).ToList();
        var json = JsonSerializer.Serialize(new
        {
            t = Config.TournamentId,
            hand = Table.HandNumber,
            stacks = Table.Seats.Where(s => s.Stack > 0).Select(s => new { name = s.Name, world = s.World, chips = s.Stack }),
            busts = busts.Select(s => new { name = s.Name, world = s.World, start = s.StartingStack }),
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
            HandleHandAnswer(answer);
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

    private void HandleHandAnswer(string answer)
    {
        using var doc = JsonDocument.Parse(answer);
        var root = doc.RootElement;
        var lines = new List<string>();
        if (root.TryGetProperty("out", out var gone) && gone.ValueKind == JsonValueKind.Array)
            lines.AddRange(gone.EnumerateArray().Select(o => Json.Str(o, "text")).Where(x => x.Length > 0));
        var left = (int)Json.Long(root, "playersLeft");
        var finished = root.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True;
        if (lines.Count > 0 && !finished && left > 0)
            lines.Add($"{left} players left.");
        if (lines.Count > 0)
            plugin.Chat.SayToTable(lines);
        foreach (var line in lines)
            plugin.Record(line);

        var warning = Json.Str(root, "warning");
        if (warning.Length > 0)
        {
            Notice = $"Chip check: {warning} Tell the owner.";
            plugin.Chat.PrintLocal(Notice);
        }

        if (root.TryGetProperty("tournament", out var view) && TournamentInfo.Parse(view) is { } info)
            Apply(info);
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

    /// <summary>The tournament is over (or cancelled): its players leave the table, and the table is a cash table again.</summary>
    private void End(TournamentInfo info)
    {
        if (Table.HandInProgress)
            Table.CancelHand();
        foreach (var seat in Table.Seats.ToList())
            Table.RemoveSeat(seat);
        Config.TournamentId = string.Empty;
        Table.TournamentMode = false;
        Table.Ante = 0;
        Table.SmallBlind = Config.SmallBlind;
        Table.BigBlind = Config.BigBlind;
        reports.Clear();

        var lines = new List<string>();
        if (info.Status == "cancelled")
        {
            lines.Add($"The tournament {info.Name} was cancelled by the owner.");
        }
        else
        {
            var winner = info.Results.FirstOrDefault(r => r.Place == 1);
            lines.Add(winner is null ? $"{info.Name} is over." : $"{winner.Name} wins {info.Name}! Congratulations!");
            var paid = info.Results.Where(r => r.Prize > 0).Select(r => $"{Json.Ordinal(r.Place)} {r.Name} {r.Prize:N0}").ToList();
            if (paid.Count > 0)
                lines.Add($"Prizes (paid by the owner): {string.Join(", ", paid)}");
        }

        plugin.Chat.SayToTable(lines);
        foreach (var line in lines)
            plugin.Record(line);
        plugin.Record("=== Tournament over: back to a cash table ===");
        plugin.Save();
    }

    private void Post(string what, string json, Action<string> done, Action<bool>? failed = null)
    {
        var url = $"{BaseUrl}/api/t/{Config.WebTableId}/tournament/{what}";
        var session = Config.AuthSessionKey;
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
                    if (what != "hand" || refused)
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
