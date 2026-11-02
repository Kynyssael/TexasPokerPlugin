using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Utility;

namespace TexasPoker.Windows;

/// <summary>
/// The main window's Venue tab, shown to dealers with a venue licence. The venue's owner adds and
/// removes its dealers here, up to the seats in the licence; other dealers see which venue they deal for.
/// </summary>
public sealed class VenueTab
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const int MaxLogoBytes = 256 * 1024;
    private static readonly Vector3 HouseFelt = FromHex("#1a6b3f");
    private static readonly Vector3 HouseAccent = FromHex("#f2c14e");

    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private List<OwnedVenue> owned = [];
    private bool loaded;
    private bool busy;
    private string message = string.Empty;
    private bool messageIsError;
    private readonly Dictionary<string, string> newDealer = new();
    /// <summary>Colours being picked, per venue, until they're saved.</summary>
    private readonly Dictionary<string, (Vector3 Felt, Vector3 Accent)> colours = new();

    public VenueTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private static string BaseUrl => WebPublisher.ServerUrl.TrimEnd('/');

    public void Draw()
    {
        fileDialog.Draw();
        var auth = plugin.Auth;
        foreach (var venue in auth.Venues)
        {
            if (venue.IsOwner)
                continue;
            ImGui.TextColored(DealerControls.Gold, venue.Name);
            ImGui.TextWrapped($"You deal for {venue.Name}. Its owner, {venue.OwnerName}, manages its dealers. " +
                              "Your table on the website shows the venue's name, logo and colours, and is listed on its page.");
            DrawPageLink(venue.Page);
            ImGui.Separator();
        }

        if (!auth.Venues.Any(v => v.IsOwner))
        {
            if (auth.HasIndividualLicence)
                ImGui.TextColored(DealerControls.Muted, "You also have your own individual licence.");
            return;
        }

        if (!loaded && !busy)
            Load();

        ImGui.TextWrapped("You own a venue licence. Add the dealers who deal for your venue: they can sign in to the plugin " +
                          "once added, and their tables show your venue's name and logo. You take one of the seats.");
        if (ImGui.SmallButton("Refresh"))
            Load();
        if (busy)
        {
            ImGui.SameLine();
            ImGui.TextColored(DealerControls.Muted, "Working...");
        }

        if (message.Length > 0)
            ImGui.TextColored(messageIsError ? DealerControls.Red : DealerControls.Good, message);

        foreach (var venue in owned)
            DrawOwned(venue);
    }

    private void DrawOwned(OwnedVenue venue)
    {
        ImGui.PushID(venue.Id);
        ImGui.Separator();
        ImGui.TextColored(DealerControls.Gold, venue.Name);
        ImGui.SameLine();
        ImGui.TextColored(venue.Used >= venue.Seats ? DealerControls.Red : DealerControls.Muted,
                          $"{venue.Used} of {venue.Seats} dealer seats used");

        if (ImGui.BeginTable("##dealers", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("dealer");
            ImGui.TableSetupColumn("remove", ImGuiTableColumnFlags.WidthFixed, 90);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{venue.OwnerName}  (owner)");
            ImGui.TableNextColumn();

            foreach (var dealer in venue.Dealers)
            {
                ImGui.PushID(dealer.Key);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(dealer.Label);
                if (dealer.Pending)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(DealerControls.Muted, "(added, hasn't signed in yet)");
                }

                ImGui.TableNextColumn();
                ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl);
                if (ImGui.SmallButton("Remove"))
                    Send(HttpMethod.Delete, $"/api/venue/{venue.Id}/dealers/{Uri.EscapeDataString(dealer.Key)}", null,
                         $"Removed {dealer.Label}.");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Hold Ctrl and click. They're signed out unless they have another licence.");
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        var text = newDealer.GetValueOrDefault(venue.Id, string.Empty);
        var full = venue.Used >= venue.Seats;
        ImGui.BeginDisabled(full || busy);
        ImGui.SetNextItemWidth(260);
        var enter = ImGui.InputTextWithHint("##new", "First Last@World", ref text, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        newDealer[venue.Id] = text;
        ImGui.SameLine();
        if ((ImGui.Button("Add dealer") || enter) && text.Trim().Length > 0)
        {
            Add(venue, text.Trim());
            newDealer[venue.Id] = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Add my target"))
        {
            if (plugin.GetTargetPlayer() is { } target)
                Add(venue, $"{target.Name}@{target.World}");
            else
                ShowMessage("Target a player first.", true);
        }

        ImGui.EndDisabled();
        if (full)
            ImGui.TextColored(DealerControls.Muted, "All seats are taken. Remove a dealer, or ask Elenor for more seats.");

        ImGui.Spacing();
        ImGui.TextColored(DealerControls.Gold, "Venue page");
        ImGui.SameLine();
        DrawPageLink(venue.Page);
        ImGui.TextColored(DealerControls.Muted, "Lists your venue's open tables, so players can find them. Post it once on your Discord or Carrd.");

        ImGui.Spacing();
        DrawLogo(venue);
        ImGui.Spacing();
        DrawColours(venue);
        ImGui.PopID();
    }

    /// <summary>A venue page's address with Open and Copy buttons.</summary>
    private static void DrawPageLink(string page)
    {
        if (page.Length == 0)
            return;
        var url = BaseUrl + page;
        ImGui.TextUnformatted(url.Replace("https://", string.Empty));
        ImGui.SameLine();
        if (ImGui.SmallButton("Open##page"))
            Util.OpenLink(url);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##page"))
            ImGui.SetClipboardText(url);
    }

    private void DrawColours(OwnedVenue venue)
    {
        var saved = (venue.Felt ?? HouseFelt, venue.Accent ?? HouseAccent);
        var (felt, accent) = colours.GetValueOrDefault(venue.Id, saved);

        ImGui.TextColored(DealerControls.Gold, "Colours");
        ImGui.SameLine();
        ImGui.TextUnformatted(venue.Felt is null ? "The Eorzean Poker green and gold." : "Your own, on your tables and venue page.");
        ImGui.BeginDisabled(busy);
        var flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.DisplayHex;
        var changed = ImGui.ColorEdit3("Felt##felt", ref felt, flags);
        ImGui.SameLine(0, 24);
        changed |= ImGui.ColorEdit3("Accent (trim, pot, titles)##accent", ref accent, flags);
        if (changed)
            colours[venue.Id] = (felt, accent);

        var edited = colours.ContainsKey(venue.Id) && (ToHex(felt) != ToHex(saved.Item1) || ToHex(accent) != ToHex(saved.Item2));
        ImGui.BeginDisabled(!edited);
        if (ImGui.Button("Save colours"))
        {
            var body = new StringContent(JsonSerializer.Serialize(new { felt = ToHex(felt), accent = ToHex(accent) }), Encoding.UTF8, "application/json");
            Send(HttpMethod.Post, $"/api/venue/{venue.Id}/theme", body, "Colours saved. Open tables change straight away.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Undo"))
            colours.Remove(venue.Id);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(venue.Felt is null);
        if (ImGui.Button("Use house colours"))
        {
            colours.Remove(venue.Id);
            Send(HttpMethod.Post, $"/api/venue/{venue.Id}/theme", new StringContent("null", Encoding.UTF8, "application/json"),
                 "Back to the Eorzean Poker colours.");
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.TextColored(DealerControls.Muted, "Pick a dark felt and a bright accent, so text stays readable. The website refuses colours that aren't.");
    }

    private static string ToHex(Vector3 c) =>
        $"#{(int)MathF.Round(Math.Clamp(c.X, 0, 1) * 255):x2}{(int)MathF.Round(Math.Clamp(c.Y, 0, 1) * 255):x2}{(int)MathF.Round(Math.Clamp(c.Z, 0, 1) * 255):x2}";

    private static Vector3 FromHex(string hex) => new(
        Convert.ToInt32(hex.Substring(1, 2), 16) / 255f,
        Convert.ToInt32(hex.Substring(3, 2), 16) / 255f,
        Convert.ToInt32(hex.Substring(5, 2), 16) / 255f);

    private static Vector3? ColourOf(JsonElement theme, string name) =>
        theme.ValueKind == JsonValueKind.Object && theme.TryGetProperty(name, out var value) && value.GetString() is { Length: 7 } hex
            ? FromHex(hex)
            : null;

    private void DrawLogo(OwnedVenue venue)
    {
        ImGui.TextColored(DealerControls.Gold, "Logo");
        ImGui.SameLine();
        ImGui.TextUnformatted(venue.Logo is null ? "None yet." : "Shown on your venue's tables.");
        if (venue.Logo is { } logo)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("View"))
                Util.OpenLink(BaseUrl + logo);
            ImGui.SameLine();
            ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("Remove logo"))
                Send(HttpMethod.Delete, $"/api/venue/{venue.Id}/logo?which=live", null, "Logo removed.");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click. Your tables go back to the Eorzean Poker logo.");
        }

        if (venue.LogoPending)
        {
            ImGui.TextColored(DealerControls.Gold, "Your new logo is waiting for Elenor's approval.");
            ImGui.SameLine();
            ImGui.BeginDisabled(busy);
            if (ImGui.SmallButton("Withdraw"))
                Send(HttpMethod.Delete, $"/api/venue/{venue.Id}/logo?which=pending", null, "Upload withdrawn.");
            ImGui.EndDisabled();
        }

        ImGui.BeginDisabled(busy);
        if (ImGui.Button(venue.LogoPending ? "Upload a different logo..." : "Upload logo..."))
        {
            var id = venue.Id;
            fileDialog.OpenFileDialog("Choose your venue's logo", "Images{.png,.jpg,.jpeg,.webp}", (ok, file) =>
            {
                if (ok)
                    UploadLogo(id, file);
            });
        }

        ImGui.EndDisabled();
        ImGui.TextColored(DealerControls.Muted, "PNG, JPEG or WebP under 256 KB, ideally square with a transparent background. " +
                                                "Elenor checks each new logo before it shows.");
    }

    private void UploadLogo(string venueId, string file)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(file);
        }
        catch (Exception ex)
        {
            ShowMessage($"Can't read that file: {ex.Message}", true);
            return;
        }

        if (data.Length > MaxLogoBytes)
        {
            ShowMessage($"That image is {data.Length / 1024} KB; the logo must be under 256 KB.", true);
            return;
        }

        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Send(HttpMethod.Post, $"/api/venue/{venueId}/logo", content, "Logo uploaded. It shows on your tables once Elenor approves it.");
    }

    private void Add(OwnedVenue venue, string who)
    {
        var body = new StringContent(JsonSerializer.Serialize(new { who }), Encoding.UTF8, "application/json");
        Send(HttpMethod.Post, $"/api/venue/{venue.Id}/dealers", body, $"Added {who}. They can sign in to the plugin now.");
    }

    private void Load() => Send(HttpMethod.Get, "/api/venue", null, null);

    /// <summary>Sends a request; every venue reply carries the updated venue(s), which replace what's shown.</summary>
    private void Send(HttpMethod method, string path, HttpContent? content, string? success)
    {
        var key = plugin.Configuration.AuthSessionKey;
        busy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(method, BaseUrl + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = content;
                using var response = await Http.SendAsync(request).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Finish(() => ShowMessage(text.Length > 0 ? text : $"The server said {(int)response.StatusCode}.", true));
                    return;
                }

                using var doc = JsonDocument.Parse(text);
                var venues = doc.RootElement.TryGetProperty("venues", out var list)
                    ? ParseAll(list)
                    : [ParseVenue(doc.RootElement)];
                Finish(() =>
                {
                    loaded = true;
                    foreach (var venue in venues)
                        colours.Remove(venue.Id);
                    if (path == "/api/venue")
                        owned = venues;
                    else
                        owned = owned.ConvertAll(v => venues.Find(n => n.Id == v.Id) ?? v);
                    if (success is not null)
                        ShowMessage(success, false);
                });
            }
            catch (Exception ex)
            {
                Finish(() => ShowMessage($"Can't reach the server: {ex.Message}", true));
            }
        });
    }

    private void ShowMessage(string text, bool error)
    {
        message = text;
        messageIsError = error;
    }

    private void Finish(Action action) => Plugin.Framework.RunOnFrameworkThread(() =>
    {
        busy = false;
        loaded = true;
        action();
    });

    private static List<OwnedVenue> ParseAll(JsonElement list)
    {
        var result = new List<OwnedVenue>();
        foreach (var v in list.EnumerateArray())
            result.Add(ParseVenue(v));
        return result;
    }

    private static OwnedVenue ParseVenue(JsonElement v)
    {
        var dealers = new List<VenueDealer>();
        foreach (var d in v.GetProperty("dealers").EnumerateArray())
        {
            dealers.Add(new VenueDealer(
                d.GetProperty("key").GetString() ?? string.Empty,
                d.GetProperty("label").GetString() ?? string.Empty,
                d.GetProperty("pending").GetBoolean()));
        }

        var theme = v.TryGetProperty("theme", out var t) ? t : default;
        return new OwnedVenue(
            v.GetProperty("id").GetString() ?? string.Empty,
            v.GetProperty("name").GetString() ?? string.Empty,
            v.GetProperty("seats").GetInt32(),
            v.GetProperty("used").GetInt32(),
            v.GetProperty("ownerName").GetString() ?? string.Empty,
            dealers,
            v.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.String ? logo.GetString() : null,
            v.TryGetProperty("logoPending", out var pending) && pending.ValueKind == JsonValueKind.True,
            v.TryGetProperty("page", out var page) ? page.GetString() ?? string.Empty : string.Empty,
            ColourOf(theme, "felt"),
            ColourOf(theme, "accent"));
    }

    private sealed record OwnedVenue(
        string Id, string Name, int Seats, int Used, string OwnerName, List<VenueDealer> Dealers, string? Logo, bool LogoPending,
        string Page, Vector3? Felt, Vector3? Accent);

    private sealed record VenueDealer(string Key, string Label, bool Pending);
}
