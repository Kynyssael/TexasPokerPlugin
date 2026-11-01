using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TexasPoker.Engine;
using TexasPoker.Windows;

namespace TexasPoker;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/poker";
    private const int MaxLogLines = 300;

    private readonly WindowSystem windowSystem = new("TexasPoker");
    private readonly MainWindow mainWindow;
    private readonly ReferenceWindow referenceWindow;
    private readonly DealerBarWindow dealerBarWindow;
    private readonly DealerControls controls;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Table = new Table();
        LoadTable();

        Chat = new ChatSender(Framework, ChatGui, Configuration);
        if (Configuration.WebTableId.Length == 0)
            Configuration.WebTableId = WebPublisher.NewId(6);
        if (Configuration.WebTableKey.Length == 0)
            Configuration.WebTableKey = WebPublisher.NewId(32);
        Web = new WebPublisher(this, Framework);
        Table.Announced += OnAnnounced;
        Table.HoleCardsDealt += OnHoleCardsDealt;
        Table.StreetDealt += OnStreetDealt;

        ChatGui.ChatMessage += OnChatMessage;

        controls = new DealerControls(this);
        mainWindow = new MainWindow(this, controls);
        windowSystem.AddWindow(mainWindow);
        dealerBarWindow = new DealerBarWindow(this, controls);
        windowSystem.AddWindow(dealerBarWindow);
        referenceWindow = new ReferenceWindow(this);
        windowSystem.AddWindow(referenceWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Texas Hold'em dealer table. /poker bar toggles the compact dealer bar, " +
                          "/poker rules shows the rules, /poker hands the hand rankings.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
    }

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

    public Configuration Configuration { get; }

    public Table Table { get; }

    public ChatSender Chat { get; }

    public WebPublisher Web { get; }

    /// <summary>Everything the table has announced, newest last.</summary>
    public List<string> Log { get; } = new();

    public void Dispose()
    {
        Save();
        ChatGui.ChatMessage -= OnChatMessage;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        windowSystem.RemoveAllWindows();
        Web.Dispose();
        Chat.Dispose();
    }

    public void Save()
    {
        Configuration.SmallBlind = Table.SmallBlind;
        Configuration.BigBlind = Table.BigBlind;
        Configuration.ButtonIndex = Table.ButtonIndex;
        Configuration.HandNumber = Table.HandNumber;
        Configuration.RakePercent = Table.RakePercent;
        Configuration.RakeCap = Table.RakeCap;
        Configuration.NoFlopNoDrop = Table.NoFlopNoDrop;
        Configuration.HouseEarnings = Table.HouseEarnings;
        Configuration.MessageTemplates = Table.Messages.Overrides.ToDictionary(o => o.Key.ToString(), o => o.Value);

        // Mid-hand, save chips already bet as part of the stack so a crash or reload refunds the hand.
        Configuration.Seats = Table.Seats.Select(s => new SavedSeat
        {
            Name = s.Name,
            World = s.World,
            Stack = s.Stack + (Table.HandInProgress ? s.Contributed : 0),
            TotalBuyIn = s.TotalBuyIn,
            SittingOut = s.SittingOut,
        }).ToList();

        // Before saving, so any private link tokens it creates are saved too.
        Web.Push();
        PluginInterface.SavePluginConfig(Configuration);
    }

    /// <summary>Tells each seated player their private web link, which also shows their own cards.</summary>
    public void TellWebLinks()
    {
        foreach (var seat in Table.Seats)
            TellWebLink(seat);
        Save();
    }

    public void TellWebLink(Seat seat) =>
        TellPlayer(seat, $"Watch the table and see your cards (keep this link private): {Web.PlayerUrl(seat)}");

    public void ToggleDealerBar() => dealerBarWindow.Toggle();

    public void OpenMainWindow() => mainWindow.IsOpen = true;

    /// <summary>Opens the reference window on "Rules" or "Hand rankings".</summary>
    public void ShowReference(string tab) => referenceWindow.Show(tab);

    public void AnnounceState() => Chat.SayToTable(Table.DescribeState());
    /// <summary>Re-sends hole cards to every player in the current hand.</summary>
    public void ResendHoleCards()
    {
        foreach (var seat in Table.Seats.Where(s => s.Contending))
            SendHoleCards(seat, seat.HoleCards);
    }

    /// <summary>Privately tells each seated player their own stack and how much they're up or down.</summary>
    public void TellStacks()
    {
        foreach (var seat in Table.Seats)
            TellPlayer(seat, $"Your stack: {seat.Stack:N0} gil (bought in {seat.TotalBuyIn:N0}, net {seat.Net:+#,0;-#,0;0}).");
    }

    /// <summary>The current target as (name, home world), if it's a player.</summary>
    public (string Name, string World)? GetTargetPlayer()
    {
        if (TargetManager.Target is not IPlayerCharacter player)
            return null;
        return (player.Name.TextValue, player.HomeWorld.Value.Name.ToString());
    }

    /// <summary>The party, without the dealer (you), who runs the table rather than playing.</summary>
    public IEnumerable<(string Name, string World)> GetPartyMembers() =>
        PartyList.Select(m => (Name: m.Name.TextValue, World: m.World.Value.Name.ToString()))
            .Where(m => !IsLocalPlayer(m.Name, m.World));

    private bool IsLocalPlayer(Seat seat) => IsLocalPlayer(seat.Name, seat.World);

    private static bool IsLocalPlayer(string name, string world)
    {
        var local = ObjectTable.LocalPlayer;
        return local != null &&
               local.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase) &&
               (world.Length == 0 ||
                local.HomeWorld.Value.Name.ToString().Equals(world, StringComparison.OrdinalIgnoreCase));
    }

    private void OnAnnounced(IReadOnlyList<string> lines)
    {
        Log.AddRange(lines);
        if (Log.Count > MaxLogLines)
            Log.RemoveRange(0, Log.Count - MaxLogLines);

        if (Configuration.AnnounceActions)
            Chat.SayToTable(lines);

        Save();
    }

    /// <summary>
    /// When the player to act types their action ("call", "raise to 20k"...) in the table channel or a
    /// /tell, pick it in the dealer controls. The dealer still confirms, so a misread can't act alone.
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!Configuration.ReadChatActions || !Table.HandInProgress || Table.Actor is not { } actor)
            return;

        var isTell = message.LogKind == XivChatType.TellIncoming;
        if (isTell ? !Configuration.ReadTellActions : !TableChannelTypes().Contains(message.LogKind))
            return;

        var (name, world) = SenderOf(message.Sender);
        if (!name.Equals(actor.Name, StringComparison.OrdinalIgnoreCase) ||
            (world.Length > 0 && actor.World.Length > 0 && !world.Equals(actor.World, StringComparison.OrdinalIgnoreCase)))
            return;

        var text = message.Message.TextValue;
        if (ChatActionParser.Parse(text, Table.GetOptions(), Table.CurrentBet) is not { } parsed)
            return;

        var label = controls.PickFromChat(parsed);
        Chat.PrintLocal($"{actor.Name}{(isTell ? " (tell)" : string.Empty)}: \"{text.Trim()}\" -> {label}. Press Confirm to apply.");
        if (!mainWindow.IsOpen && !dealerBarWindow.IsOpen)
            dealerBarWindow.IsOpen = true;
    }

    /// <summary>The chat types that count as the table channel, from the configured command.</summary>
    private XivChatType[] TableChannelTypes() => Configuration.Channel switch
    {
        "/p" => [XivChatType.Party, XivChatType.CrossParty],
        "/s" => [XivChatType.Say],
        "/y" => [XivChatType.Yell],
        "/sh" => [XivChatType.Shout],
        "/fc" => [XivChatType.FreeCompany],
        "/a" => [XivChatType.Alliance],
        "/l1" => [XivChatType.Ls1],
        "/cwl1" => [XivChatType.CrossLinkShell1],
        _ => [XivChatType.Party, XivChatType.CrossParty],
    };

    /// <summary>The sender's name and home world; world is empty when the game doesn't include it.</summary>
    private static (string Name, string World) SenderOf(SeString sender)
    {
        if (sender.Payloads.OfType<PlayerPayload>().FirstOrDefault() is { } player)
            return (player.PlayerName, player.World.Value.Name.ToString());

        // No player link (e.g. your own lines): keep only the name, dropping party-number and world icons.
        var name = new string(sender.TextValue.Where(c => char.IsLetter(c) || c is ' ' or '\'' or '-').ToArray()).Trim();
        return (name, string.Empty);
    }

    private void OnHoleCardsDealt(Seat seat, IReadOnlyList<Card> cards)
    {
        if (Configuration.TellHoleCards)
            SendHoleCards(seat, cards);
    }

    private void OnStreetDealt(Street street)
    {
        if (!Configuration.RetellHandEachStreet)
            return;

        var symbols = Configuration.ChatSuitSymbols;
        foreach (var seat in Table.Seats.Where(s => s.Contending))
        {
            var hand = Table.CurrentHand(seat);
            TellPlayer(seat, $"{street}: your cards {Card.Join(seat.HoleCards, symbols)}" +
                             (hand is { } value ? $" - {value.Describe()}" : string.Empty));
        }
    }

    private void SendHoleCards(Seat seat, IReadOnlyList<Card> cards)
    {
        TellPlayer(seat, $"Hand #{Table.HandNumber}: your cards are {Card.Join(cards, Configuration.ChatSuitSymbols)}");
    }

    private void TellPlayer(Seat seat, string message)
    {
        // You can't /tell yourself, so a dealer who is also playing sees the message locally.
        if (IsLocalPlayer(seat))
            Chat.PrintLocal(message);
        else
            Chat.Tell(seat.DisplayName, message);
    }

    private void LoadTable()
    {
        Table.SmallBlind = Configuration.SmallBlind;
        Table.BigBlind = Configuration.BigBlind;
        Table.SuitSymbols = Configuration.ChatSuitSymbols;
        Table.HandNumber = Configuration.HandNumber;
        Table.RakePercent = Configuration.RakePercent;
        Table.RakeCap = Configuration.RakeCap;
        Table.NoFlopNoDrop = Configuration.NoFlopNoDrop;
        Table.HouseEarnings = Configuration.HouseEarnings;
        foreach (var (kind, text) in Configuration.MessageTemplates)
        {
            if (Enum.TryParse<MessageKind>(kind, out var parsed))
                Table.Messages.Set(parsed, text);
        }

        foreach (var seat in Configuration.Seats)
        {
            try
            {
                Table.RestoreSeat(seat.Name, seat.World, seat.Stack, seat.TotalBuyIn, seat.SittingOut);
            }
            catch (InvalidOperationException)
            {
                // Skip duplicate or invalid saved seats rather than failing to load.
            }
        }

        Table.ButtonIndex = Math.Min(Configuration.ButtonIndex, Table.Seats.Count - 1);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "state":
                AnnounceState();
                break;
            case "bar":
                ToggleDealerBar();
                break;
            case "rules":
                ShowReference("Rules");
                break;
            case "hands":
                ShowReference("Hand rankings");
                break;
            case "deal":
                try
                {
                    Table.StartHand();
                }
                catch (InvalidOperationException ex)
                {
                    Chat.PrintLocal(ex.Message);
                }

                break;
            default:
                mainWindow.Toggle();
                break;
        }
    }
}
