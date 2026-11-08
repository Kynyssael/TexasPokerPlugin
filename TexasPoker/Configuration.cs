using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Newtonsoft.Json;
using TexasPoker.Engine;

namespace TexasPoker;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public long SmallBlind { get; set; } = 500;

    public long BigBlind { get; set; } = 1000;

    public long DefaultBuyIn { get; set; } = 100_000;

    /// <summary>Chat command prefix used for table announcements, e.g. "/p" or "/s".</summary>
    public string Channel { get; set; } = "/p";

    /// <summary>When off, messages are only printed to your own chat log (handy for testing).</summary>
    public bool SendToChat { get; set; } = true;

    public bool AnnounceActions { get; set; } = true;

    public bool TellHoleCards { get; set; } = true;

    /// <summary>After the flop, turn and river, tell each player their cards and best hand again.</summary>
    public bool RetellHandEachStreet { get; set; } = true;

    public bool ChatSuitSymbols { get; set; } = true;

    public bool UiSuitSymbols { get; set; } = true;

    public int MessageDelayMs { get; set; } = 1500;

    /// <summary>The game's chat sound (&lt;se.1&gt; to &lt;se.16&gt;) that Call turn plays.</summary>
    public int TurnSound { get; set; } = 1;

    /// <summary>The turn timer: Call turn at half the time, then an automatic check (or fold) when it runs out.</summary>
    public bool TurnTimerEnabled { get; set; }

    public int TurnTimerSeconds { get; set; } = 60;

    /// <summary>The session log file being written (Log tab), picked up again after a reload until a new session.</summary>
    public string SessionLogFile { get; set; } = string.Empty;

    public bool ShowHoleCards { get; set; }

    /// <summary>Action buttons only pick the action; a separate Confirm button applies it.</summary>
    public bool ConfirmActions { get; set; } = true;

    /// <summary>When the player to act types their action in the table channel, pick it for the dealer to confirm.</summary>
    public bool ReadChatActions { get; set; } = true;

    /// <summary>Also read actions players send the dealer by /tell.</summary>
    public bool ReadTellActions { get; set; } = true;

    /// <summary>
    /// Gil a seated player trades you, beyond any buy-in already entered, goes straight onto their stack (after their
    /// hand, if they're in one). Off: it waits on the Players and Table tabs for you to add it.
    /// </summary>
    public bool AutoAddTrades { get; set; } = true;

    /// <summary>
    /// Cashing a player out also asks them to trade and types in their gil once they accept. The dealer still
    /// checks it and presses Trade and Confirm.
    /// </summary>
    public bool TradeOnCashOut { get; set; } = true;

    /// <summary>Show players around a drawn poker table (dealer at the bottom) instead of a list.</summary>
    public bool TableView { get; set; } = true;

    /// <summary>How far in front of a seat box its bet chip sits on the table view, in pixels.</summary>
    public int BetDistance { get; set; } = 18;

    /// <summary>Whether the compact dealer bar was open, so it reopens after a reload.</summary>
    public bool ShowDealerBar { get; set; }

    /// <summary>Percentage of each contested pot the house keeps; 0 = no rake.</summary>
    public decimal RakePercent { get; set; }

    /// <summary>Most rake per hand; 0 = no cap.</summary>
    public long RakeCap { get; set; } = 10_000;

    public bool NoFlopNoDrop { get; set; } = true;

    /// <summary>Deal the rest of the board twice when everyone left is all-in.</summary>
    public bool RunItTwice { get; set; }

    /// <summary>Show the bomb pot option next to Deal.</summary>
    public bool BombPotEnabled { get; set; }

    /// <summary>What everyone puts in for a bomb pot; 0 = two big blinds.</summary>
    public long BombPotAmount { get; set; }

    /// <summary>The next hand dealt is a bomb pot (then it goes back to normal).</summary>
    public bool BombPotNext { get; set; }

    public long HouseEarnings { get; set; }

    /// <summary>Jackpot cuts the dealer holds for the venue (negative: jackpot they paid out of their own gil).</summary>
    public long JackpotHeld { get; set; }

    /// <summary>
    /// The dealer's own wording for announcements, by message kind. Only changed messages are stored;
    /// the rest use the built-in wording.
    /// </summary>
    public Dictionary<string, string> MessageTemplates { get; set; } = new();

    /// <summary>The version of the terms of use the dealer accepted (0 = never). See TermsPanel.Version.</summary>
    public int AcceptedTermsVersion { get; set; }

    /// <summary>
    /// The table server's session key from signing in with XIVAuth. Empty when signed out. Only kept in memory:
    /// it's saved encrypted for this Windows account, in <see cref="AuthKeyStored"/>.
    /// </summary>
    [JsonIgnore]
    public string AuthSessionKey
    {
        get => authSessionKey ??= SecretStore.Unprotect(authKeyStored);
        set
        {
            authSessionKey = value;
            authKeyStored = SecretStore.Protect(value);
        }
    }

    /// <summary>The session key as saved, encrypted (see <see cref="SecretStore"/>).</summary>
    public string AuthKeyStored
    {
        get => authKeyStored;
        set
        {
            authKeyStored = value;
            authSessionKey = null;
        }
    }

    private string authKeyStored = string.Empty;
    private string? authSessionKey;

    /// <summary>
    /// Versions before 0.5.1 saved the key as plain text under this name. It's read once and saved encrypted from
    /// then on; it's never written.
    /// </summary>
    [JsonProperty("AuthSessionKey")]
    private string LegacyAuthSessionKey
    {
        set
        {
            if (string.IsNullOrEmpty(value) || authKeyStored.Length > 0)
                return;
            AuthSessionKey = value;
            KeyNeedsSave = true;
        }
    }

    /// <summary>A plain-text key from an older version was read: save soon, so it's only on disk encrypted.</summary>
    [JsonIgnore]
    public bool KeyNeedsSave { get; private set; }

    public string AuthName { get; set; } = string.Empty;

    public string AuthWorld { get; set; } = string.Empty;

    public string AuthLodestoneId { get; set; } = string.Empty;

    /// <summary>When the server last confirmed the sign-in; it keeps working offline for a day after.</summary>
    public DateTime? AuthConfirmedUtc { get; set; }

    /// <summary>Send the table to the web server so players can watch it in a browser.</summary>
    public bool WebEnabled { get; set; }

    /// <summary>This dealer's permanent table id, the end of the table link (/t/&lt;id&gt;). Made once.</summary>
    public string WebTableId { get; set; } = string.Empty;

    /// <summary>The secret that proves to the server this table is ours. Made once, never shown in chat.</summary>
    public string WebTableKey { get; set; } = string.Empty;

    /// <summary>Set once the server has accepted this table, after which its id never changes.</summary>
    public bool WebTableClaimed { get; set; }

    public string WebTableTitle { get; set; } = "Texas Hold'em";

    /// <summary>Players can act (fold, call, raise...) from their private page on their turn.</summary>
    public bool WebActions { get; set; } = true;

    /// <summary>Web actions apply at once; off = they're only picked, for the dealer to confirm.</summary>
    public bool WebActionsAuto { get; set; } = true;

    /// <summary>
    /// Which license the web table is shown under: "own" (individual license, no venue branding), a venue
    /// id, or empty for the server's default (the venue you own, else the first that added you).
    /// </summary>
    public string WebShowAs { get; set; } = string.Empty;

    /// <summary>List the table on its venue's page. Off keeps it off the list (it still shows the venue's branding).</summary>
    public bool WebListOnVenuePage { get; set; } = true;

    /// <summary>Each player's private link token, by "name@world" in lower case.</summary>
    public Dictionary<string, string> WebTokens { get; set; } = new();

    public int ButtonIndex { get; set; } = -1;

    public int HandNumber { get; set; }

    public List<SavedSeat> Seats { get; set; } = new();
}

[Serializable]
public sealed class SavedSeat
{
    public string Name { get; set; } = string.Empty;

    public string World { get; set; } = string.Empty;

    public long Stack { get; set; }

    public long TotalBuyIn { get; set; }

    public bool SittingOut { get; set; }
}
