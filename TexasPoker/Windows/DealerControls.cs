using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

/// <summary>
/// The dealing controls shared by the main window and the compact dealer bar: hand summary, action
/// buttons and the between-hands view. One instance is shared so both show the same raise amount.
/// </summary>
public sealed class DealerControls
{
    public static readonly Vector4 Red = new(0.95f, 0.35f, 0.35f, 1f);
    public static readonly Vector4 Gold = new(1f, 0.8f, 0.3f, 1f);
    public static readonly Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 Good = new(0.4f, 0.9f, 0.5f, 1f);

    private static readonly Vector4 FoldColor = new(0.55f, 0.18f, 0.18f, 1f);
    private static readonly Vector4 CallColor = new(0.16f, 0.45f, 0.22f, 1f);
    private static readonly Vector4 RaiseColor = new(0.18f, 0.32f, 0.58f, 1f);
    private static readonly Vector4 AllInColor = new(0.62f, 0.36f, 0.08f, 1f);
    private static readonly Vector4 ConfirmColor = new(0.68f, 0.52f, 0.08f, 1f);

    private readonly Plugin plugin;
    private long raiseTo;
    private int lastActorIndex = -1;
    private int lastHandNumber = -1;
    private SelectedAction? selected;

    public DealerControls(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public string Error { get; set; } = string.Empty;

    private Table Table => plugin.Table;

    private Configuration Config => plugin.Configuration;

    public void DrawError()
    {
        if (Error.Length == 0)
            return;

        ImGui.TextColored(Red, Error);
        ImGui.SameLine();
        if (ImGui.SmallButton("Dismiss##error"))
            Error = string.Empty;
    }

    /// <summary>Hand number and street, a large pot, the board, and the last action.</summary>
    public void DrawHandSummary(bool compact, bool showBoard = true)
    {
        var info = Table.HandInProgress
            ? $"Hand #{Table.HandNumber}  -  {Table.Street}"
            : Table.Street == Street.HandOver ? $"Hand #{Table.HandNumber}  -  finished" : "No hand yet";
        if (!compact)
        {
            info += $"  -  Blinds {Table.SmallBlind:N0}/{Table.BigBlind:N0}";
            if (Table.RakePercent > 0)
                info += $"  -  Rake {Table.RakePercent:0.##}%" + (Table.RakeCap > 0 ? $" (cap {Table.RakeCap:N0})" : string.Empty);
        }

        ImGui.TextColored(Muted, info);

        if (!showBoard)
        {
            if (Table.HandInProgress && Table.LastAction is { } lastAction)
            {
                ImGui.SameLine(0, 20);
                ImGui.TextColored(Gold, $"Last: {lastAction}");
            }

            return;
        }

        if (Table.HandInProgress)
        {
            Large(() => ImGui.TextColored(Gold, $"Pot {Table.Pot:N0}"), compact ? 1.2f : 1.4f);
            ImGui.SameLine(0, 20);
        }

        if (Table.Board.Count > 0)
            Large(() => CardUi.Draw(Table.Board, Config.UiSuitSymbols), compact ? 1.2f : 1.4f);
        else if (Table.HandInProgress)
            ImGui.TextColored(Muted, "(no board yet)");
        if (Table.RanTwice)
        {
            ImGui.TextColored(Muted, "Run 2:");
            ImGui.SameLine();
            Large(() => CardUi.Draw(Table.SecondBoard, Config.UiSuitSymbols), compact ? 1.2f : 1.4f);
        }

        if (!compact && plugin.Web.IsConfigured && plugin.Web.Promotions is { } promo)
        {
            var parts = new List<string>();
            if (promo.BadBeat is { } bb)
                parts.Add($"Bad beat jackpot {bb.Balance:N0} ({bb.Qualifier})");
            if (promo.HighHand is { } hh)
                parts.Add(hh.BestPlayer is null ? $"High hand: none yet ({hh.Min}+)" : $"High hand: {hh.BestPlayer}, {hh.BestHand}");
            ImGui.TextColored(Gold, $"{promo.Venue}: {string.Join("  -  ", parts)}");
        }

        if (Table.HandInProgress && Table.LastAction is { } last)
            ImGui.TextColored(Muted, $"Last: {last}");
    }

    /// <summary>The dealer has picked an action and not confirmed it yet (the turn timer waits for them).</summary>
    public bool HasPicked => selected is not null;

    /// <summary>The turn timer's countdown, with Pause and More time, next to the player to act.</summary>
    private void DrawTurnTimer()
    {
        if (plugin.Clock.SecondsLeft(DateTime.UtcNow) is not { } left)
            return;

        var clock = plugin.Clock;
        var total = Math.Max(1, plugin.Configuration.TurnTimerSeconds);
        var color = clock.Paused ? Muted : left <= 10 ? new Vector4(1f, 0.42f, 0.42f, 1f) : left <= total / 2 ? Gold : new Vector4(0.4f, 0.82f, 0.5f, 1f);
        ImGui.SameLine(0, 14);
        ImGui.TextColored(color, clock.Paused ? $"Timer paused ({left} s)" : HasPicked && left == 0 ? "Time's up: confirm or pick" : $"{left} s");
        ImGui.SameLine();
        if (ImGui.SmallButton(clock.Paused ? "Resume" : "Pause"))
            plugin.TogglePause();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stops the turn timer for this player, e.g. for a ruling or a question.");
        ImGui.SameLine();
        if (ImGui.SmallButton("More time"))
            plugin.MoreTime();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Gives {Table.Actor?.Name ?? "the player"} their full {total} s again.");
    }

    /// <summary>Who is to act, the big action buttons, quick bets and dealer tools.</summary>
    public void DrawActionPanel(bool compact)
    {
        var buttonSize = compact ? new Vector2(100, 30) : new Vector2(130, 40);

        if (Table.Actor is { } actor)
        {
            var options = Table.GetOptions();
            if (Table.ActorIndex != lastActorIndex || Table.HandNumber != lastHandNumber)
            {
                // New player to act: preset the raise box to the minimum.
                lastActorIndex = Table.ActorIndex;
                lastHandNumber = Table.HandNumber;
                raiseTo = options.MinRaiseTo;
                selected = null;
            }

            var prompt = options.ToCall > 0 ? $"{options.CallAmount:N0} to call" : "check or bet";
            Large(() => ImGui.TextColored(Gold, $">> {actor.Name} to act"), compact ? 1.1f : 1.3f);
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"({prompt}, stack {actor.Stack:N0})");
            ImGui.SameLine();
            if (ImGui.SmallButton("Call turn"))
                plugin.CallTurn();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Says in chat whose turn it is, with a sound (<se.{plugin.Configuration.TurnSound}>), " +
                                 "so a busy table can hear it.\nPick the sound in Settings > Chat and announcements. Also: /poker call");
            DrawTurnTimer();

            ActionButton("Fold", "fold", FoldColor, buttonSize, PlayerAction.Fold);

            ImGui.SameLine();
            if (options.CanCheck)
                ActionButton("Check", "check", CallColor, buttonSize, PlayerAction.Check);
            else
                ActionButton($"Call {options.CallAmount:N0}", "call", CallColor, buttonSize, PlayerAction.Call);

            var canRaise = options.CanRaise && options.MaxRaiseTo > options.MinRaiseTo;
            if (canRaise)
            {
                raiseTo = Math.Clamp(raiseTo, options.MinRaiseTo, options.MaxRaiseTo);
                ImGui.SameLine();

                // Once picked, the raise keeps the amount it was picked with, even if the box changes.
                var amount = selected is { Action: PlayerAction.BetOrRaise } pick ? pick.Amount : raiseTo;
                var label = options.IsBet ? $"Bet {amount:N0}" : $"Raise to {amount:N0}";
                ActionButton(label, "raise", RaiseColor, buttonSize with { X = buttonSize.X * 1.3f }, PlayerAction.BetOrRaise, amount);
            }

            ImGui.SameLine();
            ActionButton($"All-in {actor.Stack + actor.StreetBet:N0}", "allin", AllInColor, buttonSize, PlayerAction.AllIn);

            if (selected is { } chosen)
                DrawConfirm(chosen, actor, buttonSize);

            ImGui.BeginDisabled(selected is not null);
            if (canRaise)
                DrawRaiseSizing(options);
            else if (!options.CanRaise && options.ToCall > 0 && actor.Stack > options.ToCall)
                ImGui.TextColored(Muted, "Betting not reopened by the short all-in: call or fold only.");
            ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(!Table.CanUndo);
        if (ImGui.Button("Undo"))
        {
            selected = null;
            Do(Table.Undo);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Take back the last action.");

        ImGui.SameLine();
        if (ImGui.Button("Announce state"))
            plugin.AnnounceState();

        if (!compact)
        {
            ImGui.SameLine();
            if (ImGui.Button("Resend hole cards"))
                plugin.ResendHoleCards();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
        if (ImGui.Button("Cancel hand"))
            Do(Table.CancelHand);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click. Misdeal: returns every bet and ends the hand.");
    }

    /// <summary>Results of the last hand, a big Deal button and stack tools.</summary>
    public void DrawBetweenHands(bool compact)
    {
        // After the hand counter is reset there's no "last hand" to show results for.
        if (Table.LastResults.Count > 0 && Table.HandNumber > 0)
        {
            ImGui.TextUnformatted($"Hand #{Table.HandNumber} result");
            foreach (var result in Table.LastResults)
            {
                var winners = string.Join(", ", result.Winners.Select(w => w.Name));
                var line = result.Returned
                    ? $"{result.Amount:N0} back to {winners} (uncalled)"
                    : $"{result.Label}: {winners} wins {result.Amount:N0}";
                Large(() => ImGui.TextColored(result.Returned ? Muted : Good, line), compact ? 1.0f : 1.2f);
                if (!result.Returned && (result.Hand is not null || result.Rake > 0 || result.Jackpot > 0))
                {
                    var details = new List<string>();
                    if (result.Hand is { } hand)
                        details.Add(hand);
                    if (result.Rake > 0)
                        details.Add($"rake {result.Rake:N0}");
                    if (result.Jackpot > 0)
                        details.Add($"jackpot {result.Jackpot:N0}");
                    ImGui.TextColored(Muted, "    " + string.Join(", ", details));
                }
            }

            ImGui.Spacing();
        }

        var ready = Table.Seats.Count(s => !s.SittingOut && s.Stack > 0);
        if (Config.BombPotEnabled)
        {
            var bomb = Config.BombPotNext;
            if (ImGui.Checkbox($"Bomb pot ({plugin.BombPotAmount:N0} each)", ref bomb))
            {
                Config.BombPotNext = bomb;
                plugin.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Next hand only: everyone puts in this much, no blinds, straight to the flop. Change the amount in Settings.");
        }

        ImGui.BeginDisabled(ready < 2);
        var width = compact ? 260 : Math.Max(260, ImGui.GetContentRegionAvail().X);
        var bombNext = Config.BombPotEnabled && Config.BombPotNext;
        if (ColoredButton(bombNext ? $"Deal hand #{Table.HandNumber + 1}: BOMB POT" : $"Deal hand #{Table.HandNumber + 1}", CallColor,
                          new Vector2(width, compact ? 34 : 46)))
            Do(plugin.DealNextHand);
        ImGui.EndDisabled();
        if (ready < 2)
            ImGui.TextColored(Muted, "Need at least two players with chips (Players tab).");

        if (ImGui.Button("Tell stacks"))
            plugin.TellStacks();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sends each player their own stack and net by /tell.");

        ImGui.SameLine();
        if (ImGui.Button("Announce stacks"))
            plugin.Chat.SayToTable(Table.Seats.Select(s => $"{s.Name}: {s.Stack:N0}"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Posts every player's stack in {Config.Channel}.");
    }

    public void Do(Action action)
    {
        try
        {
            action();
            Error = string.Empty;
            plugin.Save();
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Gil amounts fit in an int (the gil cap is 999,999,999), so edit them as one.</summary>
    public static bool InputLong(string label, ref long value)
    {
        var asInt = (int)Math.Clamp(value, 0, int.MaxValue);
        if (!ImGui.InputInt(label, ref asInt, 0, 0))
            return false;
        value = Math.Max(0, asInt);
        return true;
    }

    /// <summary>
    /// One action button. With "Confirm actions" on, a click only picks the action: the other actions grey
    /// out and a separate Confirm button appears, so a stray or double click can't act for the player.
    /// </summary>
    private void ActionButton(string label, string id, Vector4 color, Vector2 size, PlayerAction action, long amount = 0)
    {
        var isPicked = selected?.Action == action;
        ImGui.BeginDisabled(selected is not null && !isPicked);
        if (isPicked)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 3f);
            ImGui.PushStyleColor(ImGuiCol.Border, Gold);
        }

        var clicked = ColoredButton($"{label}##{id}", isPicked ? (color * 1.4f) with { W = 1f } : color, size);
        if (isPicked)
        {
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Picked. Click again to un-pick, or use Confirm below.");
        }

        ImGui.EndDisabled();
        if (!clicked)
            return;

        if (!Config.ConfirmActions)
            Act(action, amount);
        else
            selected = isPicked ? null : new SelectedAction(action, amount, label);
    }

    /// <summary>Picks an action a player typed in chat, for the dealer to confirm. Returns its button label.</summary>
    public string PickFromChat(ParsedAction parsed)
    {
        var actor = Table.Actor ?? throw new InvalidOperationException("No one is waiting to act");
        var options = Table.GetOptions();
        var label = parsed.Action switch
        {
            PlayerAction.Fold => "Fold",
            PlayerAction.Check => "Check",
            PlayerAction.Call => $"Call {options.CallAmount:N0}",
            PlayerAction.BetOrRaise => options.IsBet ? $"Bet {parsed.Amount:N0}" : $"Raise to {parsed.Amount:N0}",
            _ => $"All-in {actor.Stack + actor.StreetBet:N0}",
        };

        // Mark this player as already seen, so the next draw doesn't clear the pick as a "new turn".
        lastActorIndex = Table.ActorIndex;
        lastHandNumber = Table.HandNumber;
        raiseTo = parsed.Action == PlayerAction.BetOrRaise ? parsed.Amount : options.MinRaiseTo;
        selected = new SelectedAction(parsed.Action, parsed.Amount, label, FromChat: true);
        return label;
    }

    private void DrawConfirm(SelectedAction chosen, Seat actor, Vector2 buttonSize)
    {
        ImGui.Spacing();
        var source = chosen.FromChat ? " (from chat)" : string.Empty;
        if (ColoredButton($"Confirm{source}: {actor.Name} {chosen.Label}##confirm", ConfirmColor, new Vector2(buttonSize.X * 2.8f, buttonSize.Y)))
            Act(chosen.Action, chosen.Amount);

        ImGui.SameLine();
        if (ImGui.Button("Change##confirm", new Vector2(90, buttonSize.Y)))
            selected = null;
    }

    private void Act(PlayerAction action, long amount)
    {
        selected = null;
        Do(() => Table.Act(action, amount));
    }

    private void DrawRaiseSizing(ActionOptions options)
    {
        var bb = Table.BigBlind;

        if (ImGui.Button($"-{bb:N0}##minus"))
            raiseTo = Math.Max(options.MinRaiseTo, raiseTo - bb);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        InputLong("##raiseTo", ref raiseTo);
        ImGui.SameLine();
        if (ImGui.Button($"+{bb:N0}##plus"))
            raiseTo = Math.Min(options.MaxRaiseTo, raiseTo + bb);

        // Quick sizes only fill the amount; the Raise button confirms it.
        foreach (var (label, amount) in QuickBets(options))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"{label} ({amount:N0})"))
                raiseTo = amount;
        }
    }

    private IEnumerable<(string Label, long Amount)> QuickBets(ActionOptions options)
    {
        long Clamp(long target) => Math.Clamp(target, options.MinRaiseTo, options.MaxRaiseTo);

        yield return ("Min", options.MinRaiseTo);

        if (Table.Street == Street.Preflop && Table.CurrentBet == Table.BigBlind)
        {
            // Unopened preflop pot: sizes in big blinds are what players ask for.
            yield return ("2.5x", Clamp(Table.BigBlind * 5 / 2));
            yield return ("3x", Clamp(Table.BigBlind * 3));
            yield return ("4x", Clamp(Table.BigBlind * 4));
            yield break;
        }

        // Pot-sized raise: call first, then raise by a fraction of the pot after the call.
        var potAfterCall = Table.Pot + options.CallAmount;
        yield return ("1/2 pot", Clamp(Table.CurrentBet + (potAfterCall / 2)));
        yield return ("3/4 pot", Clamp(Table.CurrentBet + (potAfterCall * 3 / 4)));
        yield return ("Pot", Clamp(Table.CurrentBet + potAfterCall));
    }

    internal static bool ColoredButton(string label, Vector4 color, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, (color * 1.25f) with { W = 1f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, (color * 1.5f) with { W = 1f });
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private static void Large(Action draw, float scale)
    {
        ImGui.SetWindowFontScale(scale);
        draw();
        ImGui.SetWindowFontScale(1f);
    }
}

/// <summary>An action the dealer picked but hasn't confirmed yet.</summary>
internal sealed record SelectedAction(PlayerAction Action, long Amount, string Label, bool FromChat = false);
