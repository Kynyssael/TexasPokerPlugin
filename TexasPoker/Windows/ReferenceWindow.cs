using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

/// <summary>Reference sheets for the dealer: how the game is played, and the hand rankings.</summary>
public sealed class ReferenceWindow : Window
{
    private static readonly Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);

    private static readonly (string Name, string Rule, List<Card> Example)[] Hands =
    [
        ("Royal flush", "A K Q J 10, all the same suit", Card.ParseMany("As Ks Qs Js 10s")),
        ("Straight flush", "Five in a row, all the same suit", Card.ParseMany("9h 8h 7h 6h 5h")),
        ("Four of a kind", "Four cards of the same rank", Card.ParseMany("Kc Kd Kh Ks 3d")),
        ("Full house", "Three of a kind plus a pair", Card.ParseMany("Qs Qh Qd 7c 7s")),
        ("Flush", "Five cards of the same suit, not in a row", Card.ParseMany("Ad Jd 8d 6d 2d")),
        ("Straight", "Five in a row, mixed suits", Card.ParseMany("10c 9d 8s 7h 6c")),
        ("Three of a kind", "Three cards of the same rank", Card.ParseMany("8s 8h 8d Kc 4s")),
        ("Two pair", "Two different pairs", Card.ParseMany("Jh Jc 4d 4s Ah")),
        ("One pair", "Two cards of the same rank", Card.ParseMany("10s 10h Kd 7c 3h")),
        ("High card", "None of the above; highest card plays", Card.ParseMany("Ac Qd 9s 6h 3c")),
    ];

    private readonly Plugin plugin;
    private string? selectTab;

    public ReferenceWindow(Plugin plugin)
        : base("Poker Reference###TexasPokerReference", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
    }

    private Configuration Config => plugin.Configuration;

    private Table Table => plugin.Table;

    /// <summary>Opens the window on the given tab ("Rules" or "Hand rankings").</summary>
    public void Show(string tab)
    {
        selectTab = tab;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##reference"))
            return;

        if (BeginTab("Rules"))
        {
            DrawRules();
            ImGui.EndTabItem();
        }

        if (BeginTab("Hand rankings"))
        {
            DrawHandRankings();
            ImGui.EndTabItem();
        }

        selectTab = null;
        ImGui.EndTabBar();
    }

    private bool BeginTab(string label)
    {
        var flags = selectTab == label ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        return ImGui.BeginTabItem(label, flags);
    }

    private void DrawRules()
    {
        ImGui.TextColored(Muted, "Blinds, buy-in and rake come from the Settings tab.");
        ImGui.SameLine();
        PostButton("rules", RulesLines());
        ImGui.Spacing();

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 36);
        foreach (var line in RulesLines())
            ImGui.BulletText(line);
        ImGui.PopTextWrapPos();
    }

    private void DrawHandRankings()
    {
        ImGui.TextColored(Muted, "Strongest at the top. Each player uses their best 5 of 7 cards.");
        ImGui.SameLine();
        PostButton("rankings", HandRankingLines());
        ImGui.Spacing();

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("##hands", 4, flags))
        {
            ImGui.TableSetupColumn("#");
            ImGui.TableSetupColumn("Hand");
            ImGui.TableSetupColumn("Example");
            ImGui.TableSetupColumn("What it is");
            ImGui.TableHeadersRow();

            for (var i = 0; i < Hands.Length; i++)
            {
                var (name, rule, example) = Hands[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{i + 1}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(name);
                ImGui.TableNextColumn();
                CardUi.Draw(example, Config.UiSuitSymbols);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rule);
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Breaking ties");
        ImGui.BulletText("Same hand type: the higher ranks win (a pair of Kings beats a pair of Queens).");
        ImGui.BulletText("Still tied: compare the leftover cards (kickers), highest first.");
        ImGui.BulletText("Ace is high, but can be low in A-2-3-4-5 (the lowest straight).");
        ImGui.BulletText("Suits never break a tie. Identical hands split the pot.");
    }

    private void PostButton(string id, IReadOnlyList<string> lines)
    {
        if (ImGui.SmallButton($"Post to chat ({Config.Channel})##{id}"))
            plugin.Chat.SayToTable(lines);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Posts this in {Config.Channel}, one line per message ({lines.Count} messages).");
    }

    private List<string> RulesLines()
    {
        var lines = new List<string>
        {
            "How to play No-Limit Texas Hold'em:",
            "You get 2 private cards by /tell. 5 shared cards are dealt face up on the table.",
            "Make the best 5-card hand from your 2 cards and the 5 on the table.",
            $"Blinds are {Table.SmallBlind:N0}/{Table.BigBlind:N0}, posted by the 2 players left of the button. The button moves every hand.",
            "Betting rounds: before the flop, after the flop (3 cards), after the turn (4th card), after the river (5th card).",
            $"On your turn: fold, check (if no bet), call, bet or raise. Say it in chat, e.g. \"call\" or \"raise to {Table.BigBlind * 3:N0}\".",
            $"Minimum bet is {Table.BigBlind:N0}. A raise must be at least as big as the last bet or raise.",
            "You can go all-in at any time. All-in for less than a bet? You can only win what you matched; extra bets go to a side pot.",
            "The last player left wins the pot, or the best hand at showdown. Identical hands split it.",
        };

        if (Table.RakePercent > 0)
        {
            var cap = Table.RakeCap > 0 ? $", max {Table.RakeCap:N0} per hand" : string.Empty;
            var noFlop = Table.NoFlopNoDrop ? " No rake if the hand ends before the flop." : string.Empty;
            lines.Add($"House rake: {Table.RakePercent:0.##}% of each pot{cap}.{noFlop}");
        }

        if (Table.RunItTwice)
            lines.Add("All-in with cards still to come? The rest of the board is dealt twice, and each pot is split between the two boards.");
        if (Config.BombPotEnabled)
            lines.Add("Bomb pots: now and then everyone puts in the same amount, there are no blinds, and the hand starts on the flop.");
        if (plugin.Web.IsConfigured && plugin.Web.Promotions is { } promo)
        {
            if (promo.BadBeat is { } bb)
                lines.Add($"{promo.Venue} bad beat jackpot ({bb.Balance:N0} now): lose at showdown with {bb.Qualifier}, using both your cards, " +
                          $"with at least {bb.MinPlayers} players dealt in, and the jackpot is shared by you, the winner and the table.");
            if (promo.HighHand is { } hh)
                lines.Add($"{promo.Venue} high hand: the best hand shown at showdown ({hh.Min} or better, using both your cards)" +
                          (hh.Prize.Length > 0 ? $" wins {hh.Prize}." : " wins a prize."));
        }

        lines.Add($"Buy-in is {Config.DefaultBuyIn:N0} gil, traded to the dealer. Cash out with the dealer between hands.");
        return lines;
    }

    private List<string> HandRankingLines()
    {
        var lines = new List<string> { "Hand rankings, strongest first:" };
        for (var i = 0; i < Hands.Length; i++)
        {
            var (name, rule, example) = Hands[i];
            lines.Add($"{i + 1}. {name}: {Card.Join(example, Config.ChatSuitSymbols)} ({rule})");
        }

        lines.Add("Ties: higher ranks win, then kickers. Suits never break a tie.");
        return lines;
    }
}
