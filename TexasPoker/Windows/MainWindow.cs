using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Red = DealerControls.Red;
    private static readonly Vector4 Gold = DealerControls.Gold;
    private static readonly Vector4 Muted = DealerControls.Muted;
    private static readonly Vector4 Good = DealerControls.Good;
    private static readonly Vector4 ActorRow = new(0.75f, 0.55f, 0.1f, 0.6f);

    private static readonly (string Command, string Label)[] Channels =
    [
        ("/p", "Party"),
        ("/s", "Say"),
        ("/y", "Yell"),
        ("/sh", "Shout"),
        ("/fc", "Free Company"),
        ("/a", "Alliance"),
        ("/l1", "Linkshell 1"),
        ("/cwl1", "Cross-world Linkshell 1"),
    ];

    private readonly Plugin plugin;
    private readonly DealerControls controls;
    private readonly MessagesTab messagesTab;
    private readonly TableView tableView;
    private readonly PlayersTab playersTab;
    private readonly WebTab webTab;
    private readonly VenueTab venueTab;
    private readonly SignInPanel signInPanel;
    private readonly TermsPanel termsPanel;

    public MainWindow(Plugin plugin, DealerControls controls)
        : base("Eorzean Poker###TexasPokerMain")
    {
        this.plugin = plugin;
        this.controls = controls;
        messagesTab = new MessagesTab(plugin);
        tableView = new TableView(plugin);
        playersTab = new PlayersTab(plugin, controls);
        webTab = new WebTab(plugin);
        venueTab = new VenueTab(plugin);
        signInPanel = new SignInPanel(plugin);
        termsPanel = new TermsPanel(plugin);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 520) };
        Size = new Vector2(900, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Table Table => plugin.Table;

    private Configuration Config => plugin.Configuration;

    public override void Draw()
    {
        // The terms come first, before signing in, the first time and whenever they change.
        if (!plugin.TermsAccepted)
        {
            termsPanel.Draw();
            return;
        }

        if (!plugin.Auth.Allowed)
        {
            signInPanel.Draw();
            return;
        }

        DrawStatusBar();
        controls.DrawError();

        if (!ImGui.BeginTabBar("##tabs"))
            return;

        if (ImGui.BeginTabItem("Table"))
        {
            DrawTableTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Players"))
        {
            playersTab.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Messages"))
        {
            messagesTab.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Web"))
        {
            webTab.Draw();
            ImGui.EndTabItem();
        }

        if (plugin.Auth.Venues.Count > 0 && ImGui.BeginTabItem("Venue"))
        {
            venueTab.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettingsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Log"))
        {
            DrawLogTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawStatusBar()
    {
        var send = Config.SendToChat;
        if (ImGui.Checkbox("Send to chat", ref send))
        {
            Config.SendToChat = send;
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off = preview mode: messages are printed only in your own chat log.");

        ImGui.SameLine();
        ImGui.TextColored(Muted, $"Channel {Config.Channel}");

        ImGui.SameLine();
        if (ImGui.SmallButton("Rules"))
            plugin.ShowReference("Rules");
        ImGui.SameLine();
        if (ImGui.SmallButton("Hand rankings"))
            plugin.ShowReference("Hand rankings");
        ImGui.SameLine();
        if (ImGui.SmallButton("Dealer bar"))
            plugin.ToggleDealerBar();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A small window with just the action buttons, so you can close this one while dealing.");

        if (plugin.Chat.Pending > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Gold, $"{plugin.Chat.Pending} message(s) queued");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear queue"))
                plugin.Chat.Clear();
        }

        ImGui.Separator();

        // Signed out mid-hand: the hand can finish, and signing in again right here keeps the table going.
        if (!plugin.Auth.Unlocked)
        {
            ImGui.TextColored(Red, "Your sign-in ended. You can finish this hand; the table locks after it unless you sign in again.");
            if (ImGui.CollapsingHeader("Sign in again", ImGuiTreeNodeFlags.DefaultOpen))
                signInPanel.Draw();
            ImGui.Separator();
        }
    }

    private void DrawTableTab()
    {
        var useTable = Config.TableView;
        if (ImGui.Checkbox("Table view", ref useTable))
        {
            Config.TableView = useTable;
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show the players around a poker table, or as a list.");

        ImGui.SameLine(0, 20);
        if (useTable)
        {
            // The table already shows the pot and board, so only the info line and last action go above it.
            controls.DrawHandSummary(compact: false, showBoard: false);

            // Leave room below the table for the action buttons.
            var reserved = Table.HandInProgress ? 170 : 150;
            var height = Math.Clamp(ImGui.GetContentRegionAvail().Y - reserved, 280, 560);
            tableView.Draw(height);
        }
        else
        {
            controls.DrawHandSummary(compact: false);
            ImGui.Spacing();
            DrawSeatTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (Table.HandInProgress)
            controls.DrawActionPanel(compact: false);
        else
            controls.DrawBetweenHands(compact: false);
    }

    private void DrawSeatTable()
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##seats", 6, flags))
            return;

        ImGui.TableSetupColumn("Pos", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Stack");
        ImGui.TableSetupColumn("Bet");
        ImGui.TableSetupColumn("Cards");
        ImGui.TableSetupColumn("Status");
        ImGui.TableHeadersRow();

        for (var i = 0; i < Table.Seats.Count; i++)
        {
            var seat = Table.Seats[i];
            ImGui.TableNextRow();
            var isActor = Table.Actor == seat;
            if (isActor)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(ActorRow));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(PositionLabel(i));

            ImGui.TableNextColumn();
            if (isActor)
                ImGui.TextColored(Gold, $">> {seat.DisplayName}");
            else if (seat.Folded || !seat.InHand && Table.HandInProgress)
                ImGui.TextColored(Muted, seat.DisplayName);
            else
                ImGui.TextUnformatted(seat.DisplayName);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(seat.Stack.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(seat.StreetBet > 0 ? seat.StreetBet.ToString("N0") : string.Empty);

            ImGui.TableNextColumn();
            if (seat.HoleCards.Count > 0 && seat.InHand)
            {
                if (Config.ShowHoleCards || Table.Street == Street.HandOver && seat.Contending)
                    DrawCards(seat.HoleCards);
                else
                    ImGui.TextColored(Muted, "[?] [?]");
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(StatusColor(seat), StatusText(seat));
        }

        ImGui.EndTable();
    }

    private void DrawSettingsTab()
    {
        var changed = false;
        var inHand = Table.HandInProgress;

        // One section per topic, so finding a setting doesn't mean scrolling past all of them.
        if (SettingsHeader("Blinds and buy-in", open: true))
        {
            ImGui.BeginDisabled(inHand);
            var sb = Table.SmallBlind;
            var bb = Table.BigBlind;
            ImGui.SetNextItemWidth(140);
            if (DealerControls.InputLong("Small blind", ref sb))
            {
                Table.SmallBlind = Math.Max(0, sb);
                changed = true;
            }

            ImGui.SetNextItemWidth(140);
            if (DealerControls.InputLong("Big blind", ref bb))
            {
                Table.BigBlind = Math.Max(1, bb);
                changed = true;
            }

            ImGui.EndDisabled();

            var buyIn = Config.DefaultBuyIn;
            ImGui.SetNextItemWidth(140);
            if (DealerControls.InputLong("Default buy-in", ref buyIn))
            {
                Config.DefaultBuyIn = Math.Max(0, buyIn);
                changed = true;
            }

            ImGui.TextUnformatted($"Hands dealt: {Table.HandNumber:N0} (next is #{Table.HandNumber + 1})");
            ImGui.SameLine();
            ImGui.BeginDisabled(inHand || Table.HandNumber == 0 || !ImGui.GetIO().KeyCtrl);
            if (ImGui.Button("Reset hand #"))
            {
                Table.HandNumber = 0;
                changed = true;
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(inHand
                    ? "Finish or cancel the current hand first."
                    : "Hold Ctrl and click. The next hand dealt will be Hand #1. Stacks are not touched.");
            LockedDuringHand(inHand);
        }

        if (SettingsHeader("Rake"))
        {
            ImGui.BeginDisabled(inHand);
            var rakePercent = (float)Table.RakePercent;
            ImGui.SetNextItemWidth(140);
            if (ImGui.InputFloat("Rake % (0 = off)", ref rakePercent, 0.5f, 1f, "%.2f"))
            {
                Table.RakePercent = Math.Round((decimal)Math.Clamp(rakePercent, 0f, 100f), 2);
                changed = true;
            }

            ImGui.BeginDisabled(Table.RakePercent <= 0);
            var rakeCap = Table.RakeCap;
            ImGui.SetNextItemWidth(140);
            if (DealerControls.InputLong("Rake cap per hand (0 = no cap)", ref rakeCap))
            {
                Table.RakeCap = rakeCap;
                changed = true;
            }

            changed |= Checkbox("No flop, no drop", () => Table.NoFlopNoDrop, v => Table.NoFlopNoDrop = v);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Take no rake from hands that end before the flop.");
            ImGui.EndDisabled();
            ImGui.EndDisabled();
            LockedDuringHand(inHand);
        }

        if (SettingsHeader("Bomb pots and run it twice"))
        {
            ImGui.BeginDisabled(inHand);
            changed |= Checkbox("Run it twice when everyone is all-in", () => Table.RunItTwice, v => Table.RunItTwice = v);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When nobody can bet any more and cards are still to come, the rest of the board is dealt twice\n" +
                                 "and each pot is split between the two boards. Hands run twice don't count for venue promotions.");
            ImGui.EndDisabled();

            changed |= Checkbox("Bomb pots", () => Config.BombPotEnabled, v => Config.BombPotEnabled = v);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Adds a Bomb pot option next to Deal: everyone puts in the same amount, no blinds, and the hand starts on the flop.");
            if (Config.BombPotEnabled)
            {
                ImGui.SameLine();
                var bomb = Config.BombPotAmount;
                ImGui.SetNextItemWidth(140);
                if (DealerControls.InputLong("Bomb pot amount (0 = two big blinds)", ref bomb))
                {
                    Config.BombPotAmount = Math.Max(0, bomb);
                    changed = true;
                }
            }
        }

        if (SettingsHeader("Chat and announcements"))
        {
            var current = Channels.FirstOrDefault(c => c.Command == Config.Channel);
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("Announce channel", current.Label ?? Config.Channel))
            {
                foreach (var (command, label) in Channels)
                {
                    if (ImGui.Selectable($"{label} ({command})", command == Config.Channel))
                    {
                        Config.Channel = command;
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }

            changed |= Checkbox("Announce every action", () => Config.AnnounceActions, v => Config.AnnounceActions = v);
            changed |= Checkbox("Send hole cards by /tell when dealing", () => Config.TellHoleCards, v => Config.TellHoleCards = v);
            changed |= Checkbox("Re-tell each player their cards and hand after flop, turn and river", () => Config.RetellHandEachStreet, v => Config.RetellHandEachStreet = v);
            changed |= Checkbox("Use suit symbols in chat", () => Config.ChatSuitSymbols, v =>
            {
                Config.ChatSuitSymbols = v;
                Table.SuitSymbols = v;
            });

            var delay = Config.MessageDelayMs;
            ImGui.SetNextItemWidth(140);
            if (ImGui.SliderInt("Delay between chat messages (ms)", ref delay, 500, 5000))
            {
                Config.MessageDelayMs = delay;
                changed = true;
            }

            var sound = Config.TurnSound;
            ImGui.SetNextItemWidth(140);
            if (ImGui.SliderInt("Call turn sound", ref sound, 1, 16, "<se.%d>"))
            {
                Config.TurnSound = sound;
                changed = true;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The game's chat sound that the Call turn button plays with its message.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Hear it"))
                plugin.PreviewTurnSound(Config.TurnSound);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Plays it in your own chat only (/echo).");
        }

        if (SettingsHeader("Players' actions"))
        {
            changed |= Checkbox("Confirm actions (pick, then Confirm)", () => Config.ConfirmActions, v => Config.ConfirmActions = v);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Clicking an action only picks it and greys out the others; a separate Confirm button applies it.");
            changed |= Checkbox($"Read player actions from {Config.Channel} chat", () => Config.ReadChatActions, v => Config.ReadChatActions = v);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("When the player to act types \"call\", \"fold\", \"check\", \"all in\", \"bet 5000\" or \"raise to 20k\",\n" +
                                 "that action is picked for you. You still press Confirm.");
            ImGui.BeginDisabled(!Config.ReadChatActions);
            ImGui.Indent();
            changed |= Checkbox("Also read actions sent to me by /tell", () => Config.ReadTellActions, v => Config.ReadTellActions = v);
            ImGui.Unindent();
            ImGui.EndDisabled();
        }

        if (SettingsHeader("This window"))
        {
            changed |= Checkbox("Use suit symbols in this window", () => Config.UiSuitSymbols, v => Config.UiSuitSymbols = v);
            changed |= Checkbox("Show all hole cards in this window", () => Config.ShowHoleCards, v => Config.ShowHoleCards = v);

            var betDistance = Config.BetDistance;
            ImGui.SetNextItemWidth(140);
            if (ImGui.SliderInt("Bet chip distance from seat (table view)", ref betDistance, 0, 120))
            {
                Config.BetDistance = betDistance;
                changed = true;
            }
        }

        if (changed)
            plugin.Save();
    }

    private static bool SettingsHeader(string label, bool open = false) =>
        ImGui.CollapsingHeader(label, open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);

    private static void LockedDuringHand(bool inHand)
    {
        if (inHand)
            ImGui.TextColored(Muted, "Some of these can only change between hands.");
    }

    private void DrawLogTab()
    {
        if (ImGui.Button("Clear log"))
            plugin.Log.Clear();

        ImGui.BeginChild("##log", Vector2.Zero, true);
        foreach (var line in plugin.Log)
            ImGui.TextWrapped(line);
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
    }

    private void DrawCards(IReadOnlyList<Card> cards) => CardUi.Draw(cards, Config.UiSuitSymbols);

    private string PositionLabel(int index) => tableView.PositionLabel(index);

    private string StatusText(Seat seat) => tableView.StatusText(seat);

    private Vector4 StatusColor(Seat seat) => tableView.StatusColor(seat);

    private static bool Checkbox(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (!ImGui.Checkbox(label, ref value))
            return false;
        set(value);
        return true;
    }
}
