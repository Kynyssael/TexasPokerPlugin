using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace TexasPoker;

/// <summary>
/// Links a player's Discord account for the tournament table roles. The player signs in with Discord at
/// eorzeanpoker.app/discord, gets a code, and /tells it to a dealer: "!link CODE". The game vouches for who sent the
/// tell, so this plugin sends the code with the sender's Name@World, and tells them how it went.
/// </summary>
public sealed partial class DiscordLinker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Plugin plugin;
    private readonly IFramework framework;

    // One link try per player every few seconds: someone spamming "!link" tells gets no flood of answers.
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, DateTime> lastTry = new(StringComparer.OrdinalIgnoreCase);

    public DiscordLinker(Plugin plugin, IFramework framework)
    {
        this.plugin = plugin;
        this.framework = framework;
    }

    [GeneratedRegex(@"^\s*!link\s+([A-Za-z0-9]{6})\s*$")]
    private static partial Regex LinkTell();

    /// <summary>The code in a "!link CODE" tell, or null.</summary>
    public static string? CodeIn(string text) =>
        LinkTell().Match(text) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;

    /// <summary>A "!link CODE" tell from Name@World: links them, then tells them (and the dealer) how it went.</summary>
    public void Link(string name, string world, string code)
    {
        if (plugin.Configuration.AuthSessionKey.Length == 0)
        {
            plugin.Chat.PrintLocal($"{name} sent a Discord link code, but you're not signed in: sign in on the Web tab, then ask them to send it again.");
            return;
        }

        if (world.Length == 0)
        {
            plugin.Chat.PrintLocal($"{name} sent a Discord link code, but the game didn't say their world. Ask them to send it again.");
            return;
        }

        var who = $"{name}@{world}";
        var now = DateTime.UtcNow;
        if (lastTry.TryGetValue(who, out var last) && now - last < Cooldown)
            return;
        foreach (var old in lastTry.Where(e => now - e.Value >= Cooldown).Select(e => e.Key).ToList())
            lastTry.Remove(old);
        lastTry[who] = now;

        var url = $"{WebPublisher.ServerUrl.TrimEnd('/')}/api/discord/link";
        var session = plugin.Configuration.AuthSessionKey;
        var json = JsonSerializer.Serialize(new { code, name, world });
        _ = Task.Run(async () =>
        {
            string reply;
            string local;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(text);
                    var username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
                    reply = $"Linked to Discord{(string.IsNullOrEmpty(username) ? string.Empty : $" ({username})")}. You'll get your table's role on the Discord server at tournaments.";
                    local = $"Linked {name}@{world} to Discord{(string.IsNullOrEmpty(username) ? string.Empty : $" ({username})")}.";
                }
                else if ((int)response.StatusCode == 404)
                {
                    reply = "That Discord code didn't work (codes last 15 minutes). Get a new one at eorzeanpoker.app/discord and send it again.";
                    local = $"{name}@{world}'s Discord code didn't work; told them to get a new one.";
                }
                else
                {
                    reply = "Couldn't link your Discord right now. Try again in a minute.";
                    local = $"Couldn't link {name}@{world}'s Discord ({(int)response.StatusCode}: {text}).";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                reply = "Couldn't reach the server to link your Discord. Try again in a minute.";
                local = $"Couldn't reach the server to link {name}@{world}'s Discord: {ex.Message}";
            }

            await framework.RunOnFrameworkThread(() =>
            {
                plugin.TellTournamentPlayer(name, world, reply);
                plugin.Chat.PrintLocal(local);
            }).ConfigureAwait(false);
        });
    }
}
