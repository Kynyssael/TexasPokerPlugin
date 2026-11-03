using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

/// <summary>The main window's Players tab: seating players, rebuys, cash-outs and the gil ledger.</summary>
public sealed class PlayersTab
{
    private static readonly Vector4 AddColor = new(0.16f, 0.45f, 0.22f, 1f);
    private static readonly Vector4 TakeColor = new(0.55f, 0.18f, 0.18f, 1f);
    private static readonly Vector4 PayColor = new(0.68f, 0.52f, 0.08f, 1f);

    private readonly Plugin plugin;
    private readonly DealerControls controls;
    private string newName = string.Empty;
    private string newWorld = string.Empty;
    private long newBuyIn;
    private long adjustAmount;

    // The last thing done here, shown under the add row, with an optional way to take it back.
    private string notice = string.Empty;
    private Action? undoNotice;

    public PlayersTab(Plugin plugin, DealerControls controls)
    {
        this.plugin = plugin;
        this.controls = controls;
        newBuyIn = plugin.Configuration.DefaultBuyIn;
    }

    private Table Table => plugin.Table;

    private Configuration Config => plugin.Configuration;

    public void Draw()
    {
        DrawAddPlayers();
        ImGui.Spacing();
        DrawSeats();
        ImGui.Spacing();
        DrawGilSummary();
    }

    private void DrawAddPlayers()
    {
        ImGui.TextColored(DealerControls.Gold, "Seat a player");
        ImGui.SameLine();
        var full = Table.Seats.Count >= Table.MaxSeats;
        ImGui.TextColored(full ? DealerControls.Red : DealerControls.Muted,
                          full ? $"The table is full ({Table.MaxSeats} players)." : $"{Table.Seats.Count} of {Table.MaxSeats} seats taken");
        ImGui.BeginDisabled(full);

        ImGui.SetNextItemWidth(180);
        var entered = ImGui.InputTextWithHint("##name", "First Last", ref newName, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Type the name and press Enter to seat them.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        entered |= ImGui.InputTextWithHint("##world", "World (optional)", ref newWorld, 24, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        DealerControls.InputLong("##buyIn", ref newBuyIn);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Buy-in: the gil they hand you to sit down.");
        ImGui.SameLine();

        ImGui.BeginDisabled(newName.Trim().Length == 0);
        if (ImGui.Button("Seat player") || (entered && newName.Trim().Length > 0))
            Seat(newName, newWorld, clearInputs: true);
        ImGui.EndDisabled();

        if (ImGui.Button("Seat my target"))
        {
            if (plugin.GetTargetPlayer() is { } target)
                Seat(target.Name, target.World, clearInputs: false);
            else
                controls.Error = "Target a player first.";
        }

        ImGui.SameLine();
        if (ImGui.Button("Seat whole party"))
            SeatParty();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Seats everyone in your party except you, the dealer.");

        ImGui.SameLine();
        ImGui.TextColored(DealerControls.Muted, $"Each with a {newBuyIn:N0} buy-in");
        ImGui.EndDisabled();

        if (notice.Length > 0)
        {
            ImGui.TextColored(DealerControls.Good, notice);
            if (undoNotice is { } undo)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Undo##notice"))
                {
                    controls.Do(undo);
                    notice = string.Empty;
                    undoNotice = null;
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("OK##notice"))
            {
                notice = string.Empty;
                undoNotice = null;
            }
        }
    }

    private void Seat(string name, string world, bool clearInputs)
    {
        var buyIn = newBuyIn;
        controls.Do(() =>
        {
            var seat = Table.AddSeat(name, world, buyIn);
            Notify($"Seated {seat.DisplayName} with {buyIn:N0}. Collect {buyIn:N0} gil from them.",
                   () => Table.RemoveSeat(seat));
            if (clearInputs)
            {
                newName = string.Empty;
                newWorld = string.Empty;
            }
        });
    }

    private void SeatParty()
    {
        var buyIn = newBuyIn;
        controls.Do(() =>
        {
            var waiting = plugin.GetPartyMembers()
                .Where(m => !Table.Seats.Any(s => s.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase) &&
                                                  s.World.Equals(m.World, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (waiting.Count == 0)
                throw new InvalidOperationException("Everyone in the party (except you, the dealer) is already seated.");

            // Only as many as there are free seats; the rest are named, so the dealer can tell them.
            var free = Math.Max(0, Table.MaxSeats - Table.Seats.Count);
            if (free == 0)
                throw new InvalidOperationException($"The table is full ({Table.MaxSeats} players). Cash someone out first.");
            var added = waiting.Take(free).Select(m => Table.AddSeat(m.Name, m.World, buyIn)).ToList();
            var left = waiting.Skip(free).Select(m => m.Name).ToList();

            Notify($"Seated {string.Join(", ", added.Select(s => s.Name))}. Collect {buyIn:N0} gil from each." +
                   (left.Count > 0 ? $" No seat left for {string.Join(", ", left)}." : string.Empty),
                   () => added.ForEach(Table.RemoveSeat));
        });
    }

    private void DrawSeats()
    {
        if (Table.Seats.Count == 0)
        {
            ImGui.TextColored(DealerControls.Muted, "No one is seated yet.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##ledger", 6, flags))
            return;

        ImGui.TableSetupColumn("Seat", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Stack");
        ImGui.TableSetupColumn("Bought in");
        ImGui.TableSetupColumn("Net");
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 290);
        ImGui.TableHeadersRow();

        var seats = Table.Seats.ToList();
        for (var i = 0; i < seats.Count; i++)
        {
            var seat = seats[i];
            ImGui.PushID(seat.DisplayName);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawSeatOrder(seat, i, seats.Count);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(seat.SittingOut ? DealerControls.Muted : new Vector4(1, 1, 1, 1), seat.Name);
            if (seat.World.Length > 0)
            {
                ImGui.SameLine(0, 4);
                ImGui.TextColored(DealerControls.Muted, $"@{seat.World}");
            }

            if (seat.SittingOut)
            {
                ImGui.SameLine();
                ImGui.TextColored(DealerControls.Muted, "(sitting out)");
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(seat.Stack.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(seat.TotalBuyIn.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(seat.Net >= 0 ? DealerControls.Good : DealerControls.Red, seat.Net.ToString("+#,0;-#,0;0"));

            ImGui.TableNextColumn();
            DrawSeatActions(seat);

            ImGui.PopID();
        }

        ImGui.EndTable();
        ImGui.TextColored(DealerControls.Muted, "Seats go clockwise from the dealer's left, the same order as the table view.");
    }

    private void DrawSeatOrder(Seat seat, int index, int count)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{index + 1}");
        ImGui.SameLine();

        ImGui.BeginDisabled(Table.HandInProgress || index == 0);
        if (ImGuiComponents.IconButton("up", FontAwesomeIcon.ArrowUp))
            controls.Do(() => Table.MoveSeat(seat, -1));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Table.HandInProgress ? "Seats can't move during a hand." : "Move one seat earlier.");

        ImGui.SameLine(0, 2);
        ImGui.BeginDisabled(Table.HandInProgress || index == count - 1);
        if (ImGuiComponents.IconButton("down", FontAwesomeIcon.ArrowDown))
            controls.Do(() => Table.MoveSeat(seat, 1));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Table.HandInProgress ? "Seats can't move during a hand." : "Move one seat later.");
    }

    private void DrawSeatActions(Seat seat)
    {
        var busyInHand = Table.HandInProgress && seat.InHand;
        var rebuy = Config.DefaultBuyIn;

        ImGui.BeginDisabled(busyInHand || rebuy <= 0);
        if (ImGui.Button("Rebuy"))
        {
            controls.Do(() =>
            {
                Table.AdjustChips(seat, rebuy);
                Notify($"{seat.Name} rebought for {rebuy:N0}. Collect {rebuy:N0} gil from them.",
                       () => Table.AdjustChips(seat, -rebuy));
            });
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(busyInHand ? "They're in the hand. Wait until it's over." : $"Add {rebuy:N0} (the default buy-in) to their stack.");

        ImGui.SameLine();
        ImGui.BeginDisabled(busyInHand);
        if (ImGui.Button("Adjust"))
        {
            adjustAmount = Config.DefaultBuyIn;
            ImGui.OpenPopup("adjust");
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(busyInHand ? "They're in the hand. Wait until it's over." : "Add or take out any amount.");

        ImGui.SameLine();
        if (ImGui.Button(seat.SittingOut ? "Sit in" : "Sit out", new Vector2(62, 0)))
        {
            seat.SittingOut = !seat.SittingOut;
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(seat.SittingOut ? "Deal them in again from the next hand." : "Skip them from the next hand; their chips stay.");

        ImGui.SameLine();
        ImGui.BeginDisabled(Table.HandInProgress);
        if (ImGui.Button("Cash out"))
            ImGui.OpenPopup("cashout");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Table.HandInProgress ? "Finish or cancel the hand first." : $"Pay {seat.Name} their stack and remove them.");

        DrawAdjustPopup(seat);
        DrawCashOutPopup(seat);
    }

    private void DrawAdjustPopup(Seat seat)
    {
        if (!ImGui.BeginPopup("adjust"))
            return;

        ImGui.TextColored(DealerControls.Gold, seat.DisplayName);
        ImGui.TextUnformatted($"Stack {seat.Stack:N0}   Bought in {seat.TotalBuyIn:N0}");
        ImGui.SetNextItemWidth(140);
        DealerControls.InputLong("gil##adjust", ref adjustAmount);
        var amount = adjustAmount;

        ImGui.BeginDisabled(amount <= 0);
        if (DealerControls.ColoredButton($"Add {amount:N0}", AddColor, new Vector2(170, 30)))
        {
            controls.Do(() =>
            {
                Table.AdjustChips(seat, amount);
                Notify($"Added {amount:N0} to {seat.Name}. Collect {amount:N0} gil from them.", () => Table.AdjustChips(seat, -amount));
            });
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("They handed you gil (a rebuy or top-up).");

        ImGui.SameLine();
        ImGui.BeginDisabled(amount > seat.Stack);
        if (DealerControls.ColoredButton($"Take out {amount:N0}", TakeColor, new Vector2(170, 30)))
        {
            controls.Do(() =>
            {
                Table.AdjustChips(seat, -amount);
                Notify($"Took {amount:N0} from {seat.Name}. Pay them {amount:N0} gil.", () => Table.AdjustChips(seat, amount));
            });
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(amount > seat.Stack ? $"They only have {seat.Stack:N0}." : "You hand them gil (a partial cash-out).");

        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawCashOutPopup(Seat seat)
    {
        if (!ImGui.BeginPopup("cashout"))
            return;

        ImGui.TextUnformatted($"Cash out {seat.DisplayName}?");
        ImGui.Spacing();
        ImGui.SetWindowFontScale(1.3f);
        ImGui.TextColored(DealerControls.Gold, $"Pay them {seat.Stack:N0} gil");
        ImGui.SetWindowFontScale(1f);
        ImGui.TextColored(seat.Net >= 0 ? DealerControls.Good : DealerControls.Red,
                          $"Bought in {seat.TotalBuyIn:N0}, so they leave {(seat.Net >= 0 ? "up" : "down")} {Math.Abs(seat.Net):N0}");
        ImGui.Spacing();

        if (DealerControls.ColoredButton("Paid - remove them", PayColor, new Vector2(180, 30)))
        {
            var stack = seat.Stack;
            controls.Do(() =>
            {
                Table.RemoveSeat(seat);
                plugin.Chat.PrintLocal($"Cashed out {seat.DisplayName}: paid {stack:N0} gil (net {seat.Net:+#,0;-#,0;0}).");
                notice = $"Cashed out {seat.DisplayName} for {stack:N0}.";
                undoNotice = null;
            });
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 30)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawGilSummary()
    {
        var stacks = Table.Seats.Sum(s => s.Stack);
        var players = stacks + Table.Pot;
        var total = players + Table.HouseEarnings;

        if (!ImGui.BeginTable("##gil", 2, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("what", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("amount", ImGuiTableColumnFlags.WidthFixed, 230);

        Row("Players' chips" + (Table.Pot > 0 ? $" (stacks {stacks:N0} + pot {Table.Pot:N0})" : string.Empty), $"{players:N0}");

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("House rake");
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Table.HouseEarnings:N0}");
        ImGui.SameLine();
        ImGui.BeginDisabled(Table.HouseEarnings == 0);
        if (ImGui.SmallButton("Pay out"))
            ImGui.OpenPopup("payhouse");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Once you've moved the rake out of the table's gil.");
        DrawPayHousePopup();

        // Here too, not only in Settings, so the count can start again at the end of a night.
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Hands dealt");
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{Table.HandNumber:N0}");
        ImGui.SameLine();
        ImGui.BeginDisabled(Table.HandInProgress || Table.HandNumber == 0 || !ImGui.GetIO().KeyCtrl);
        if (ImGui.SmallButton("Reset##hands"))
        {
            plugin.Chat.PrintLocal($"Hand count reset after {Table.HandNumber:N0} hands. The next hand is Hand #1.");
            Table.HandNumber = 0;
            plugin.Save();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Table.HandInProgress
                ? "Finish or cancel the current hand first."
                : "Hold Ctrl and click, e.g. at the end of a night. The next hand dealt will be Hand #1. Stacks are not touched.");

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(DealerControls.Gold, "Gil you should have on you");
        ImGui.TableNextColumn();
        ImGui.TextColored(DealerControls.Gold, $"{total:N0}");

        ImGui.EndTable();
    }

    private void DrawPayHousePopup()
    {
        if (!ImGui.BeginPopup("payhouse"))
            return;

        ImGui.TextUnformatted($"Take {Table.HouseEarnings:N0} gil of rake out of the table's gil?");
        if (DealerControls.ColoredButton("Done - clear it", PayColor, new Vector2(150, 28)))
        {
            plugin.Chat.PrintLocal($"House rake paid out: {Table.HouseEarnings:N0} gil.");
            Table.HouseEarnings = 0;
            plugin.Save();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 28)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static void Row(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private void Notify(string message, Action? undo)
    {
        notice = message;
        undoNotice = undo;
    }
}
