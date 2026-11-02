using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TexasPoker.Engine;
using TexasPoker.Windows;

namespace TexasPoker;

/// <summary>
/// Sends the table to the web server (TexasPokerServer) so players can watch it live in a browser.
/// The shared page gets no hole cards. Each player's private link ends in #token; their cards are sent
/// encrypted with a key from that token (see <see cref="CardSealer"/>), so only their browser can read them.
/// Only the newest state is sent: if the table changes while a send is in flight, the next send
/// carries the latest state and anything in between is skipped.
/// </summary>
public sealed class WebPublisher : IDisposable
{
    /// <summary>The table server every copy of the plugin uses. Set this to your server before building.</summary>
    public const string ServerUrl = "https://eorzeanpoker.app";

    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int TokenLength = 12;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    private readonly Plugin plugin;
    private readonly IFramework framework;
    private readonly TableView labels;
    private readonly object gate = new();
    private string? latest;
    private bool sending;
    private DateTime nextHeartbeat = DateTime.MinValue;

    private static readonly TimeSpan ActionFetchEvery = TimeSpan.FromSeconds(1.5);
    private DateTime nextActionFetch = DateTime.MinValue;
    private bool fetchingActions;

    // Set by the send loop, handled on the framework thread where the config is safe to change.
    private volatile bool claimedNow;
    private volatile bool retakeId;

    public WebPublisher(Plugin plugin, IFramework framework)
    {
        this.plugin = plugin;
        this.framework = framework;
        labels = new TableView(plugin);
        framework.Update += OnUpdate;
    }

    /// <summary>The result of the last send, for the Web tab.</summary>
    public string Status { get; private set; } = "Not sent yet";

    public bool LastSendFailed { get; private set; }

    private Configuration Config => plugin.Configuration;

    private Table Table => plugin.Table;

    /// <summary>Publishing needs the web table turned on and a signed-in dealer.</summary>
    public bool IsConfigured => Config.WebEnabled && Config.WebTableId.Length > 0 && plugin.Auth.SignedIn && plugin.Auth.Allowed;

    private static string BaseUrl => ServerUrl.TrimEnd('/');

    public string TableUrl => $"{BaseUrl}/t/{Config.WebTableId}";

    /// <summary>The token goes after #, which browsers never send to a server.</summary>
    public string PlayerUrl(Seat seat) => $"{TableUrl}#{TokenFor(seat)}";

    public static string NewId(int length) =>
        new(Enumerable.Range(0, length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    /// <summary>
    /// The player's private link token, created the first time it's needed. 12 characters (about 70 bits):
    /// short enough to type from game chat, too long to guess. Shorter tokens are from the old ?p= links,
    /// which servers could log, so they're replaced.
    /// </summary>
    public string TokenFor(Seat seat)
    {
        var key = seat.DisplayName.ToLowerInvariant();
        if (!Config.WebTokens.TryGetValue(key, out var token) || token.Length < TokenLength)
        {
            token = NewId(TokenLength);
            Config.WebTokens[key] = token;
        }

        return token;
    }

    /// <summary>Forgets private links of players who left the table, so an old link can't follow them back.</summary>
    public void ForgetLinksOfAbsentPlayers()
    {
        var seated = Table.Seats.Select(s => s.DisplayName.ToLowerInvariant()).ToHashSet();
        foreach (var name in Config.WebTokens.Keys.Where(k => !seated.Contains(k)).ToList())
            Config.WebTokens.Remove(name);
    }

    /// <summary>Queues the current table to be sent. Call on the framework thread.</summary>
    public void Push()
    {
        if (!IsConfigured)
            return;

        var json = BuildJson();
        lock (gate)
        {
            latest = json;
            if (sending)
                return;
            sending = true;
        }

        nextHeartbeat = DateTime.UtcNow + Heartbeat;
        _ = Task.Run(SendLoop);
    }

    /// <summary>
    /// Takes the table off the website now (the web table was turned off): its pages go back to "waiting"
    /// and it leaves the venue's page. Waits for a send in flight, so that can't put it back. Call on the
    /// framework thread.
    /// </summary>
    public void Close()
    {
        if (Config.WebTableId.Length == 0 || !plugin.Auth.SignedIn)
            return;

        lock (gate)
            latest = null;
        var url = $"{BaseUrl}/api/t/{Config.WebTableId}";
        var session = Config.AuthSessionKey;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 100 && IsSending(); i++)
                await Task.Delay(100).ConfigureAwait(false);
            if (Config.WebEnabled)
                return; // turned back on meanwhile
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                Status = response.IsSuccessStatusCode ? "Taken off the website" : $"Couldn't take it off the website ({(int)response.StatusCode})";
            }
            catch (Exception ex)
            {
                Status = $"Can't reach the server: {ex.Message}";
            }
        });
    }

    private bool IsSending()
    {
        lock (gate)
            return sending;
    }

    public void Dispose() => framework.Update -= OnUpdate;

    // Re-send now and then, so a restarted server gets the table back without waiting for an action.
    private void OnUpdate(IFramework _)
    {
        if (claimedNow)
        {
            claimedNow = false;
            Config.WebTableClaimed = true;
            plugin.Save();
        }

        if (retakeId)
        {
            retakeId = false;
            Config.WebTableId = NewId(6);
            Config.WebTokens.Clear();
            plugin.Save();
        }

        if (IsConfigured && DateTime.UtcNow >= nextHeartbeat)
            Push();

        if (IsConfigured && Config.WebActions && Table.HandInProgress && !fetchingActions && DateTime.UtcNow >= nextActionFetch)
            FetchActions();
    }

    /// <summary>Collects actions players sent from their private pages, then checks and applies them here.</summary>
    private void FetchActions()
    {
        fetchingActions = true;
        nextActionFetch = DateTime.UtcNow + ActionFetchEvery;
        var url = $"{BaseUrl}/api/t/{Config.WebTableId}/actions";
        var session = Config.AuthSessionKey;
        _ = Task.Run(async () =>
        {
            var received = Array.Empty<WebAction>();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    received = JsonSerializer.Deserialize<WebAction[]>(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) ?? [];
            }
            catch (Exception)
            {
                // Try again next time; players can always fall back to chat.
            }

            await framework.RunOnFrameworkThread(() =>
            {
                fetchingActions = false;
                foreach (var action in received)
                    HandleAction(action);
            }).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Applies one web action if it's genuine (signed with that player's token) and still current: this
    /// table, this hand, this exact turn, and that player is the one to act. Anything else is ignored.
    /// </summary>
    private void HandleAction(WebAction web)
    {
        var tableId = Config.WebTableId;
        var seat = Table.Seats.FirstOrDefault(s => CardSealer.Lookup(tableId, TokenFor(s)) == web.k);
        if (seat is null || web.a is null || web.s is null || !CardSealer.VerifyAction(tableId, TokenFor(seat), web.a, web.s))
            return;

        ActionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ActionPayload>(web.a);
        }
        catch (JsonException)
        {
            return;
        }

        if (payload is null || payload.table != tableId || payload.hand != Table.HandNumber ||
            payload.turn != Table.Sequence || Table.Actor != seat)
            return; // stale (the table moved on) or not their turn

        var options = Table.GetOptions();
        ParsedAction? action = payload.action switch
        {
            "fold" => new ParsedAction(PlayerAction.Fold, 0),
            "check" when options.CanCheck => new ParsedAction(PlayerAction.Check, 0),
            "call" when options.ToCall > 0 => new ParsedAction(PlayerAction.Call, 0),
            "call" when options.CanCheck => new ParsedAction(PlayerAction.Check, 0),
            "allin" => new ParsedAction(PlayerAction.AllIn, 0),
            "raise" when payload.amount >= options.MaxRaiseTo => new ParsedAction(PlayerAction.AllIn, 0),
            "raise" when options.CanRaise && payload.amount >= options.MinRaiseTo => new ParsedAction(PlayerAction.BetOrRaise, payload.amount),
            _ => null,
        };

        if (action is null)
        {
            plugin.Chat.PrintLocal($"{seat.Name} sent \"{payload.action} {payload.amount:N0}\" from the web, which isn't possible right now.");
            return;
        }

        plugin.ApplyWebAction(seat, action);
    }

    /// <summary>One queued action as the server hands it over: lookup, payload, signature.</summary>
    private sealed record WebAction(string? k, string? a, string? s);

    /// <summary>What the page signs: which table, hand and turn it's for, and the action.</summary>
    private sealed record ActionPayload(string? table, int hand, int turn, string? action, long amount);

    /// <summary>"id:key", so a dealer can move their table (and its link) to another PC or a reinstall.</summary>
    public string BackupCode => $"{Config.WebTableId}:{Config.WebTableKey}";

    /// <summary>Takes over the table in a backup code. Returns false if the code isn't valid.</summary>
    public bool Restore(string code)
    {
        var parts = code.Trim().Split(':');
        if (parts.Length != 2 || parts[0].Length < 4 || parts[1].Length < 24 ||
            !parts.All(p => p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return false;

        Config.WebTableId = parts[0];
        Config.WebTableKey = parts[1];
        Config.WebTableClaimed = true;
        Config.WebTokens.Clear();
        return true;
    }

    private async Task SendLoop()
    {
        while (true)
        {
            string json;
            lock (gate)
            {
                if (latest is null)
                {
                    sending = false;
                    return;
                }

                json = latest;
                latest = null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/t/{Config.WebTableId}");
                // The session proves who's dealing. The table key proves a table from before sign-in was
                // ours, so the server can move it (and its link) to this dealer's account.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.AuthSessionKey);
                request.Headers.Add("X-Table-Key", Config.WebTableKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                LastSendFailed = !response.IsSuccessStatusCode;
                if (response.IsSuccessStatusCode)
                {
                    Status = $"Updated at {DateTime.Now:HH:mm:ss}";
                    claimedNow = !Config.WebTableClaimed;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && !Config.WebTableClaimed)
                {
                    // Someone else already has this id (very unlikely): pick another before anyone has the link.
                    retakeId = true;
                    Status = "That table id was taken; picking a new one...";
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Status = "The server wants you to sign in again.";
                    plugin.Auth.CheckSoon();
                }
                else
                {
                    Status = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? "The server says this table belongs to another dealer."
                        : $"The server answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}";
                }
            }
            catch (Exception ex)
            {
                LastSendFailed = true;
                Status = $"Can't reach the server: {ex.Message}";
            }
        }
    }

    private string BuildJson()
    {
        var showdown = Table.Street == Street.HandOver && Table.LastResults.Any(r => r.Hand is not null);
        var buttonShown = Table.HandInProgress || Table.Street == Street.HandOver;

        var seats = Table.Seats.Select((seat, i) => new
        {
            name = seat.Name,
            world = seat.World,
            stack = seat.Stack,
            bet = seat.StreetBet,
            status = labels.StatusText(seat),
            tag = labels.PositionLabel(i).Replace("D", string.Empty).Trim(),
            isActor = Table.Actor == seat,
            faded = seat.SittingOut || seat.Folded || (Table.HandInProgress && !seat.InHand),
            hasCards = seat.InHand && !seat.Folded && seat.HoleCards.Count > 0 && Table.Street != Street.HandOver,
            shown = showdown && seat.Contending && seat.HoleCards.Count > 0 ? Cards(seat.HoleCards) : null,
        }).ToList();

        var results = Table.HandNumber > 0
            ? Table.LastResults.Select(r =>
            {
                var winners = string.Join(", ", r.Winners.Select(w => w.Name));
                return r.Returned
                    ? $"{r.Amount:N0} back to {winners} (uncalled)"
                    : $"{r.Label}: {winners} wins {r.Amount:N0}" + (r.Hand is { } hand ? $" ({hand})" : string.Empty);
            }).ToList()
            : [];

        var state = new
        {
            title = Config.WebTableTitle.Trim().Length > 0 ? Config.WebTableTitle.Trim() : "Texas Hold'em",
            handNumber = Table.HandNumber,
            street = Table.Street.ToString(),
            inProgress = Table.HandInProgress,
            pot = Table.Pot,
            sb = Table.SmallBlind,
            bb = Table.BigBlind,
            rake = Table.RakePercent > 0 ? $"{Table.RakePercent:0.##}%" : null,
            lastAction = Table.HandInProgress ? Table.LastAction : results.FirstOrDefault(),
            actions = Table.HandActions.Select(a => new { street = a.Street.ToString(), text = a.Text }),
            button = buttonShown ? Table.ButtonIndex : -1,
            board = Cards(Table.Board),
            seats,
            results = Table.HandInProgress ? [] : results,
            resultsHand = Table.HandNumber,
            turn = Table.Sequence,
            webActions = Config.WebActions,
            options = ActorOptions(),
        };

        // Each player's own data, encrypted for their browser and filed under a lookup the server can't reverse.
        var tableId = Config.WebTableId;
        var privates = Table.Seats.ToDictionary(
            seat => CardSealer.Lookup(tableId, TokenFor(seat)),
            seat => new
            {
                c = CardSealer.Seal(tableId, TokenFor(seat), JsonSerializer.Serialize(new
                {
                    name = seat.Name,
                    world = seat.World,
                    cards = seat.InHand && seat.HoleCards.Count > 0 ? Cards(seat.HoleCards) : [],
                    hand = seat.InHand && !seat.Folded ? Table.CurrentHand(seat)?.Describe() : null,
                })),
            });

        var showAs = Config.WebShowAs.Length > 0 ? Config.WebShowAs : null;
        return JsonSerializer.Serialize(new { state, privates, showAs });
    }

    /// <summary>What the player to act may do, for the buttons on their private page. Public: it's on the table anyway.</summary>
    private object? ActorOptions()
    {
        if (Table.Actor is not { } actor)
            return null;

        var o = Table.GetOptions();
        return new
        {
            toCall = o.ToCall,
            callAmount = o.CallAmount,
            canCheck = o.CanCheck,
            canRaise = o.CanRaise && o.MaxRaiseTo > o.MinRaiseTo,
            isBet = o.IsBet,
            minRaiseTo = o.MinRaiseTo,
            maxRaiseTo = o.MaxRaiseTo,
            allIn = actor.Stack + actor.StreetBet,
        };
    }

    private static object[] Cards(System.Collections.Generic.IEnumerable<Card> cards) =>
        cards.Select(c => (object)new { r = c.RankText, s = c.SuitText(false) }).ToArray();
}
