using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace TexasPoker.Windows;

/// <summary>
/// The Tournaments part of the Venue tab, for an owner with the Tournament License: set a tournament up, register
/// the players at the start of the event, start it with one of the venue's dealers, watch and pause the clock, and pay
/// the prizes at the end.
/// </summary>
public sealed class TournamentsPanel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(15);

    private readonly Plugin plugin;
    private readonly Dictionary<string, Section> sections = new();
    private readonly Dictionary<string, string> registerText = new();
    /// <summary>The dealer picked for each table at the start, by tournament.</summary>
    private readonly Dictionary<string, List<int>> dealerPicks = new();

    /// <summary>The dealer picked to take over a table, by tournament and table.</summary>
    private readonly Dictionary<string, int> handover = new();
    private Draft? editing;
    private string editingVenue = string.Empty;
    private bool busy;
    private string message = string.Empty;
    private bool messageIsError;

    public TournamentsPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private static string BaseUrl => WebPublisher.ServerUrl.TrimEnd('/');

    public void Draw(string venueId)
    {
        ImGui.PushID($"tour{venueId}");
        ImGui.TextColored(DealerControls.Gold, "Tournaments");
        ImGui.SameLine();
        ImGui.TextColored(DealerControls.Muted, "Tournament License");

        sections.TryGetValue(venueId, out var section);
        var due = section is null || (section.List.Any(t => t.Status == "running") && DateTime.UtcNow - section.Loaded > RefreshEvery);
        if (due && !busy)
            Load(venueId);

        ImGui.TextWrapped("Up to 8 tables of 7 players. Register the players at the start of the event, once each has traded you " +
                          "the entry fee. Start closes registration: the server draws the tables and seats, and each table's dealer " +
                          "seats their players. The server moves players to keep the tables even, breaks tables as players go out, " +
                          "and runs hand-for-hand on the bubble. You pay the prizes at the end.");
        ImGui.BeginDisabled(busy);
        if (ImGui.SmallButton("Refresh##tour"))
            Load(venueId);
        ImGui.SameLine();
        if (ImGui.SmallButton("New tournament") && section is not null)
        {
            editing = Draft.New(section);
            editingVenue = venueId;
        }

        ImGui.EndDisabled();
        if (busy)
        {
            ImGui.SameLine();
            ImGui.TextColored(DealerControls.Muted, "Working...");
        }

        if (message.Length > 0)
            ImGui.TextColored(messageIsError ? DealerControls.Red : DealerControls.Good, message);

        if (section is null)
        {
            ImGui.PopID();
            return;
        }

        if (editing is not null && editingVenue == venueId)
            DrawEditor(venueId, section, editing);

        if (section.List.Count == 0)
            ImGui.TextColored(DealerControls.Muted, "No tournaments yet.");
        foreach (var t in section.List)
            DrawTournament(venueId, section, t);
        DrawSeries(venueId, section);
        ImGui.PopID();
    }

    // ---- one tournament -----------------------------------------------------------------------------

    private void DrawTournament(string venueId, Section section, TournamentView t)
    {
        ImGui.PushID(t.Id);
        var status = t.Status switch
        {
            "draft" => $"registering, {t.Entries.Count} player{(t.Entries.Count == 1 ? string.Empty : "s")}",
            "running" => $"running, {t.PlayersLeft} of {t.Entries.Count} left",
            "finished" => t.Results.FirstOrDefault() is { } w ? $"finished, won by {w.Name}" : "finished",
            _ => "cancelled",
        };
        var open = t.Status is "draft" or "running" ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.TreeNodeEx($"{t.Name}   ({status})###node", open))
        {
            ImGui.PopID();
            return;
        }

        var path = $"/api/venue/{venueId}/tournaments/{t.Id}";
        ImGui.TextColored(DealerControls.Muted,
                          $"Entry {t.Fee:N0} gil, prize pool {t.PoolNow:N0}{(t.Pool is null ? " (the entry fees)" : string.Empty)}, " +
                          $"{t.Chips:N0} chips each. Prizes {string.Join(" / ", t.Prizes.Select(p => $"{p}%"))}. " +
                          $"{t.Levels.Count(l => !l.IsBreak)} levels from {t.Levels.FirstOrDefault()?.Blinds}.");

        if (t.CommentatorOn && t.Status is "draft" or "running")
            DrawCommentator(venueId, t, path);

        switch (t.Status)
        {
            case "draft":
                DrawDraft(venueId, section, t, path);
                break;
            case "running":
                DrawRunning(venueId, t, path);
                break;
            default:
                DrawResults(venueId, t, path);
                break;
        }

        if (t.Events.Count > 0 && ImGui.TreeNode("Log##events"))
        {
            foreach (var line in t.Events)
                ImGui.TextColored(DealerControls.Muted, line);
            ImGui.TreePop();
        }

        ImGui.TreePop();
        ImGui.Spacing();
        ImGui.PopID();
    }

    private void DrawDraft(string venueId, Section section, TournamentView t, string path)
    {
        ImGui.BeginDisabled(busy);
        if (ImGui.SmallButton("Edit"))
        {
            editing = Draft.From(t);
            editingVenue = venueId;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
        if (ImGui.SmallButton("Delete"))
            Send(venueId, HttpMethod.Delete, path, null, $"Deleted {t.Name}.");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click.");
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextUnformatted($"Registered: {t.Entries.Count} of {section.MaxPlayers}");
        foreach (var e in t.Entries)
        {
            ImGui.PushID(e.Key);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted($"{e.Name}@{e.World}");
            ImGui.SameLine();
            ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("Remove"))
                Send(venueId, HttpMethod.Delete, $"{path}/register/{Uri.EscapeDataString(e.Key)}", null,
                     $"Removed {e.Name}. Refund their entry fee ({t.Fee:N0} gil).");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click. Trade them their entry fee back.");
            ImGui.PopID();
        }

        var full = t.Entries.Count >= section.MaxPlayers;
        var text = registerText.GetValueOrDefault(t.Id, string.Empty);
        ImGui.BeginDisabled(busy || full);
        ImGui.SetNextItemWidth(240);
        var enter = ImGui.InputTextWithHint("##who", "First Last@World", ref text, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        registerText[t.Id] = text;
        ImGui.SameLine();
        if ((ImGui.Button("Register") || enter) && text.Trim().Length > 0)
        {
            Register(venueId, t, text.Trim());
            registerText[t.Id] = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Register my target"))
        {
            if (plugin.GetTargetPlayer() is { } target)
                Register(venueId, t, $"{target.Name}@{target.World}");
            else
                Show("Target a player first.", true);
        }

        ImGui.EndDisabled();
        ImGui.TextColored(DealerControls.Muted, "Register each player once they've traded you the entry fee.");

        ImGui.Spacing();
        var tables = Math.Max(1, t.TablesNeeded);
        ImGui.TextUnformatted(tables == 1 ? "Dealer" : $"{tables} tables: a dealer for each");
        if (!dealerPicks.TryGetValue(t.Id, out var picks))
            dealerPicks[t.Id] = picks = [];
        while (picks.Count < tables)
            picks.Add(Math.Min(picks.Count, Math.Max(0, section.Dealers.Count - 1)));
        for (var n = 0; n < tables; n++)
        {
            var pick = Math.Clamp(picks[n], 0, Math.Max(0, section.Dealers.Count - 1));
            ImGui.SetNextItemWidth(240);
            if (ImGui.BeginCombo($"Table {n + 1}##dealer{n}", section.Dealers.Count > 0 ? section.Dealers[pick].Label : "(none)"))
            {
                for (var i = 0; i < section.Dealers.Count; i++)
                {
                    if (ImGui.Selectable(section.Dealers[i].Label, i == pick))
                        picks[n] = i;
                }

                ImGui.EndCombo();
            }
        }

        var chosen = picks.Take(tables).Select(i => Math.Clamp(i, 0, Math.Max(0, section.Dealers.Count - 1))).ToList();
        var distinct = chosen.Distinct().Count() == tables;
        var enough = section.Dealers.Count >= tables;
        ImGui.BeginDisabled(busy || t.Entries.Count < 2 || !enough || !distinct || !ImGui.GetIO().KeyCtrl);
        if (DealerControls.ColoredButton("Start", new Vector4(0.16f, 0.45f, 0.22f, 1f), new Vector2(90, 0)))
        {
            var body = JsonContent(new { dealers = chosen.Select(i => section.Dealers[i].Key).ToArray() });
            Send(venueId, HttpMethod.Post, $"{path}/start", body,
                 $"Started {t.Name}. Each dealer sees their players on their Table tab (with the web table on).");
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(t.Entries.Count < 2 ? "Register at least 2 players first."
                : !enough ? $"{tables} tables need {tables} dealers: add dealers to your venue first."
                : !distinct ? "Each table needs its own dealer."
                : "Hold Ctrl and click. Registration closes for good, and the server draws the tables and seats.\n" +
                  "Each dealer seats their players from their Table tab; the clock starts once every table is seated.");
    }

    private void DrawRunning(string venueId, TournamentView t, string path)
    {
        var (index, left, paused, started) = TournamentClient.Advance(t.Levels, t.Clock, t.Received, DateTime.UtcNow);
        var unseated = t.Tables.Count(x => !x.Seated && !x.Broken);
        if (unseated > 0 && !started)
            ImGui.TextColored(DealerControls.Gold, $"Waiting for {unseated} table{(unseated == 1 ? string.Empty : "s")} to seat their players.");
        else if (!started)
            ImGui.TextColored(DealerControls.Muted, "The clock hasn't started.");
        else
        {
            var level = t.Levels[index];
            ImGui.TextUnformatted($"{TournamentClient.LevelName(t.Levels, index)}{(level.IsBreak ? string.Empty : $": {level.Blinds}")}");
            ImGui.SameLine();
            ImGui.TextColored(paused ? DealerControls.Red : DealerControls.Good, paused ? $"paused, {TournamentClient.Clock(left)} left" : $"{TournamentClient.Clock(left)} left");
        }

        ImGui.BeginDisabled(busy);
        if (started)
        {
            if (ImGui.Button(paused ? "Resume clock" : "Pause clock"))
                Send(venueId, HttpMethod.Post, $"{path}/clock", JsonContent(new { @do = paused ? "resume" : "pause" }),
                     paused ? "The clock runs again." : "The clock is paused for every table.");
        }
        else
        {
            ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
            if (ImGui.Button("Start the clock"))
                Send(venueId, HttpMethod.Post, $"{path}/clock", JsonContent(new { @do = "start" }), "The clock is running: level 1.");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click: start it without waiting for the dealer to seat everyone.");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
        if (ImGui.Button("Cancel tournament"))
            Send(venueId, HttpMethod.Post, $"{path}/cancel", JsonContent(new { }), $"Cancelled {t.Name}. Refund every entry fee.");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click. The players leave the table; you refund the entry fees.");
        ImGui.EndDisabled();

        DrawHandForHand(venueId, t, path);
        DrawTables(venueId, t, path);

        foreach (var m in t.Moves)
            ImGui.TextColored(DealerControls.Gold, m.Kind == "send"
                ? $"Moving: {m.Player}, table {m.From} to table {m.To} (waiting for table {m.From} to send them)"
                : $"Moving: {m.Player}, table {m.From} to table {m.To} (on their way)");

        if (ImGui.BeginTable("##standings", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Player");
            ImGui.TableSetupColumn("Table", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Chips", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Place", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();
            foreach (var e in t.Entries.OrderBy(e => e.Place ?? 0).ThenByDescending(e => e.Chips))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(e.Place is null ? new Vector4(1, 1, 1, 1) : DealerControls.Muted, $"{e.Name}@{e.World}");
                ImGui.TableNextColumn();
                ImGui.TextColored(DealerControls.Muted, e.Place is not null ? string.Empty : e.Moving ? "moving" : e.Table is { } no ? $"{no}" : string.Empty);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(e.Place is null ? e.Chips.ToString("N0") : string.Empty);
                ImGui.TableNextColumn();
                ImGui.TextColored(DealerControls.Muted, e.Place is { } place ? $"out, {Json.Ordinal(place)}" : string.Empty);
            }

            ImGui.EndTable();
        }
    }

    /// <summary>Hand-for-hand: on the bubble by itself, or as the owner says.</summary>
    private void DrawHandForHand(string venueId, TournamentView t, string path)
    {
        ImGui.TextUnformatted("Hand-for-hand:");
        ImGui.SameLine();
        ImGui.TextColored(t.HandForHand ? DealerControls.Gold : DealerControls.Muted, t.HandForHand ? "on" : "off");
        ImGui.SameLine();
        var mode = t.HandForHandSet switch { true => 1, false => 2, _ => 0 };
        ImGui.BeginDisabled(busy);
        string[] labels = ["On the bubble", "On now", "Off"];
        for (var i = 0; i < labels.Length; i++)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{labels[i]}##h4h", mode == i) && mode != i)
            {
                bool? on = i switch { 1 => true, 2 => false, _ => null };
                Send(venueId, HttpMethod.Post, $"{path}/hand-for-hand", JsonContent(new { on }),
                     i switch { 1 => "Hand-for-hand is on: every table deals one hand, then waits for the others.", 2 => "Hand-for-hand is off.", _ => "Hand-for-hand starts by itself on the bubble." });
            }
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("On the bubble: it starts by itself when the next player out wins nothing, and stops once the rest are in the money.");
    }

    /// <summary>Each table: its dealer and players, and a way to give it to another dealer if its dealer can't go on.</summary>
    private void DrawTables(string venueId, TournamentView t, string path)
    {
        if (t.Tables.Count == 0 || !ImGui.BeginTable("##tables", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Table", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Dealer");
        ImGui.TableSetupColumn("Players", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("##give", ImGuiTableColumnFlags.WidthFixed, 300);
        ImGui.TableHeadersRow();
        var section = sections.GetValueOrDefault(venueId);
        foreach (var x in t.Tables)
        {
            ImGui.PushID(x.No);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{x.No}");
            ImGui.TableNextColumn();
            ImGui.TextColored(x.Broken ? DealerControls.Muted : new Vector4(1, 1, 1, 1), x.DealerName);
            ImGui.TableNextColumn();
            ImGui.TextColored(x.Broken ? DealerControls.Muted : x.Waiting ? DealerControls.Gold : new Vector4(1, 1, 1, 1),
                              x.Broken ? "closed" : !x.Seated ? $"{x.Players}, not seated yet" : x.Breaking ? $"{x.Players}, breaking up"
                              : x.Waiting ? $"{x.Players}, waiting (hand-for-hand)" : $"{x.Players}");
            if (!x.Broken && ImGui.IsItemHovered() && PlayersAt(t, x.No) is { Count: > 0 } here)
                ImGui.SetTooltip(string.Join("\n", here));
            ImGui.TableNextColumn();
            if (!x.Broken && section is { Dealers.Count: > 0 })
            {
                var key = $"{t.Id}/{x.No}";
                var pick = Math.Clamp(handover.GetValueOrDefault(key), 0, section.Dealers.Count - 1);
                ImGui.SetNextItemWidth(180);
                if (ImGui.BeginCombo("##to", section.Dealers[pick].Label))
                {
                    for (var i = 0; i < section.Dealers.Count; i++)
                    {
                        if (ImGui.Selectable(section.Dealers[i].Label, i == pick))
                            handover[key] = i;
                    }

                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl || section.Dealers[pick].Label.StartsWith(x.DealerName, StringComparison.OrdinalIgnoreCase));
                if (ImGui.SmallButton("Give table"))
                    Send(venueId, HttpMethod.Post, $"{path}/table", JsonContent(new { no = x.No, dealer = section.Dealers[pick].Key }),
                         $"Table {x.No} goes to {section.Dealers[pick].Label}: they seat its players from their Table tab.");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Hold Ctrl and click: for a dealer who can't come back. The new dealer seats the table's players\n" +
                                     "with the chips from its last hand reported; the old dealer's plugin lets the table go.");
            }

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (ImGui.SmallButton("Copy draw"))
        {
            ImGui.SetClipboardText(DrawText(t));
            Show("Copied who plays at each table: paste it in your Discord or chat.", false);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Every table with its dealer and players, to post for the players. Each dealer's plugin also tells\n" +
                             "its players by tell which table is theirs when the tournament starts, and the venue page shows it.");
    }

    /// <summary>The players still in at a table (as the server last said), by name.</summary>
    private static List<string> PlayersAt(TournamentView t, int no) =>
        t.Entries.Where(e => e.Place is null && e.Table == no).Select(e => e.Name).ToList();

    private static string DrawText(TournamentView t)
    {
        var lines = new List<string> { $"**{t.Name}**: who plays where" };
        foreach (var x in t.Tables.Where(x => !x.Broken))
            lines.Add($"Table {x.No} ({x.DealerName}): {string.Join(", ", PlayersAt(t, x.No))}");
        return string.Join("\n", lines);
    }

    private void DrawResults(string venueId, TournamentView t, string path)
    {
        if (t.Status == "cancelled")
            ImGui.TextColored(DealerControls.Red, $"Cancelled. Refund each player their entry fee ({t.Fee:N0} gil).");
        if (t.Status == "finished")
        {
            var league = sections.GetValueOrDefault(venueId)?.League;
            ImGui.TextColored(DealerControls.Muted, t.OnLeaderboard
                ? "On the server-wide leaderboard (its points come from the server's own table)."
                : t.Ranked
                    ? $"Not on the server-wide leaderboard: a ranked tournament needs {league?.MinPlayers ?? 6} players or more."
                    : "Not ranked: not on the server-wide leaderboard.");
            if (t.Series is { } sid && sections.GetValueOrDefault(venueId)?.Series.Find(s => s.Id == sid) is { } series)
            {
                ImGui.SameLine();
                ImGui.TextColored(DealerControls.Muted, $"In the series {series.Name} (the Points column).");
            }
        }

        if (t.Results.Count > 0 && ImGui.BeginTable("##results", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Place", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Player");
            ImGui.TableSetupColumn("Prize", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Points", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Counts", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("##pay", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableHeadersRow();
            foreach (var r in t.Results)
            {
                ImGui.PushID(r.Key);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Json.Ordinal(r.Place));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Name}@{r.World}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Prize > 0 ? r.Prize.ToString("N0") : "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(r.Removed ? DealerControls.Muted : new Vector4(1, 1, 1, 1), r.Points.ToString("0.#"));
                ImGui.TableNextColumn();
                var counts = !r.Removed;
                ImGui.BeginDisabled(busy || t.Status != "finished");
                if (ImGui.Checkbox("##counts", ref counts))
                    Send(venueId, HttpMethod.Post, $"{path}/result", JsonContent(new { key = r.Key, removed = !counts }),
                         counts ? $"{r.Name}'s points count again." : $"{r.Name}'s points are taken away (their place stays).");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Untick to take this player's points away, in the series and on the server-wide leaderboard\n" +
                                     "(a mistake, a disqualification). Their place and prize stay; the change is logged.");
                ImGui.TableNextColumn();
                if (r.Prize > 0)
                {
                    ImGui.BeginDisabled(r.Paid || plugin.TradeOpen);
                    if (ImGui.SmallButton("Trade"))
                    {
                        plugin.Trades.Expect(r.Name, -r.Prize, DateTime.UtcNow);
                        plugin.TryTradePayout(r.Name, r.Prize);
                        plugin.Record($"Tournament {t.Name}: paying {r.Name}@{r.World} {r.Prize:N0} gil for {Json.Ordinal(r.Place)} place.");
                    }

                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip($"Asks {r.Name} to trade (stand next to them) and types in their prize. You press Trade and Confirm.\n" +
                                         "Over 1,000,000 goes in parts: the rest waits under Gil to pay on the Players tab.");
                    ImGui.SameLine();
                    var paid = r.Paid;
                    ImGui.BeginDisabled(busy);
                    if (ImGui.Checkbox("Paid", ref paid))
                        Send(venueId, HttpMethod.Post, $"{path}/paid", JsonContent(new { key = r.Key, paid }),
                             paid ? $"{r.Name}'s prize is marked paid." : $"{r.Name}'s prize is marked unpaid.");
                    ImGui.EndDisabled();
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl);
        if (ImGui.SmallButton("Delete"))
            Send(venueId, HttpMethod.Delete, path, null, $"Deleted {t.Name}.");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click: remove it and its results.");
    }

    /// <summary>
    /// The commentator link: made here (the token stays in this plugin and the link; the server only gets its hash),
    /// copied to give to the caster, or replaced if it leaked. And the table the view opens on.
    /// </summary>
    private void DrawCommentator(string venueId, TournamentView t, string path)
    {
        ImGui.TextColored(DealerControls.Gold, "Commentator");
        ImGui.SameLine();
        var tokens = plugin.Configuration.CommentatorTokens;
        var token = tokens.GetValueOrDefault(t.Id);
        ImGui.BeginDisabled(busy);
        if (!t.CommentatorLinked || token is null)
        {
            if (ImGui.SmallButton(t.CommentatorLinked ? "Make a new link (this PC has lost it)" : "Make the link"))
                NewCommentatorLink(venueId, t, path);
        }
        else
        {
            if (ImGui.SmallButton("Copy link"))
            {
                ImGui.SetClipboardText($"{BaseUrl}/c/{t.Id}#{token}");
                Show("Copied the commentator link: give it to your caster only. It opens in one browser.", false);
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("New link"))
                NewCommentatorLink(venueId, t, path);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click: the old link stops working at once (it leaked, or the caster changes browser).");
            ImGui.SameLine();
            ImGui.TextColored(DealerControls.Muted, t.CommentatorOpened ? "opened in a browser" : "not opened yet");
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(DealerControls.Muted, t.Delay < 0 ? "(after each hand)" : $"({t.Delay} min behind)");
        DrawOverlay(venueId, t, path);

        var open = t.Tables.Where(x => !x.Broken).ToList();
        if (t.Status == "running" && open.Count > 1)
        {
            ImGui.SetNextItemWidth(160);
            var current = open.Find(x => x.No == t.Feature);
            if (ImGui.BeginCombo("Feature table##feature", current is null ? "(any)" : $"Table {current.No}"))
            {
                if (ImGui.Selectable("(any)", current is null))
                    Send(venueId, HttpMethod.Post, $"{path}/commentator", JsonContent(new { feature = (int?)null }), "The commentator view opens on any table.");
                foreach (var x in open)
                {
                    if (ImGui.Selectable($"Table {x.No} ({x.DealerName})", x.No == t.Feature))
                        Send(venueId, HttpMethod.Post, $"{path}/commentator", JsonContent(new { feature = x.No }), $"Table {x.No} is the feature table.");
                }

                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The table the commentator view shows first. The caster can still pick another.");
        }
    }

    /// <summary>
    /// The stream overlay link: the commentator view made for a streamer's OBS (a browser source), with the same
    /// delay. A second link with its own key, so it can be given out (or replaced) apart from the commentator's.
    /// </summary>
    private void DrawOverlay(string venueId, TournamentView t, string path)
    {
        ImGui.TextColored(DealerControls.Gold, "Stream overlay");
        ImGui.SameLine();
        var token = plugin.Configuration.OverlayTokens.GetValueOrDefault(t.Id);
        ImGui.BeginDisabled(busy);
        if (!t.OverlayLinked || token is null)
        {
            if (ImGui.SmallButton(t.OverlayLinked ? "Make a new link (this PC has lost it)##overlay" : "Make the link##overlay"))
                NewOverlayLink(venueId, t, path);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A link for a streamer's OBS: the feature table, the clock and the chip leaders over their stream, with the same delay.");
        }
        else
        {
            if (ImGui.SmallButton("Copy link##overlay"))
            {
                ImGui.SetClipboardText($"{BaseUrl}/o/{t.Id}#{token}");
                Show("Copied the overlay link. In OBS: add a Browser source, paste it as the URL, 1920 x 1080. It opens in one browser only, so paste it straight into OBS.", false);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("For OBS: a Browser source, 1920 x 1080. Opening it in a normal browser first uses it up (it opens in one only).\n" +
                                 "Add ?show=bar,table,leaders,events before the # to pick the parts, or ?table=2 to always show one table.");
            ImGui.SameLine();
            ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("New link##overlay"))
                NewOverlayLink(venueId, t, path);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click: the old overlay link stops working at once (it leaked, or the streamer changes PC).");
            ImGui.SameLine();
            ImGui.TextColored(DealerControls.Muted, t.OverlayOpened ? "opened in a browser" : "not opened yet");
        }

        ImGui.EndDisabled();
    }

    private void NewOverlayLink(string venueId, TournamentView t, string path)
    {
        var token = WebPublisher.NewId(24);
        Send(venueId, HttpMethod.Post, $"{path}/commentator", JsonContent(new { lookup = TexasPoker.Engine.CommentatorSealer.Lookup(t.Id, token), overlay = true }),
             "Made a new overlay link. Copy it for the streamer; the old one stopped working.", () =>
             {
                 plugin.Configuration.OverlayTokens[t.Id] = token;
                 plugin.Save();
             });
    }

    private void NewCommentatorLink(string venueId, TournamentView t, string path)
    {
        var token = WebPublisher.NewId(24);
        Send(venueId, HttpMethod.Post, $"{path}/commentator", JsonContent(new { lookup = TexasPoker.Engine.CommentatorSealer.Lookup(t.Id, token) }),
             "Made a new commentator link. Copy it for your caster; the old one stopped working.", () =>
             {
                 plugin.Configuration.CommentatorTokens[t.Id] = token;
                 plugin.Save();
             });
    }

    private string newSeries = string.Empty;

    /// <summary>The venue's series: its own leagues, with leaderboards on the venue page.</summary>
    private void DrawSeries(string venueId, Section section)
    {
        if (!ImGui.TreeNode("Series and leaderboards##series"))
            return;
        ImGui.TextWrapped("A series is your venue's own league: put tournaments in it (in their settings) and their series points add up " +
                          "on your venue page. Every ranked tournament also counts on the server-wide leaderboard.");
        if (section.League is { } league)
        {
            ImGui.TextColored(DealerControls.Gold, "Server-wide leaderboard");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy link##league"))
            {
                ImGui.SetClipboardText(league.Page);
                Show("Copied the leaderboard link.", false);
            }
        }

        foreach (var s in section.Series)
        {
            ImGui.PushID(s.Id);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted($"{s.Name}{(s.Closed ? " (closed)" : string.Empty)}");
            ImGui.SameLine();
            ImGui.TextColored(DealerControls.Muted, s.Tournaments == 0 ? "no results yet"
                : $"{s.Tournaments} tournament{(s.Tournaments == 1 ? string.Empty : "s")}" + (s.Leader is { } leader ? $", led by {leader}" : string.Empty));
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if (ImGui.SmallButton(s.Closed ? "Open again" : "Close"))
                Send(venueId, HttpMethod.Post, $"/api/venue/{venueId}/series/{s.Id}/close", JsonContent(new { closed = !s.Closed }),
                     s.Closed ? $"{s.Name} is open again." : $"{s.Name} is closed: its leaderboard stays up, frozen.");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(s.Closed ? "New tournaments can go in it again." : "No new tournaments go in it; its leaderboard stays on your venue page.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy CSV link"))
            {
                ImGui.SetClipboardText(s.Csv);
                Show($"Copied the link to {s.Name}'s leaderboard as a CSV file (for a spreadsheet or Discord).", false);
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("Delete"))
                Send(venueId, HttpMethod.Delete, $"/api/venue/{venueId}/series/{s.Id}", null, $"Deleted {s.Name}.");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click. Only a series with no tournaments in it; close one that has some.");
            ImGui.PopID();
        }

        ImGui.BeginDisabled(busy);
        ImGui.SetNextItemWidth(220);
        var enter = ImGui.InputTextWithHint("##newseries", "Winter League", ref newSeries, 60, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("New series") || enter) && newSeries.Trim().Length > 0)
        {
            Send(venueId, HttpMethod.Post, $"/api/venue/{venueId}/series", JsonContent(new { name = newSeries.Trim() }),
                 $"Made the series {newSeries.Trim()}. Pick it in a tournament's settings.");
            newSeries = string.Empty;
        }

        ImGui.EndDisabled();
        ImGui.TreePop();
    }

    private void Register(string venueId, TournamentView t, string who)
    {
        Send(venueId, HttpMethod.Post, $"/api/venue/{venueId}/tournaments/{t.Id}/register", JsonContent(new { who }), $"Registered {who}.", () =>
        {
            // Their entry fee, traded just before: it's for the tournament, not the table.
            var name = who.Split('@')[0];
            if (plugin.Trades.Waiting.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Amount == t.Fee) is { } trade)
            {
                plugin.Trades.Dismiss(trade);
                Show($"Registered {who}. Their {t.Fee:N0} gil trade is their entry fee.", false);
            }

            plugin.Record($"Tournament {t.Name}: registered {who} (entry fee {t.Fee:N0} gil).");
        });
    }

    // ---- the setup form -----------------------------------------------------------------------------

    private void DrawEditor(string venueId, Section section, Draft d)
    {
        ImGui.Separator();
        ImGui.TextColored(DealerControls.Gold, d.Id is null ? "New tournament" : $"Edit {d.Name}");
        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Name", ref d.Name, 60);
        ImGui.SetNextItemWidth(140);
        DealerControls.InputLong("Entry fee (gil)", ref d.Fee);
        ImGui.Checkbox("Set the prize pool yourself", ref d.OwnPool);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: the prize pool is every entry fee added up. On: a pool you pick, for example when the venue adds gil.");
        if (d.OwnPool)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            DealerControls.InputLong("##pool", ref d.Pool);
        }

        ImGui.SetNextItemWidth(140);
        DealerControls.InputLong("Starting chips", ref d.Chips);

        ImGui.Spacing();
        ImGui.TextUnformatted("Blind structure");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        var presetName = section.Presets.Count > 0 ? section.Presets[Math.Clamp(d.Preset, 0, section.Presets.Count - 1)].Name : "-";
        if (ImGui.BeginCombo("##preset", presetName))
        {
            for (var i = 0; i < section.Presets.Count; i++)
            {
                if (ImGui.Selectable(section.Presets[i].Name, i == d.Preset))
                    d.Preset = i;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Use this structure") && section.Presets.Count > 0)
        {
            var p = section.Presets[Math.Clamp(d.Preset, 0, section.Presets.Count - 1)];
            d.Levels = p.Levels.Select(LevelRow.From).ToList();
            d.Chips = p.Chips;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turbo: 8 min levels. Regular: 12 min. Deep: 15 min and 20,000 chips. Antes from level 4. Replaces the levels below.");

        DrawLevels(d);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Prize split (%)", ref d.Prizes, 120);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Percent of the prize pool for 1st, 2nd, 3rd...: they must add up to 100. For example 50, 30, 20.");
        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Series points by place", ref d.Points, 200);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Points for 1st, 2nd, 3rd... in your venue's series; places after the last one listed get 0.\n" +
                             "The server-wide leaderboard uses its own table, the same for every venue.");

        ImGui.Checkbox("Ranked (counts on the server-wide leaderboard)", ref d.Ranked);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(section.League is { } league
                ? $"Points by place, the same for every venue: {string.Join(", ", league.Points.Take(10).Select((p, i) => $"{Json.Ordinal(i + 1)} {p}"))}.\n" +
                  $"It counts once it's finished with {league.MinPlayers} players or more. Untick for a casual or test tournament."
                : "Counts on the server-wide leaderboard once finished.");

        var open = section.Series.Where(s => !s.Closed || s.Id == d.Series).ToList();
        var current = open.Find(s => s.Id == d.Series);
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Series", current?.Name ?? "(none)"))
        {
            if (ImGui.Selectable("(none)", current is null))
                d.Series = null;
            foreach (var s in open)
            {
                if (ImGui.Selectable(s.Name, s.Id == d.Series))
                    d.Series = s.Id;
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Your venue's own league: its tournaments add up their series points on your venue page. Make one under Series.");

        ImGui.Checkbox("Commentator view", ref d.Commentator);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A secret link for a caster: every table's hole cards, each player's chance to win and their hand, on a delay.");
        if (d.Commentator)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(170);
            string DelayText(int m) => m < 0 ? "After each hand" : $"{m} minute{(m == 1 ? string.Empty : "s")} behind";
            if (ImGui.BeginCombo("##delay", DelayText(d.Delay)))
            {
                foreach (var m in new[] { -1, 1, 2, 3, 5, 10 })
                {
                    if (ImGui.Selectable(DelayText(m), m == d.Delay))
                        d.Delay = m;
                }

                ImGui.EndCombo();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How far the commentator view runs behind the tables, so nobody at a table can be told what it shows.\n" +
                                 "After each hand: a hand plays back once it's over. If the commentary is streamed, the stream needs its own delay too.");
        }

        ImGui.BeginDisabled(busy);
        if (ImGui.Button(d.Id is null ? "Create" : "Save"))
        {
            if (d.Body() is { } body)
            {
                Send(venueId, HttpMethod.Post, $"/api/venue/{venueId}/tournaments", JsonContent(body),
                     d.Id is null ? $"Created {d.Name}. Register the players at the start of the event." : $"Saved {d.Name}.",
                     () => editing = null);
            }
            else
            {
                Show("The prize split and the points must be numbers separated by commas.", true);
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel##edit"))
            editing = null;
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawLevels(Draft d)
    {
        if (!ImGui.BeginTable("##levels", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
                              new Vector2(0, Math.Min(260, 30 + d.Levels.Count * 27))))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Break", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableSetupColumn("Small blind", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Big blind", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Ante", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Minutes", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        var number = 0;
        var remove = -1;
        for (var i = 0; i < d.Levels.Count; i++)
        {
            var l = d.Levels[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(l.Break ? "-" : $"{++number}");
            ImGui.TableNextColumn();
            ImGui.Checkbox("##break", ref l.Break);
            ImGui.TableNextColumn();
            if (!l.Break)
            {
                ImGui.SetNextItemWidth(-1);
                DealerControls.InputLong("##sb", ref l.Sb);
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                DealerControls.InputLong("##bb", ref l.Bb);
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                DealerControls.InputLong("##ante", ref l.Ante);
            }
            else
            {
                ImGui.TextColored(DealerControls.Muted, "Break");
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputInt("##min", ref l.Minutes, 0);
            l.Minutes = Math.Clamp(l.Minutes, 1, 120);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Remove"))
                remove = i;
            ImGui.PopID();
        }

        ImGui.EndTable();
        if (remove >= 0)
            d.Levels.RemoveAt(remove);

        if (ImGui.SmallButton("Add a level"))
        {
            var last = d.Levels.LastOrDefault(l => !l.Break);
            d.Levels.Add(last is null
                ? new LevelRow { Sb = 50, Bb = 100, Minutes = 12 }
                : new LevelRow { Sb = last.Sb * 2, Bb = last.Bb * 2, Ante = last.Ante * 2, Minutes = last.Minutes });
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Add a break"))
            d.Levels.Add(new LevelRow { Break = true, Minutes = 10 });
    }

    // ---- the server ---------------------------------------------------------------------------------

    private static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private void Load(string venueId) => Send(venueId, HttpMethod.Get, $"/api/venue/{venueId}/tournaments", null, null);

    /// <summary>Every reply is the venue's whole Tournaments section, which replaces what's shown.</summary>
    private void Send(string venueId, HttpMethod method, string path, HttpContent? content, string? success, Action? after = null)
    {
        var key = plugin.Configuration.AuthSessionKey;
        busy = true;
        _ = Task.Run(async () =>
        {
            Section? section = null;
            string? error = null;
            try
            {
                using var request = new HttpRequestMessage(method, BaseUrl + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = content;
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(text);
                    section = Section.Parse(doc.RootElement);
                }
                else
                {
                    error = text.Length > 0 ? text : $"The server said {(int)response.StatusCode}.";
                }
            }
            catch (Exception ex)
            {
                error = ex is JsonException ? "The server sent something this plugin doesn't understand." : $"Can't reach the server: {ex.Message}";
            }

            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                busy = false;
                if (section is not null)
                {
                    sections[venueId] = section;
                    if (success is not null)
                        Show(success, false);
                    after?.Invoke();
                }
                else if (error is not null)
                {
                    Show(error, true);
                    // Don't hammer a server that keeps refusing: wait for the next refresh.
                    if (!sections.ContainsKey(venueId))
                        sections[venueId] = new Section([], [], 7, []) { Loaded = DateTime.UtcNow };
                }
            }).ConfigureAwait(false);
        });
    }

    private void Show(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    // ---- what the server sends ------------------------------------------------------------------------

    private sealed record Series(string Id, string Name, bool Closed, int Tournaments, string? Leader, string Csv);

    private sealed record LeagueRules(int[] Points, int MinPlayers, string Page);

    private sealed record Section(List<TournamentView> List, List<Preset> Presets, int MaxPlayers, List<(string Key, string Label)> Dealers)
    {
        public List<Series> Series { get; init; } = [];

        public LeagueRules? League { get; init; }

        public DateTime Loaded { get; init; } = DateTime.UtcNow;

        public int[] DefaultPrizes { get; init; } = [50, 30, 20];

        public int[] DefaultPoints { get; init; } = [100, 70, 50, 40, 30, 20, 10, 10, 10, 10];

        public static Section Parse(JsonElement root)
        {
            var now = DateTime.UtcNow;
            var list = root.TryGetProperty("tournaments", out var ts) && ts.ValueKind == JsonValueKind.Array
                ? ts.EnumerateArray().Select(t => TournamentView.Parse(t, now)).ToList()
                : [];
            var presets = root.TryGetProperty("presets", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().Select(p => new Preset(Json.Str(p, "name"), Json.Long(p, "chips"),
                                                               p.TryGetProperty("levels", out var l) ? TournamentLevel.ParseAll(l) : [])).ToList()
                : [];
            var dealers = root.TryGetProperty("dealers", out var ds) && ds.ValueKind == JsonValueKind.Array
                ? ds.EnumerateArray().Select(x => (Json.Str(x, "key"), Json.Str(x, "label"))).ToList()
                : [];
            return new Section(list, presets, Math.Max(2, (int)Json.Long(root, "maxPlayers")), dealers)
            {
                DefaultPrizes = Ints(root, "prizes") is { Length: > 0 } prizes ? prizes : [50, 30, 20],
                DefaultPoints = Ints(root, "points") is { Length: > 0 } points ? points : [100, 70, 50, 40, 30, 20, 10, 10, 10, 10],
                Series = root.TryGetProperty("series", out var ss) && ss.ValueKind == JsonValueKind.Array
                    ? ss.EnumerateArray().Select(s => new Series(Json.Str(s, "id"), Json.Str(s, "name"), True(s, "closed"), (int)Json.Long(s, "tournaments"),
                        s.TryGetProperty("leaders", out var ls) && ls.ValueKind == JsonValueKind.Array && ls.GetArrayLength() > 0 ? Json.Str(ls[0], "name") : null,
                        Json.Str(s, "csv"))).ToList()
                    : [],
                League = root.TryGetProperty("league", out var lg) && lg.ValueKind == JsonValueKind.Object
                    ? new LeagueRules(Ints(lg, "points"), (int)Json.Long(lg, "minPlayers"), Json.Str(lg, "page"))
                    : null,
            };
        }
    }

    private static bool True(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int[] Ints(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble()).ToArray()
            : [];

    private sealed record Preset(string Name, long Chips, List<TournamentLevel> Levels);

    private sealed record Entry(string Key, string Name, string World, long Chips, int? Place, int? Table, bool Moving);

    private sealed record TableView(int No, string DealerName, bool Seated, bool Broken, bool Breaking, int Players, bool Waiting);

    private sealed record MoveView(string Player, string Kind, int From, int To);

    private sealed record Result(string Key, string Name, string World, int Place, long Prize, double Points, bool Paid, bool Removed);

    private sealed record TournamentView(
        string Id, string Name, string Status, long Fee, long? Pool, long PoolNow, long Chips, List<TournamentLevel> Levels,
        int[] Prizes, int[] Points, int TablesNeeded, List<TableView> Tables, List<MoveView> Moves, bool HandForHand, bool? HandForHandSet,
        List<Entry> Entries, int PlayersLeft, TournamentClock? Clock, List<Result> Results, List<string> Events, DateTime Received)
    {
        public bool Ranked { get; init; } = true;

        public string? Series { get; init; }

        public bool OnLeaderboard { get; init; }

        public bool CommentatorOn { get; init; }

        public bool CommentatorLinked { get; init; }

        public bool CommentatorOpened { get; init; }

        public bool OverlayLinked { get; init; }

        public bool OverlayOpened { get; init; }

        public int? Feature { get; init; }

        public int Delay { get; init; } = 2;

        public static TournamentView Parse(JsonElement t, DateTime now)
        {
            var entries = t.TryGetProperty("entries", out var es) && es.ValueKind == JsonValueKind.Array
                ? es.EnumerateArray().Select(e => new Entry(Json.Str(e, "key"), Json.Str(e, "name"), Json.Str(e, "world"), Json.Long(e, "chips"),
                                                            e.TryGetProperty("place", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null,
                                                            e.TryGetProperty("table", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt32() : null,
                                                            True(e, "moving"))).ToList()
                : [];
            var tables = t.TryGetProperty("tables", out var ts) && ts.ValueKind == JsonValueKind.Array
                ? ts.EnumerateArray().Select(x => new TableView((int)Json.Long(x, "no"), Json.Str(x, "dealerName"), True(x, "seated"), True(x, "broken"),
                                                                True(x, "breaking"), (int)Json.Long(x, "players"), True(x, "waiting"))).ToList()
                : [];
            var moves = t.TryGetProperty("moves", out var ms) && ms.ValueKind == JsonValueKind.Array
                ? ms.EnumerateArray().Select(m => new MoveView(Json.Str(m, "player"), Json.Str(m, "kind"), (int)Json.Long(m, "from"), (int)Json.Long(m, "to"))).ToList()
                : [];
            bool? set = t.TryGetProperty("handForHandSet", out var hs) ? hs.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
            var results = t.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array
                ? rs.EnumerateArray().Select(r => new Result(Json.Str(r, "key"), Json.Str(r, "name"), Json.Str(r, "world"), (int)Json.Long(r, "place"),
                                                             Json.Long(r, "prize"),
                                                             r.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Number ? pts.GetDouble() : 0,
                                                             r.TryGetProperty("paid", out var paid) && paid.ValueKind == JsonValueKind.True,
                                                             True(r, "removed"))).ToList()
                : [];
            var events = t.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array
                ? ev.EnumerateArray().Select(x =>
                {
                    var at = DateTimeOffset.FromUnixTimeMilliseconds(Json.Long(x, "at")).ToLocalTime();
                    return $"{at:MMM d HH:mm}  {Json.Str(x, "text")}";
                }).Reverse().ToList()
                : [];
            return new TournamentView(
                Json.Str(t, "id"), Json.Str(t, "name"), Json.Str(t, "status"), Json.Long(t, "fee"),
                t.TryGetProperty("pool", out var pool) && pool.ValueKind == JsonValueKind.Number ? pool.GetInt64() : null,
                Json.Long(t, "poolNow"), Json.Long(t, "chips"),
                t.TryGetProperty("levels", out var levels) ? TournamentLevel.ParseAll(levels) : [],
                Ints(t, "prizes"), Ints(t, "points"), (int)Json.Long(t, "tablesNeeded"), tables, moves, True(t, "handForHand"), set,
                entries, (int)Json.Long(t, "playersLeft"),
                t.TryGetProperty("clock", out var clock) ? TournamentClock.Parse(clock) : null,
                results, events, now)
            {
                Ranked = !t.TryGetProperty("ranked", out var rk) || rk.ValueKind != JsonValueKind.False,
                Series = Json.Str(t, "series") is { Length: > 0 } sid ? sid : null,
                OnLeaderboard = True(t, "onLeaderboard"),
                CommentatorOn = t.TryGetProperty("commentator", out var cm) && True(cm, "on"),
                CommentatorLinked = t.TryGetProperty("commentator", out var cl) && True(cl, "linked"),
                CommentatorOpened = t.TryGetProperty("commentator", out var co) && True(co, "opened"),
                OverlayLinked = t.TryGetProperty("commentator", out var ol) && True(ol, "overlayLinked"),
                OverlayOpened = t.TryGetProperty("commentator", out var oo) && True(oo, "overlayOpened"),
                Feature = t.TryGetProperty("commentator", out var cf) && cf.TryGetProperty("feature", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetInt32() : null,
                Delay = (int)Json.Long(t, "delay"),
            };
        }
    }

    private sealed class LevelRow
    {
        public long Sb;
        public long Bb;
        public long Ante;
        public int Minutes = 12;
        public bool Break;

        public static LevelRow From(TournamentLevel l) => new() { Sb = l.SmallBlind, Bb = l.BigBlind, Ante = l.Ante, Minutes = l.Minutes, Break = l.IsBreak };
    }

    private sealed class Draft
    {
        public string? Id;
        public string Name = string.Empty;
        public long Fee = 100_000;
        public bool OwnPool;
        public long Pool;
        public long Chips = 10_000;
        public int Preset = 1;
        public List<LevelRow> Levels = [];
        public string Prizes = "50, 30, 20";
        public string Points = "100, 70, 50, 40, 30, 20, 10, 10, 10, 10";
        public bool Ranked = true;
        public string? Series;
        public bool Commentator;
        public int Delay = 2;

        public static Draft New(Section section)
        {
            var d = new Draft
            {
                Name = $"Tournament {DateTime.Now:MMM d}",
                Prizes = string.Join(", ", section.DefaultPrizes),
                Points = string.Join(", ", section.DefaultPoints),
            };
            var regular = section.Presets.Count > 1 ? section.Presets[1] : section.Presets.FirstOrDefault();
            if (regular is not null)
            {
                d.Levels = regular.Levels.Select(LevelRow.From).ToList();
                d.Chips = regular.Chips;
            }

            return d;
        }

        public static Draft From(TournamentView t) => new()
        {
            Id = t.Id, Name = t.Name, Fee = t.Fee, OwnPool = t.Pool is not null, Pool = t.Pool ?? 0, Chips = t.Chips,
            Levels = t.Levels.Select(LevelRow.From).ToList(), Prizes = string.Join(", ", t.Prizes), Points = string.Join(", ", t.Points),
            Ranked = t.Ranked, Series = t.Series, Commentator = t.CommentatorOn, Delay = t.Delay == 0 ? 2 : t.Delay,
        };

        /// <summary>The settings as the server takes them, or null if the prize split or points don't read as numbers.</summary>
        public object? Body()
        {
            static int[]? Numbers(string text)
            {
                var parts = text.Split([',', ' ', ';', '/'], StringSplitOptions.RemoveEmptyEntries);
                var result = new int[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!int.TryParse(parts[i].TrimEnd('%'), out result[i]))
                        return null;
                }

                return result.Length > 0 ? result : null;
            }

            if (Numbers(Prizes) is not { } prizes || Numbers(Points) is not { } points)
                return null;
            return new
            {
                id = Id,
                name = Name.Trim(),
                fee = Fee,
                pool = OwnPool ? Pool : (long?)null,
                chips = Chips,
                levels = Levels.Select(l => l.Break
                    ? (object)new { @break = true, minutes = l.Minutes }
                    : new { sb = l.Sb, bb = l.Bb, ante = l.Ante, minutes = l.Minutes }),
                prizes,
                points,
                ranked = Ranked,
                series = Series,
                commentator = Commentator,
                delay = Delay,
            };
        }
    }
}
