using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TexasPoker.Windows;

/// <summary>
/// The top of the Table tab while this table deals a tournament: the level, blinds, clock and players left, or the
/// drawn players to seat when the owner has just started it.
/// </summary>
public sealed class TournamentBanner
{
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
        if (tournament.WaitingToSeat && tournament.Current is { Seating: { } drawn } waiting)
        {
            ImGui.TextColored(DealerControls.Gold, $"Tournament: {waiting.Name}");
            ImGui.TextWrapped($"The owner started it, with you dealing. Seat the {drawn.Count} players the server drew " +
                              $"({waiting.Chips:N0} chips each): {string.Join(", ", drawn.Select(p => p.Name))}.");
            var occupied = plugin.Table.Seats.Count > 0;
            ImGui.BeginDisabled(occupied || plugin.Table.HandInProgress);
            if (DealerControls.ColoredButton("Seat the players", new Vector4(0.16f, 0.45f, 0.22f, 1f), new Vector2(160, 28)))
                controls.Do(tournament.SeatPlayers);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(occupied
                    ? "A tournament table starts empty: cash out everyone seated first."
                    : "Seats them in the drawn order, with the drawn button. The clock starts once they're seated.");
            DrawNotice();
            ImGui.Separator();
            return;
        }

        if (!tournament.Active)
            return;

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
        ImGui.TextColored(DealerControls.Gold, t.Name);
        ImGui.SameLine();
        if (!started)
        {
            ImGui.TextColored(DealerControls.Muted, "Waiting for the clock to start.");
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
        ImGui.TextColored(DealerControls.Muted,
                          "Chips, not gil: no buy-ins, rebuys or cash-outs. Players sitting out are dealt in and folded. Each level starts with the next hand.");
        DrawNotice();
        ImGui.Separator();
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
