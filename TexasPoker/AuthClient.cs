using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace TexasPoker;

/// <summary>
/// Dealer sign-in. The plugin only deals for a character signed in through XIVAuth (an approved
/// character on Elenor's private XIVAuth app). The table server runs the XIVAuth device sign-in and
/// gives back a session key, which this checks now and then. If the server can't be reached, the last
/// confirmed sign-in keeps working for <see cref="OfflineGrace"/>, so a server outage doesn't stop a game.
/// </summary>
public sealed class AuthClient : IDisposable
{
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromHours(24);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Plugin plugin;
    private readonly IFramework framework;
    private DateTime nextCheck = DateTime.MinValue;
    private DateTime nextPoll = DateTime.MaxValue;
    private string? loginId;
    private bool busy;
    private bool handGrace;

    public AuthClient(Plugin plugin, IFramework framework)
    {
        this.plugin = plugin;
        this.framework = framework;
        framework.Update += OnUpdate;
    }

    public enum SignInStep
    {
        Idle,
        Starting,
        WaitingForUser,
    }

    public SignInStep Step { get; private set; } = SignInStep.Idle;

    /// <summary>The code the dealer enters on XIVAuth while signing in.</summary>
    public string UserCode { get; private set; } = string.Empty;

    public string VerificationUrl { get; private set; } = string.Empty;

    /// <summary>The latest problem or news to show on the sign-in screen.</summary>
    public string Message { get; private set; } = string.Empty;

    public bool Checking => busy && Step == SignInStep.Idle;

    /// <summary>The venues the signed-in character owns or deals for, as of the last check.</summary>
    public IReadOnlyList<VenueMembership> Venues { get; private set; } = [];

    /// <summary>The signed-in character has an individual licence (not only a venue seat).</summary>
    public bool HasIndividualLicence { get; private set; }

    private Configuration Config => plugin.Configuration;

    private static string BaseUrl => WebPublisher.ServerUrl.TrimEnd('/');

    public bool SignedIn => Config.AuthSessionKey.Length > 0;

    public string SignedInAs => SignedIn ? $"{Config.AuthName}@{Config.AuthWorld}" : string.Empty;

    /// <summary>The server confirmed the sign-in recently enough.</summary>
    public bool Confirmed => SignedIn && Config.AuthConfirmedUtc is { } at && DateTime.UtcNow - at < OfflineGrace;

    /// <summary>The character you're playing is the one that signed in.</summary>
    public bool CharacterMatches => SignedIn && plugin.IsLocalCharacter(Config.AuthName, Config.AuthWorld);

    public bool Unlocked => Confirmed && CharacterMatches;

    /// <summary>
    /// Whether the dealer tools may be used. A hand that started while unlocked can always be finished,
    /// so a sign-out or revoke never strands chips mid-hand.
    /// </summary>
    public bool Allowed => Unlocked || (handGrace && plugin.Table.HandInProgress);

    public void Dispose() => framework.Update -= OnUpdate;

    public void StartSignIn()
    {
        if (busy)
            return;

        busy = true;
        Step = SignInStep.Starting;
        Message = string.Empty;
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await Http.PostAsync($"{BaseUrl}/api/auth/start", null).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Finish(() => Fail($"The server couldn't start the sign-in: {text}"));
                    return;
                }

                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                var id = root.GetProperty("loginId").GetString() ?? string.Empty;
                var code = root.GetProperty("userCode").GetString() ?? string.Empty;
                var url = root.TryGetProperty("verificationUriComplete", out var full) && full.ValueKind == JsonValueKind.String
                    ? full.GetString()!
                    : root.GetProperty("verificationUri").GetString() ?? string.Empty;

                Finish(() =>
                {
                    loginId = id;
                    UserCode = code;
                    VerificationUrl = url;
                    Step = SignInStep.WaitingForUser;
                    nextPoll = DateTime.UtcNow + PollEvery;
                    OpenSignInPage();
                });
            }
            catch (Exception ex)
            {
                Finish(() => Fail($"Can't reach the server: {ex.Message}"));
            }
        });
    }

    public void OpenSignInPage()
    {
        if (VerificationUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            VerificationUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            Util.OpenLink(VerificationUrl);
    }

    public void CancelSignIn()
    {
        loginId = null;
        Step = SignInStep.Idle;
        nextPoll = DateTime.MaxValue;
        Message = string.Empty;
    }

    public void SignOut()
    {
        var key = Config.AuthSessionKey;
        ClearSession(string.Empty);
        if (key.Length == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/auth/logout");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var _ = await Http.SendAsync(request).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Signing out locally is what matters; the server session expires on its own.
            }
        });
    }

    /// <summary>Asks the server about the sign-in again soon (e.g. after it refused an update).</summary>
    public void CheckSoon() => nextCheck = DateTime.MinValue;

    private void OnUpdate(IFramework _)
    {
        if (Unlocked)
            handGrace = true;
        else if (!plugin.Table.HandInProgress)
            handGrace = false;

        if (busy)
            return;

        if (Step == SignInStep.WaitingForUser && DateTime.UtcNow >= nextPoll)
            Poll();
        else if (Step == SignInStep.Idle && SignedIn && DateTime.UtcNow >= nextCheck)
            Check();
    }

    private void Poll()
    {
        var id = loginId;
        if (id is null)
            return;

        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await Http.GetStringAsync($"{BaseUrl}/api/auth/login/{Uri.EscapeDataString(id)}").ConfigureAwait(false);
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                var status = root.GetProperty("status").GetString();

                if (status == "pending")
                {
                    Finish(() => nextPoll = DateTime.UtcNow + PollEvery);
                    return;
                }

                if (status == "done")
                {
                    var key = root.GetProperty("sessionKey").GetString() ?? string.Empty;
                    var character = root.GetProperty("character");
                    var name = character.GetProperty("name").GetString() ?? string.Empty;
                    var world = character.GetProperty("world").GetString() ?? string.Empty;
                    var lodestone = character.GetProperty("lodestoneId").GetString() ?? string.Empty;
                    Finish(() =>
                    {
                        Config.AuthSessionKey = key;
                        Config.AuthName = name;
                        Config.AuthWorld = world;
                        Config.AuthLodestoneId = lodestone;
                        Config.AuthConfirmedUtc = DateTime.UtcNow;
                        CancelSignIn();
                        nextCheck = DateTime.MinValue; // fetch the licence details right away
                        plugin.Save();
                        plugin.Chat.PrintLocal($"Signed in as {name}@{world}.");
                    });
                    return;
                }

                var who = root.TryGetProperty("character", out var c) && c.ValueKind == JsonValueKind.Object
                    ? $"{c.GetProperty("name").GetString()}@{c.GetProperty("world").GetString()}"
                    : "your character";
                var reason = status switch
                {
                    "notapproved" => $"{who} isn't approved to deal yet. Ask Elenor, or your venue's owner to add it, then sign in again.",
                    "denied" => "The sign-in was cancelled on XIVAuth.",
                    "expired" => "The code expired before it was entered. Try again.",
                    "blocked" => "This character isn't allowed to use the plugin.",
                    "nocharacter" => "XIVAuth didn't share a verified character. Pick your character when you sign in.",
                    _ => "The sign-in didn't go through. Try again.",
                };
                Finish(() => Fail(reason));
            }
            catch (Exception)
            {
                // Network hiccup: keep waiting for the dealer.
                Finish(() => nextPoll = DateTime.UtcNow + PollEvery);
            }
        });
    }

    private void Check()
    {
        var key = Config.AuthSessionKey;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/auth/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Finish(() => ClearSession("Your sign-in ended: it expired, was revoked, or your character isn't approved. Sign in again."));
                    return;
                }

                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var root = json.RootElement;
                var name = root.GetProperty("name").GetString() ?? Config.AuthName;
                var world = root.GetProperty("world").GetString() ?? Config.AuthWorld;
                var individual = root.TryGetProperty("individual", out var ind) && ind.ValueKind == JsonValueKind.True;
                var venues = new List<VenueMembership>();
                if (root.TryGetProperty("venues", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in list.EnumerateArray())
                    {
                        venues.Add(new VenueMembership(
                            v.GetProperty("id").GetString() ?? string.Empty,
                            v.GetProperty("name").GetString() ?? string.Empty,
                            v.TryGetProperty("isOwner", out var owner) && owner.ValueKind == JsonValueKind.True,
                            v.TryGetProperty("ownerName", out var ownerName) ? ownerName.GetString() ?? string.Empty : string.Empty));
                    }
                }

                Finish(() =>
                {
                    if (Config.AuthSessionKey != key)
                        return; // signed out or in again meanwhile
                    var changed = name != Config.AuthName || world != Config.AuthWorld;
                    Config.AuthName = name;
                    Config.AuthWorld = world;
                    Venues = venues;
                    HasIndividualLicence = individual;
                    Config.AuthConfirmedUtc = DateTime.UtcNow;
                    Message = string.Empty;
                    nextCheck = DateTime.UtcNow + CheckEvery;
                    if (changed)
                        plugin.Save();
                });
            }
            catch (Exception ex)
            {
                // Server unreachable: the last confirmation keeps working until OfflineGrace runs out.
                Finish(() =>
                {
                    Message = Confirmed ? string.Empty : $"Can't reach the server to confirm your sign-in: {ex.Message}";
                    nextCheck = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                });
            }
        });
    }

    private void ClearSession(string message)
    {
        Config.AuthSessionKey = string.Empty;
        Config.AuthConfirmedUtc = null;
        Venues = [];
        HasIndividualLicence = false;
        Message = message;
        plugin.Save();
    }

    private void Fail(string message)
    {
        CancelSignIn();
        Message = message;
    }

    /// <summary>Runs the result on the framework thread, where the config and UI state are safe to change.</summary>
    private void Finish(Action action) => framework.RunOnFrameworkThread(() =>
    {
        busy = false;
        action();
    });
}

/// <summary>A venue licence the signed-in character holds a seat in.</summary>
public sealed record VenueMembership(string Id, string Name, bool IsOwner, string OwnerName);
