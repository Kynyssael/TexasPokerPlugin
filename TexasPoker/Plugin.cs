using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
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

    private readonly WindowSystem windowSystem = new("TexasPoker");
    private readonly MainWindow mainWindow;
    private readonly ReferenceWindow referenceWindow;
    private readonly DealerBarWindow dealerBarWindow;
    private readonly DealerControls controls;
    private readonly TradeWatcher tradeWatcher;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.KeyNeedsSave)
            PluginInterface.SavePluginConfig(Configuration); // an older version's plain-text sign-in key: saved encrypted now
        Table = new Table();
        LoadTable();

        Log = new SessionLog(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "Session logs"));
        Log.Resume(Configuration.SessionLogFile, DateTime.Now);
        Configuration.SessionLogFile = Log.FilePath ?? string.Empty;

        Chat = new ChatSender(Framework, ChatGui, Configuration, CanSendChat);
        if (Configuration.WebTableId.Length == 0)
            Configuration.WebTableId = WebPublisher.NewId(6);
        if (Configuration.WebTableKey.Length == 0)
            Configuration.WebTableKey = WebPublisher.NewId(32);
        Web = new WebPublisher(this, Framework);
        Tournament = new TournamentClient(this, Framework);
        Auth = new AuthClient(this, Framework);
        discordLinker = new DiscordLinker(this, Framework);
        tradeWatcher = new TradeWatcher(AddonLifecycle, Framework, ObjectTable, OnTradeFinished, OnTradeNotice,
                                        () => mainWindow is { IsOpen: true } || dealerBarWindow is { IsOpen: true });
        Table.Announced += OnAnnounced;
        Table.HoleCardsDealt += OnHoleCardsDealt;
        Table.StreetDealt += OnStreetDealt;

        ChatGui.ChatMessage += OnChatMessage;
        Framework.Update += OnUpdate;

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
                          "/poker rules shows the rules, /poker hands the hand rankings, /poker call says whose turn it is with a sound.",
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
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    /// <summary>
    /// Whether the game can take chat input now: logged in and in a zone, not loading one, logging out, or in a
    /// cutscene. Sending chat on the title screen or a loading screen can crash the game, and the plugin now sends
    /// some tells by itself (a tournament's draw, a Discord link's answer), so they wait for a safe moment.
    /// </summary>
    private static bool CanSendChat() =>
        ObjectTable.LocalPlayer is not null &&
        !Condition[ConditionFlag.BetweenAreas] && !Condition[ConditionFlag.BetweenAreas51] &&
        !Condition[ConditionFlag.LoggingOut] && !Condition[ConditionFlag.OccupiedInCutSceneEvent] &&
        !Condition[ConditionFlag.WatchingCutscene] && !Condition[ConditionFlag.WatchingCutscene78];

    public Configuration Configuration { get; }

    public Table Table { get; }

    public ChatSender Chat { get; }

    public WebPublisher Web { get; }

    /// <summary>The tournament this table deals, if any (Tournament License).</summary>
    public TournamentClient Tournament { get; }

    /// <summary>Players' "!link CODE" tells, for the Discord table roles.</summary>
    private readonly DiscordLinker discordLinker;

    /// <summary>XIVAuth sign-in: the dealer tools only work for a signed-in, approved character.</summary>
    public AuthClient Auth { get; }

    /// <summary>The turn timer (Settings: Players' actions), running while someone is to act.</summary>
    public TurnClock Clock { get; } = new();

    private DateTime? clockPublished;

    /// <summary>The whole session: everything the table announced and the gil taken and paid, saved to a file.</summary>
    public SessionLog Log { get; }

    /// <summary>Gil trades matched against buy-ins and cash-outs, and the ones waiting for the dealer.</summary>
    public GilTrades Trades { get; } = new();

    /// <summary>What the last trade meant, shown on the Players tab until the dealer clears it.</summary>
    public TradeResult? LastTrade { get; set; }

    /// <summary>The trade the plugin last put on a stack by itself, for Undo next to <see cref="LastTrade"/>.</summary>
    public GilTrade? AutoAddedTrade { get; set; }

    /// <summary>Adds a line to the session log that isn't said at the table (a buy-in, a cash-out...).</summary>
    public void Record(string line) => Log.Add([line], DateTime.Now);

    /// <summary>Ends this session's log (its file is kept) and starts a new one.</summary>
    public void NewSessionLog()
    {
        Record($"=== Session ended after {Table.HandNumber:N0} hands ===");
        Log.StartNew(DateTime.Now);
        Configuration.SessionLogFile = Log.FilePath ?? string.Empty;
        Save();
    }

    public void Dispose()
    {
        Save();
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnUpdate;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        windowSystem.RemoveAllWindows();
        tradeWatcher.Dispose();
        Tournament.Dispose();
        Auth.Dispose();
        Web.Dispose();
        Chat.Dispose();
    }

    public void Save()
    {
        // A tournament's blinds come from its clock: the dealer's own blinds stay for after it.
        if (!Table.TournamentMode)
        {
            Configuration.SmallBlind = Table.SmallBlind;
            Configuration.BigBlind = Table.BigBlind;
        }

        Configuration.ButtonIndex = Table.ButtonIndex;
        Configuration.HandNumber = Table.HandNumber;
        Configuration.RakePercent = Table.RakePercent;
        Configuration.RakeCap = Table.RakeCap;
        Configuration.NoFlopNoDrop = Table.NoFlopNoDrop;
        Configuration.RunItTwice = Table.RunItTwice;
        Configuration.HouseEarnings = Table.HouseEarnings;
        Configuration.JackpotHeld = Table.JackpotHeld;
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

        // A player who cashed out loses their private link; a new one is made if they sit down again.
        Web.ForgetLinksOfAbsentPlayers();

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

    /// <summary>
    /// The player's private link, and a seat PIN for a phone: open the table page, tap your seat, enter the PIN.
    /// Call <see cref="Save"/> after, so the PIN reaches the server.
    /// </summary>
    public void TellWebLink(Seat seat)
    {
        var pin = Web.NewPin(seat);
        TellPlayer(seat, $"See your cards: open {Web.TableUrl} , tap your seat and enter PIN {pin} (works once, for 10 minutes). " +
                         $"Or use your private link (keep it private): {Web.PlayerUrl(seat)}", secret: true);
    }

    /// <summary>
    /// An action a player sent from their private page, already checked as genuine and current. Applied at
    /// once, or (with automatic web actions off) picked for the dealer to confirm, like a chat action.
    /// </summary>
    public void ApplyWebAction(Seat seat, ParsedAction action)
    {
        if (!Configuration.WebActionsAuto)
        {
            var label = controls.PickFromChat(action);
            Chat.PrintLocal($"{seat.Name} (web): {label}. Press Confirm to apply.");
            Record($"{seat.DisplayName} (web page): {label}, waiting for the dealer to confirm");
            return;
        }

        try
        {
            Table.Act(action.Action, action.Amount);
            Chat.PrintLocal($"{seat.Name} acted from the web: {Table.LastAction}");
            Record($"(that was {seat.DisplayName} from their web page)");
        }
        catch (InvalidOperationException ex)
        {
            Chat.PrintLocal($"{seat.Name}'s web action didn't go through: {ex.Message}");
            Record($"{seat.DisplayName} (web page): sent {action.Action}, refused: {ex.Message}");
        }
    }

    /// <summary>The bomb pot amount for the next hand: the dealer's amount, or two big blinds.</summary>
    public long BombPotAmount => Configuration.BombPotAmount > 0 ? Configuration.BombPotAmount : Table.BigBlind * 2;

    /// <summary>Deals the next hand: a bomb pot if the dealer ticked it (once), otherwise a normal hand.</summary>
    public void DealNextHand()
    {
        if (Tournament.Active)
        {
            Tournament.SetBlindsForHand();
            Table.StartHand();
            return;
        }

        var bomb = Configuration.BombPotEnabled && Configuration.BombPotNext;
        Table.StartHand(bomb ? BombPotAmount : 0);
        if (bomb)
            Configuration.BombPotNext = false;
    }

    public void ToggleDealerBar() => dealerBarWindow.Toggle();

    public void OpenMainWindow() => mainWindow.IsOpen = true;

    public void CloseMainWindow() => mainWindow.IsOpen = false;

    /// <summary>The dealer has accepted the current terms of use.</summary>
    public bool TermsAccepted => Configuration.AcceptedTermsVersion >= TermsPanel.Version;

    /// <summary>Opens the reference window on "Rules" or "Hand rankings".</summary>
    public void ShowReference(string tab) => referenceWindow.Show(tab);

    public void AnnounceState() => Chat.SayToTable(Table.DescribeState());

    /// <summary>
    /// Says whose turn it is again, with a chat sound so the table hears it: for a busy venue where the
    /// action scrolls away. Uses the dealer's own "action to" wording.
    /// </summary>
    public void CallTurn()
    {
        var lines = Table.ActorPrompt();
        if (lines.Count == 0)
        {
            Chat.PrintLocal("Nobody is to act right now.");
            return;
        }

        var sound = $" <se.{Math.Clamp(Configuration.TurnSound, 1, 16)}>";
        Chat.SayToTable(lines.Select((line, i) => i == 0 ? line + sound : line));
    }

    /// <summary>The turn timer's seconds per turn, or 0 while it's off (or the dealer isn't signed in).</summary>
    private int TimerSeconds => Auth.Allowed && Configuration.TurnTimerEnabled ? Math.Clamp(Configuration.TurnTimerSeconds, 15, 300) : 0;

    private void OnUpdate(IFramework _)
    {
        // Trade gil the dealer asked to add once the player's hand was over.
        if (!Table.HandInProgress)
        {
            foreach (var trade in Trades.Waiting.Where(t => t.AddWhenHandEnds).ToList())
            {
                try
                {
                    AddTradeToStack(trade);
                }
                catch (InvalidOperationException ex)
                {
                    trade.AddWhenHandEnds = false;
                    LastTrade = new TradeResult($"Couldn't add {trade.DisplayName}'s {trade.Amount:N0} gil: {ex.Message}", Warning: true);
                }
            }
        }

        switch (Clock.Tick(Table, DateTime.UtcNow, TimerSeconds, controls.HasPicked))
        {
            case TurnClockEvent.CallTurn:
                CallTurn();
                break;
            case TurnClockEvent.TimeUp:
                TimeUp();
                break;
        }

        // A new turn's deadline goes to the web table (players see the countdown). Paused, it waits for Resume.
        if (!Clock.Paused && Clock.EndsAt != clockPublished)
        {
            clockPublished = Clock.EndsAt;
            Web.Push();
        }
    }

    /// <summary>The turn timer ran out: the player checks if they can, otherwise folds.</summary>
    private void TimeUp()
    {
        if (Table.Actor is not { } actor)
            return;

        var check = Table.GetOptions().CanCheck;
        Chat.SayToTable([$"Time's up, {actor.Name}: {(check ? "check" : "fold")}."]);
        try
        {
            Table.Act(check ? PlayerAction.Check : PlayerAction.Fold);
        }
        catch (InvalidOperationException ex)
        {
            Chat.PrintLocal($"The turn timer couldn't act for {actor.Name}: {ex.Message}");
        }
    }

    /// <summary>The dealer gives the player to act their full time again.</summary>
    public void MoreTime()
    {
        Clock.Restart(DateTime.UtcNow);
        clockPublished = null; // send the new deadline
    }

    /// <summary>Stops or restarts the turn timer for the player to act (a ruling, a question...).</summary>
    public void TogglePause()
    {
        if (Clock.Paused)
            Clock.Resume(DateTime.UtcNow);
        else
            Clock.Pause(DateTime.UtcNow);
        clockPublished = null;
        Web.Push();
    }

    /// <summary>Plays a chat sound in the dealer's own chat only, to pick one.</summary>
    public void PreviewTurnSound(int sound) => Chat.Echo($"Call turn sound: <se.{Math.Clamp(sound, 1, 16)}>");
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
            TellPlayer(seat, Tournament.Active
                ? $"Your chips: {seat.Stack:N0}."
                : $"Your stack: {seat.Stack:N0} gil (bought in {seat.TotalBuyIn:N0}, net {seat.Net:+#,0;-#,0;0}).");
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

    public bool IsLocalPlayer(Seat seat) => IsLocalPlayer(seat.Name, seat.World);

    /// <summary>Whether this seat is you, the dealer, playing at your own table.</summary>
    public bool IsDealer(Seat seat) => IsLocalPlayer(seat);

    /// <summary>
    /// Whether you're seated at your own table. While you are, the plugin keeps everyone else's cards from you:
    /// "Show all hole cards" is off, and your copy of each card tell and link tell is hidden from your chat.
    /// </summary>
    public bool DealerPlaying => Table.Seats.Any(IsLocalPlayer);

    /// <summary>Whether this window may show the seat's hole cards right now.</summary>
    public bool ShowsCards(Seat seat) =>
        (Table.Street == Street.HandOver && seat.Contending) ||
        (DealerPlaying ? IsLocalPlayer(seat) : Configuration.ShowHoleCards);

    /// <summary>The table expects gil to change hands with this player: above 0 you collect it, below 0 you pay it.</summary>
    public void ExpectGil(Seat seat, long amount)
    {
        if (!IsLocalPlayer(seat))
            Trades.Expect(seat.Name, amount, DateTime.UtcNow);
    }

    /// <summary>The seat a trade came from, if that player is seated.</summary>
    public Seat? SeatOf(GilTrade trade) =>
        Table.Seats.FirstOrDefault(s => s.Name.Equals(trade.Name, StringComparison.OrdinalIgnoreCase) &&
                                        (trade.World.Length == 0 || s.World.Length == 0 ||
                                         s.World.Equals(trade.World, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Adds traded gil to the player's stack, as a rebuy. While they're in a hand it's held, and added when the hand
    /// is over (a stack can't change mid-hand).
    /// </summary>
    /// <param name="quiet">The caller says what happened itself (no chat line or log line here).</param>
    public void AddTradeToStack(GilTrade trade, bool quiet = false)
    {
        if (SeatOf(trade) is not { } seat)
            throw new InvalidOperationException($"{trade.Name} isn't seated any more.");
        if (Table.HandInProgress && seat.InHand)
        {
            trade.AddWhenHandEnds = true;
            return;
        }

        Table.AdjustChips(seat, trade.Amount);
        Trades.Dismiss(trade);
        if (!quiet)
        {
            Record($"Added {trade.Amount:N0} gil from a trade to {seat.DisplayName}'s stack.");
            Chat.PrintLocal($"Added {trade.Amount:N0} to {seat.Name}'s stack (from their trade).");
        }

        Save();
    }

    /// <summary>Seats the player who traded, with that gil as their buy-in.</summary>
    public void SeatFromTrade(GilTrade trade)
    {
        var seat = Table.AddSeat(trade.Name, trade.World, trade.Amount);
        Trades.Dismiss(trade);
        Record($"Seated {seat.DisplayName} from a trade: buy-in {trade.Amount:N0} gil collected.");
        Save();
    }

    /// <summary>
    /// Takes back an automatic add: off the stack again (or no longer held for the hand's end), and back to waiting,
    /// where the dealer can add it, seat someone with it, or ignore it.
    /// </summary>
    public void UndoAutoAdd()
    {
        if (AutoAddedTrade is not { } trade)
            return;

        if (!Trades.Waiting.Contains(trade))
        {
            if (SeatOf(trade) is not { } seat)
                throw new InvalidOperationException($"{trade.Name} isn't seated any more.");
            Table.AdjustChips(seat, -trade.Amount);
        }

        Trades.Hold(trade);
        AutoAddedTrade = null;
        LastTrade = null;
        Record($"Undone: {trade.Amount:N0} gil from {trade.DisplayName}'s trade taken back off their stack.");
        Save();
    }

    /// <summary>The dealer leaves a trade out of the table (a tip, a payback, a mistake).</summary>
    public void IgnoreTrade(GilTrade trade)
    {
        Trades.Dismiss(trade);
        Record($"Trade from {trade.DisplayName} for {trade.Amount:N0} gil left out of the table.");
    }

    /// <summary>The trade window is open.</summary>
    public bool TradeOpen => tradeWatcher.Trading;

    /// <summary>
    /// Asks a player the table owes gil for a trade; once they accept, the gil is typed in (up to one trade's worth)
    /// for the dealer to check. The dealer presses Trade and Confirm: the plugin never does.
    /// </summary>
    public void TradePayout(string name, long owed)
    {
        var self = ObjectTable.LocalPlayer?.EntityId;
        var nearby = ObjectTable.OfType<IPlayerCharacter>()
                                .Where(p => p.EntityId != self && p.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
                                .ToList();
        if (nearby.Count == 0)
            throw new InvalidOperationException($"{name} isn't nearby. Walk up to them, then try again.");
        if (nearby.Count > 1)
            throw new InvalidOperationException($"More than one {name} is nearby. Trade them yourself.");

        var amount = (uint)Math.Min(owed, GilTrades.MaxPerTrade);
        tradeWatcher.Offer(nearby[0].EntityId, name, amount);
        var rest = owed > amount ? $" The other {owed - amount:N0} goes in another trade (at most 1,000,000 per trade)." : string.Empty;
        LastTrade = new TradeResult($"Asked {name} to trade. When they accept, {amount:N0} gil is typed in for you to check.{rest}", Warning: false);
        AutoAddedTrade = null;
    }

    /// <summary>
    /// <see cref="TradePayout"/> right after a cash-out: if the trade can't be opened (they walked off, a trade is
    /// open), the cash-out still stands and the payout waits under Gil to pay.
    /// </summary>
    public void TryTradePayout(string name, long owed)
    {
        try
        {
            TradePayout(name, owed);
        }
        catch (InvalidOperationException ex)
        {
            OnTradeNotice($"Couldn't open the trade: {ex.Message} Use Trade under Gil to pay when you're ready.", true);
        }
    }

    private void OnTradeNotice(string message, bool warning)
    {
        LastTrade = new TradeResult(message, warning);
        AutoAddedTrade = null;
        Chat.PrintLocal(message);
    }

    private void OnTradeFinished(string name, string world, long amount)
    {
        if (!Auth.Allowed || amount == 0)
            return;

        var result = Trades.Settle(new GilTrade(name, world, amount, DateTime.UtcNow));
        AutoAddedTrade = null;

        // A seated player's extra gil goes straight on their stack (after their hand, if they're in one). Not at a
        // tournament table: its stacks are chips.
        if (Configuration.AutoAddTrades && !Tournament.Active &&
            Trades.Waiting.LastOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } trade &&
            SeatOf(trade) is { } seat)
        {
            var held = Table.HandInProgress && seat.InHand;
            AddTradeToStack(trade, quiet: true);
            AutoAddedTrade = trade;
            result = new TradeResult(held
                ? $"{trade.DisplayName} gave you {trade.Amount:N0} gil: it goes on their stack when their hand is over."
                : $"{trade.DisplayName} gave you {trade.Amount:N0} gil: added to their stack.", Warning: false);
        }

        LastTrade = result;
        var waiting = Trades.Waiting.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !t.AddWhenHandEnds);
        Chat.PrintLocal(waiting ? $"{result.Message} (Players tab)" : result.Message);
        Record($"Trade: {result.Message}");
    }

    /// <summary>Tells a tournament player by /tell which table they move to.</summary>
    public void TellTournamentMove(Seat seat, string message) => TellPlayer(seat, message);

    /// <summary>Tells a tournament player who isn't seated yet (Name@World) where to go.</summary>
    public void TellTournamentPlayer(string name, string world, string message) =>
        Chat.Tell(world.Length > 0 ? $"{name}@{world}" : name, message);

    /// <summary>Tells the table the dealer has sat down to play, so nobody is surprised by it.</summary>
    public void AnnounceDealerPlaying(Seat seat)
    {
        var line = $"The dealer ({seat.Name}) is playing at this table. The plugin hides everyone else's cards from the dealer, " +
                   "but you are trusting the dealer.";
        Chat.SayToTable([line]);
        Record(line);
    }

    /// <summary>Whether you're logged in as this exact character (name and home world).</summary>
    public bool IsLocalCharacter(string name, string world) => world.Length > 0 && IsLocalPlayer(name, world);

    /// <summary>The character you're logged in as, or null on the title screen.</summary>
    public string? LocalCharacter
    {
        get
        {
            var local = ObjectTable.LocalPlayer;
            return local is null ? null : $"{local.Name.TextValue}@{local.HomeWorld.Value.Name}";
        }
    }

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
        Log.Add(lines, DateTime.Now);

        if (Configuration.AnnounceActions)
            Chat.SayToTable(lines);

        // Start the next turn's time now, so the one update sent for this action already carries it.
        Clock.Sync(Table, DateTime.UtcNow, TimerSeconds);
        clockPublished = Clock.EndsAt;
        Save();
    }

    /// <summary>
    /// When the player to act types their action ("call", "raise to 20k"...) in the table channel or a
    /// /tell, pick it in the dealer controls. The dealer still confirms, so a misread can't act alone.
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        // Your own copy of a card or link tell, while you play: the player got it, you don't see it.
        if (message.LogKind == XivChatType.TellOutgoing &&
            (Chat.TakeHiddenEcho(message.Message.TextValue) || (DealerPlaying && IsSecretTell(message.Message.TextValue))))
        {
            message.PreventOriginal();
            return;
        }

        // A player linking their Discord for the tournament table roles: "!link CODE", from the link page.
        if (message.LogKind == XivChatType.TellIncoming && DiscordLinker.CodeIn(message.Message.TextValue) is { } linkCode)
        {
            var (linkName, linkWorld) = SenderOf(message.Sender);
            discordLinker.Link(linkName, linkWorld, linkCode);
            return;
        }

        if (!Auth.Allowed || !Configuration.ReadChatActions || !Table.HandInProgress || Table.Actor is not { } actor)
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
        // Word for word, for the records ("I said check, not call"). The action itself follows once confirmed.
        Record($"{actor.DisplayName} ({(isTell ? "tell" : Configuration.Channel)}): \"{text.Trim()}\" -> read as {label}");
        if (!mainWindow.IsOpen && !dealerBarWindow.IsOpen)
            dealerBarWindow.IsOpen = true;
    }

    /// <summary>
    /// A card or link tell by its wording, in case the game changed the text a little (suit symbols, spacing)
    /// so it no longer matches the one queued.
    /// </summary>
    private static bool IsSecretTell(string text) =>
        text.Contains("your cards", StringComparison.OrdinalIgnoreCase);

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
                             (hand is { } value ? $" - {value.Describe()}" : string.Empty), secret: true);
        }
    }

    private void SendHoleCards(Seat seat, IReadOnlyList<Card> cards)
    {
        TellPlayer(seat, $"Hand #{Table.HandNumber}: your cards are {Card.Join(cards, Configuration.ChatSuitSymbols)}", secret: true);
    }

    /// <param name="secret">Hole cards or a private link: while you play, your own copy of the tell is hidden.</param>
    private void TellPlayer(Seat seat, string message, bool secret = false)
    {
        // You can't /tell yourself, so a dealer who is also playing sees the message locally.
        if (IsLocalPlayer(seat))
            Chat.PrintLocal(message);
        else if (secret && DealerPlaying)
            Chat.TellHidden(seat.DisplayName, message);
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
        Table.RunItTwice = Configuration.RunItTwice;
        Table.HouseEarnings = Configuration.HouseEarnings;
        Table.JackpotHeld = Configuration.JackpotHeld;
        Table.TournamentMode = Configuration.TournamentId.Length > 0;
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
        var sub = args.Trim().ToLowerInvariant();
        if (!Auth.Allowed && sub is "state" or "deal" or "bar" or "call")
        {
            // Everything but the reference sheets needs a signed-in dealer; the main window shows the sign-in.
            Chat.PrintLocal(TermsAccepted
                ? "Sign in with XIVAuth first: /poker opens the sign-in."
                : "Read and accept the terms of use first: /poker opens them.");
            mainWindow.IsOpen = true;
            return;
        }

        switch (sub)
        {
            case "state":
                AnnounceState();
                break;
            case "call":
                CallTurn();
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
                    DealNextHand();
                    Save();
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
