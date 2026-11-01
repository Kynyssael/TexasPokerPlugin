using System;
using System.Collections.Generic;
using Dalamud.Configuration;

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

    public bool ShowHoleCards { get; set; }

    /// <summary>Action buttons only pick the action; a separate Confirm button applies it.</summary>
    public bool ConfirmActions { get; set; } = true;

    /// <summary>When the player to act types their action in the table channel, pick it for the dealer to confirm.</summary>
    public bool ReadChatActions { get; set; } = true;

    /// <summary>Also read actions players send the dealer by /tell.</summary>
    public bool ReadTellActions { get; set; } = true;

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

    public long HouseEarnings { get; set; }

    /// <summary>
    /// The dealer's own wording for announcements, by message kind. Only changed messages are stored;
    /// the rest use the built-in wording.
    /// </summary>
    public Dictionary<string, string> MessageTemplates { get; set; } = new();

    /// <summary>Send the table to the web server so players can watch it in a browser.</summary>
    public bool WebEnabled { get; set; }

    /// <summary>This dealer's permanent table id, the end of the table link (/t/&lt;id&gt;). Made once.</summary>
    public string WebTableId { get; set; } = string.Empty;

    /// <summary>The secret that proves to the server this table is ours. Made once, never shown in chat.</summary>
    public string WebTableKey { get; set; } = string.Empty;

    /// <summary>Set once the server has accepted this table, after which its id never changes.</summary>
    public bool WebTableClaimed { get; set; }

    public string WebTableTitle { get; set; } = "Texas Hold'em";

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
