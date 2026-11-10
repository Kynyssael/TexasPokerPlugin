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

    /// <summary>The buy-in the seat buttons use, as set on this tab.</summary>
    public long BuyIn => newBuyIn;
    private long adjustAmount;

    // The last thing done here, shown under the add row, with an optional way to take it back.
    private string notice = string.Empty;
    private Action? undoNotice;
    private string? undoRecord;

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
        if (plugin.Tournament.Active)
        {
            // A tournament's players are seated by the server's draw and play for chips: no buy-ins or cash-outs.
            DrawTrades();
            ImGui.TextColored(DealerControls.Gold, "Tournament table");
            ImGui.SameLine();
            ImGui.TextColored(DealerControls.Muted, "Stacks are chips. Players are out when they lose them all; the owner pays the prizes.");
            ImGui.Spacing();
            DrawSeats();
            return;
        }

        DrawTrades();
        DrawAddPlayers();
        ImGui.Spacing();
        DrawSeats();
        ImGui.Spacing();
        DrawGilSummary();
    }

    /// <summary>What the last trade meant, and gil traded to you that no buy-in explains, for the dealer to place.</summary>
    public void DrawTrades()
    {
        if (plugin.LastTrade is { } last)
        {
            ImGui.TextColored(last.Warning ? DealerControls.Red : DealerControls.Muted, $"Trade: {last.Message}");
            if (plugin.AutoAddedTrade is not null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Undo##trade"))
                    controls.Do(plugin.UndoAutoAdd);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Not for their stack (a tip, a payback): take it back off, and choose what to do with it.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("OK##trade"))
            {
                plugin.LastTrade = null;
                plugin.AutoAddedTrade = null;
            }
        }

        DrawPayouts();

        var waiting = plugin.Trades.Waiting;
        if (waiting.Count == 0)
        {
            if (plugin.LastTrade is not null)
                ImGui.Spacing();
            return;
        }

        ImGui.TextColored(DealerControls.Gold, "Gil traded to you");
        foreach (var trade in waiting.ToList())
        {
            ImGui.PushID($"{trade.DisplayName}{trade.At.Ticks}");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted($"{trade.Name} gave you {trade.Amount:N0} gil");
            ImGui.SameLine();
            if (plugin.SeatOf(trade) is { } seat)
            {
                if (trade.AddWhenHandEnds)
                {
                    ImGui.TextColored(DealerControls.Muted, "(added when their hand is over)");
                }
                else
                {
                    var inHand = Table.HandInProgress && seat.InHand;
                    if (ImGui.SmallButton(inHand ? "Add after this hand" : "Add to stack"))
                        controls.Do(() => plugin.AddTradeToStack(trade));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(inHand
                            ? "They're in the hand, so it's added to their stack as soon as the hand is over."
                            : $"A rebuy: {trade.Amount:N0} onto {seat.Name}'s stack.");
                }
            }
            else
            {
                ImGui.BeginDisabled(Table.Seats.Count >= Table.MaxSeats);
                if (ImGui.SmallButton($"Seat with {trade.Amount:N0}"))
                    controls.Do(() => plugin.SeatFromTrade(trade));
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Table.Seats.Count >= Table.MaxSeats ? "The table is full." : "Seat them, with this gil as their buy-in.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Ignore"))
                plugin.IgnoreTrade(trade);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Not for the table (a tip, a payback, a mistake). Trade it back yourself if needed.");
            ImGui.PopID();
        }

        ImGui.Spacing();
    }

    /// <summary>Gil the table owes players after a cash-out or take-out, with a button to open the trade.</summary>
    private void DrawPayouts()
    {
        var payouts = plugin.Trades.Payouts(DateTime.UtcNow);
        if (payouts.Count == 0)
            return;

        ImGui.TextColored(DealerControls.Gold, "Gil to pay");
        foreach (var (name, amount) in payouts)
        {
            ImGui.PushID($"pay{name}");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted($"{name}: {amount:N0} gil");
            ImGui.SameLine();
            var part = Math.Min(amount, GilTrades.MaxPerTrade);
            ImGui.BeginDisabled(plugin.TradeOpen);
            if (ImGui.SmallButton(part < amount ? $"Trade {part:N0} (part)" : "Trade"))
                controls.Do(() => plugin.TradePayout(name, amount));
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(plugin.TradeOpen
                    ? "A trade is open. Finish or cancel it first."
                    : $"Asks {name} to trade (stand next to them). When they accept, {part:N0} gil is typed in.\n" +
                      "You check it and press Trade and Confirm yourself: the plugin never does.");
            ImGui.PopID();
        }

        ImGui.Spacing();
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

        DrawSeatButtons();
        ImGui.SameLine();
        ImGui.TextColored(DealerControls.Muted, $"Each with a {newBuyIn:N0} buy-in");
        ImGui.EndDisabled();

        DrawNotice();
    }

    /// <summary>Seat my target, Seat me and Seat whole party, with the buy-in set on this tab. Also on the Table tab.</summary>
    public void DrawSeatButtons()
    {
        if (ImGui.Button("Seat my target"))
        {
            if (plugin.GetTargetPlayer() is { } target)
                Seat(target.Name, target.World, clearInputs: false);
            else
                controls.Error = "Target a player first.";
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(plugin.DealerPlaying || plugin.LocalCharacter is null);
        if (ImGui.Button("Seat me"))
            SeatMe();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(plugin.DealerPlaying
                ? "You're already seated."
                : "Play at your own table, for when only one player is here. Your buy-in comes from your own gil.\n" +
                  "The table is told in chat. While you're seated, everyone else's cards are hidden from you.");

        ImGui.SameLine();
        if (ImGui.Button("Seat whole party"))
            SeatParty();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Seats everyone in your party except you, the dealer.");
    }

    /// <summary>The last seating or gil change, with Undo. Also on the Table tab.</summary>
    public void DrawNotice()
    {
        if (notice.Length > 0)
        {
            ImGui.TextColored(DealerControls.Good, notice);
            if (undoNotice is { } undo)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Undo##notice"))
                {
                    controls.Do(undo);
                    if (undoRecord is { } undone)
                        plugin.Record($"Undone: {undone}");
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
            if (plugin.IsDealer(seat))
            {
                Notify($"You're seated with {buyIn:N0}, from your own gil. Everyone else's cards are now hidden from you.",
                       () => Table.RemoveSeat(seat), $"Seated the dealer, {seat.DisplayName}: buy-in {buyIn:N0} gil from their own.");
                plugin.AnnounceDealerPlaying(seat);
                return;
            }

            plugin.ExpectGil(seat, buyIn);
            Notify($"Seated {seat.DisplayName} with {buyIn:N0}. Collect {buyIn:N0} gil from them.",
                   () => { plugin.ExpectGil(seat, -buyIn); Table.RemoveSeat(seat); }, $"Seated {seat.DisplayName}: buy-in {buyIn:N0} gil collected.");
            if (clearInputs)
            {
                newName = string.Empty;
                newWorld = string.Empty;
            }
        });
    }

    private void SeatMe()
    {
        if (plugin.LocalCharacter?.Split('@') is not [var name, var world])
            return;
        Seat(name, world, clearInputs: false);
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
            added.ForEach(s => plugin.ExpectGil(s, buyIn));

            Notify($"Seated {string.Join(", ", added.Select(s => s.Name))}. Collect {buyIn:N0} gil from each." +
                   (left.Count > 0 ? $" No seat left for {string.Join(", ", left)}." : string.Empty),
                   () => added.ForEach(s => { plugin.ExpectGil(s, -buyIn); Table.RemoveSeat(s); }),
                   $"Seated {string.Join(", ", added.Select(s => s.DisplayName))}: buy-in {buyIn:N0} gil collected from each.");
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

    public void DrawSeatActions(Seat seat)
    {
        if (plugin.Tournament.Active)
        {
            if (ImGui.Button(seat.SittingOut ? "Back" : "Away", new Vector2(62, 0)))
            {
                seat.SittingOut = !seat.SittingOut;
                plugin.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(seat.SittingOut
                    ? "They're back: they act for themselves again."
                    : "They're away: still dealt in, they post their blinds and antes and are folded on their turn.");
            return;
        }

        var busyInHand = Table.HandInProgress && seat.InHand;
        var rebuy = Config.DefaultBuyIn;

        ImGui.BeginDisabled(busyInHand || rebuy <= 0);
        if (ImGui.Button("Rebuy"))
        {
            controls.Do(() =>
            {
                Table.AdjustChips(seat, rebuy);
                plugin.ExpectGil(seat, rebuy);
                Notify($"{seat.Name} rebought for {rebuy:N0}. Collect {rebuy:N0} gil from them.",
                       () => { plugin.ExpectGil(seat, -rebuy); Table.AdjustChips(seat, -rebuy); }, $"{seat.DisplayName} rebought: {rebuy:N0} gil collected.");
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
                plugin.ExpectGil(seat, amount);
                Notify($"Added {amount:N0} to {seat.Name}. Collect {amount:N0} gil from them.",
                       () => { plugin.ExpectGil(seat, -amount); Table.AdjustChips(seat, -amount); },
                       $"Added {amount:N0} to {seat.DisplayName}'s stack: {amount:N0} gil collected.");
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
                plugin.ExpectGil(seat, -amount);
                Notify($"Took {amount:N0} from {seat.Name}. Pay them {amount:N0} gil.",
                       () => { plugin.ExpectGil(seat, amount); Table.AdjustChips(seat, amount); },
                       $"Took {amount:N0} from {seat.DisplayName}'s stack: {amount:N0} gil paid.");
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

        var canTrade = seat.Stack > 0 && !plugin.IsLocalPlayer(seat);
        if (canTrade)
        {
            var on = plugin.Configuration.TradeOnCashOut;
            if (ImGui.Checkbox("Open a trade with them", ref on))
            {
                plugin.Configuration.TradeOnCashOut = on;
                plugin.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: asks them to trade and types in their gil once they accept; you press Trade and Confirm.\n" +
                                 "Off: you trade them yourself. Remembered for next time (also in Settings).");
            ImGui.Spacing();
        }

        var trade = plugin.Configuration.TradeOnCashOut && canTrade;
        if (DealerControls.ColoredButton(trade ? "Remove them and trade" : "Paid - remove them", PayColor, new Vector2(180, 30)))
        {
            var stack = seat.Stack;
            controls.Do(() =>
            {
                Table.RemoveSeat(seat);
                plugin.ExpectGil(seat, -stack);
                if (trade)
                    plugin.TryTradePayout(seat.Name, stack);
                plugin.Chat.PrintLocal($"Cashed out {seat.DisplayName}: paid {stack:N0} gil (net {seat.Net:+#,0;-#,0;0}).");
                plugin.Record($"Cashed out {seat.DisplayName}: {stack:N0} gil paid (bought in {seat.TotalBuyIn:N0}, net {seat.Net:+#,0;-#,0;0}).");
                notice = trade
                    ? $"Cashed out {seat.DisplayName} for {stack:N0}. Check the gil in the trade, then press Trade and Confirm."
                    : $"Cashed out {seat.DisplayName} for {stack:N0}.";
                undoNotice = null;
            });
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.IsItemHovered() && trade)
            ImGui.SetTooltip($"Removes them and asks {seat.Name} to trade. When they accept, the gil is typed in:\n" +
                             "you check it and press Trade and Confirm. They can put their own items or gil in the trade too.");

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 30)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawGilSummary()
    {
        var stacks = Table.Seats.Sum(s => s.Stack);
        var players = stacks + Table.Pot;
        var total = players + Table.HouseEarnings + Table.JackpotHeld;

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

        // Only once this table takes a jackpot cut (its venue runs the bad beat jackpot) or still holds some.
        if (Table.JackpotHeld != 0 || Table.JackpotDropPercent > 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Bad beat jackpot cuts");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("What this table put into the venue's jackpot, still in your gil: give it to the venue's owner.\n" +
                                 "A jackpot paid at your table comes off it. Below 0, the venue owes you that much.");
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (Table.JackpotHeld < 0)
                ImGui.TextColored(DealerControls.Red, $"{Table.JackpotHeld:N0}  (venue owes you {-Table.JackpotHeld:N0})");
            else
                ImGui.TextUnformatted($"{Table.JackpotHeld:N0}");
            ImGui.SameLine();
            ImGui.BeginDisabled(Table.JackpotHeld == 0);
            if (ImGui.SmallButton(Table.JackpotHeld < 0 ? "Settled##jackpot" : "Hand over##jackpot"))
                ImGui.OpenPopup("handjackpot");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Table.JackpotHeld < 0
                    ? "Once the venue has paid you back."
                    : "Once you've given the jackpot cuts to the venue's owner.");
            DrawHandJackpotPopup();
        }

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
            plugin.Chat.PrintLocal($"Hand count reset after {Table.HandNumber:N0} hands. The next hand is Hand #1. A new session log starts (Log tab).");
            plugin.NewSessionLog();
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
            plugin.Record($"House rake paid out: {Table.HouseEarnings:N0} gil.");
            Table.HouseEarnings = 0;
            plugin.Save();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 28)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawHandJackpotPopup()
    {
        if (!ImGui.BeginPopup("handjackpot"))
            return;

        var held = Table.JackpotHeld;
        ImGui.TextUnformatted(held < 0
            ? $"Has the venue paid you back the {-held:N0} gil of jackpot you paid out?"
            : $"Have you given the {held:N0} gil of jackpot cuts to the venue's owner?");
        if (DealerControls.ColoredButton("Done - clear it", PayColor, new Vector2(150, 28)))
        {
            var line = held < 0
                ? $"Jackpot paid back by the venue: {-held:N0} gil."
                : $"Jackpot cuts handed over: {held:N0} gil.";
            plugin.Chat.PrintLocal(line);
            plugin.Record(line);
            Table.JackpotHeld = 0;
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

    /// <summary>Shows a notice (with Undo) and writes <paramref name="record"/> to the session log.</summary>
    private void Notify(string message, Action? undo, string? record = null)
    {
        notice = message;
        undoNotice = undo;
        undoRecord = record;
        if (record is not null)
            plugin.Record(record);
    }
}
