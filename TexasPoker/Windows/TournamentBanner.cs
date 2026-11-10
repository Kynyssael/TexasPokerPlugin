using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TexasPoker.Windows;

/// <summary>
/// The top of the Table tab while this table deals a tournament: the level, blinds, clock and players left, and what
/// the server asks of this dealer: seat the drawn players, send a player to another table, seat one who arrives, or
/// wait for the other tables (hand-for-hand).
/// </summary>
public sealed class TournamentBanner
{
    private static readonly Vector4 Go = new(0.16f, 0.45f, 0.22f, 1f);
    private static readonly Vector4 Move = new(0.68f, 0.52f, 0.08f, 1f);

    private readonly Plugin plugin;
    private readonly DealerControls controls;

    public TournamentBanner(Plugin plugin, DealerControls controls)
    {
        this.plugin = plugin;
        this.controls = controls;
    }

    public void Draw()
    {
        var tournament = plugin.Tournament;
        if (tournament.SeatingWaiting is { } seat && tournament.Current is { } waiting)
        {
            ImGui.TextColored(DealerControls.Gold, $"Tournament: {waiting.Name}{(waiting.Tables > 1 ? $", table {waiting.TableNo}" : string.Empty)}");
            ImGui.TextWrapped($"You deal this table. Seat these {seat.Players.Count} players: " +
                              $"{string.Join(", ", seat.Players.Select(p => p.Chips > 0 && p.Chips != waiting.Chips ? $"{p.Name} ({p.Chips:N0})" : p.Name))}" +
                              $" ({waiting.Chips:N0} chips each to start).");
            var occupied = plugin.Table.Seats.Count > 0;
            ImGui.BeginDisabled(occupied || plugin.Table.HandInProgress);
            if (DealerControls.ColoredButton("Seat the players", Go, new Vector2(160, 28)))
                controls.Do(tournament.SeatPlayers);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(occupied
                    ? "A tournament table starts empty: cash out everyone seated first."
                    : "Seats them in this order, with the drawn button. The clock starts once every table is seated.");
            DrawNotice();
            ImGui.Separator();
            return;
        }

        if (!tournament.Active)
        {
            DrawNotice();
            return;
        }

        if (tournament.Current is not { Status: "running" } t || t.Id != plugin.Configuration.TournamentId)
        {
            ImGui.TextColored(DealerControls.Gold, "Tournament table");
            ImGui.SameLine();
            ImGui.TextColored(plugin.Web.IsConfigured ? DealerControls.Muted : DealerControls.Red,
                              plugin.Web.IsConfigured
                                  ? "Waiting for the tournament server..."
                                  : "Turn the web table on (Web tab): the tournament runs through the server.");
            DrawNotice();
            ImGui.Separator();
            return;
        }

        var (index, left, paused, started) = tournament.LevelNow(DateTime.UtcNow);
        ImGui.TextColored(DealerControls.Gold, t.Tables > 1 ? $"{t.Name}, table {t.TableNo} of {t.Tables}" : t.Name);
        ImGui.SameLine();
        if (!started)
        {
            ImGui.TextColored(DealerControls.Muted, "Waiting for the clock: it starts once every table is seated.");
        }
        else
        {
            var level = t.Levels[index];
            var blinds = tournament.BlindsAt(index);
            ImGui.TextUnformatted(level.IsBreak
                ? $"Break, {TournamentClient.Clock(left)} left. Then {blinds?.Blinds}"
                : $"{tournament.LevelName(index)}: {level.Blinds}");
            ImGui.SameLine();
            ImGui.TextColored(paused ? DealerControls.Red : DealerControls.Good,
                              paused ? "Clock paused" : level.IsBreak ? string.Empty : $"{TournamentClient.Clock(left)} left");
            if (!level.IsBreak && index < t.Levels.Count - 1 && tournament.BlindsAt(index + 1) is { } next)
            {
                ImGui.SameLine();
                ImGui.TextColored(DealerControls.Muted, t.Levels[index + 1].IsBreak ? "(then a break)" : $"(next {next.Blinds})");
            }
        }

        ImGui.SameLine(0, 16);
        ImGui.TextColored(DealerControls.Muted, $"{t.PlayersLeft} of {t.Entries} players left");

        if (t.HandForHand)
        {
            ImGui.TextColored(t.MayDeal ? DealerControls.Gold : DealerControls.Red,
                              t.MayDeal
                                  ? "Hand-for-hand: deal one hand, then wait for the other tables."
                                  : "Hand-for-hand: waiting for the other tables to finish their hand.");
        }

        DrawMove(tournament);
        DrawMismatch(tournament);
        ImGui.TextColored(DealerControls.Muted,
                          "Chips, not gil: no buy-ins, rebuys or cash-outs. Players away are dealt in and folded. Each level starts with the next hand.");
        DrawNotice();
        ImGui.Separator();
    }

    /// <summary>The next player to send to another table, or to seat here.</summary>
    private void DrawMove(TournamentClient tournament)
    {
        if (tournament.NextMove is not { Player: { } who } move)
            return;
        var inHand = plugin.Table.HandInProgress;
        var text = move.Kind == "send"
            ? $"Move {who.Name} to table {move.To} ({move.ToDealer})."
            : $"{who.Name} is coming from table {move.From} with {move.Chips:N0} chips.";
        ImGui.TextColored(DealerControls.Gold, text);
        ImGui.SameLine();
        ImGui.BeginDisabled(inHand);
        if (DealerControls.ColoredButton(move.Kind == "send" ? $"Send {who.Name}" : $"Seat {who.Name}", move.Kind == "send" ? Move : Go,
                                         new Vector2(0, 0)))
            controls.Do(tournament.DoMove);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(inHand
                ? "Players move between hands: finish this one first."
                : move.Kind == "send"
                    ? $"Removes {who.Name} with their chips and tells them by /tell where to go. You can't deal until they're sent."
                    : $"Seats {who.Name} just before the button, once they're here. They're dealt in from the next hand.");
    }

    private void DrawMismatch(TournamentClient tournament)
    {
        if (tournament.Mismatch is not { } mismatch)
            return;
        ImGui.TextColored(DealerControls.Red, $"This table and the server disagree: {mismatch}");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
        if (ImGui.SmallButton("Use the server's stacks"))
            controls.Do(tournament.UseServerStacks);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click: the players and chips become what the server has after the last hand reported.\n" +
                             "Check with the owner first: if this table is right, tell them instead.");
    }

    private void DrawNotice()
    {
        if (plugin.Tournament.Notice is not { } notice)
            return;
        ImGui.TextColored(DealerControls.Red, notice);
        ImGui.SameLine();
        if (ImGui.SmallButton("OK##tnotice"))
            plugin.Tournament.Notice = null;
    }
}
