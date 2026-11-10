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
/// The main window's Venue tab, shown to dealers with a venue license. The venue's owner adds and
/// removes its dealers here, up to the seats in the license; other dealers see which venue they deal for.
/// </summary>
public sealed class VenueTab
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const int MaxLogoBytes = 256 * 1024;
    private static readonly Vector3 HouseFelt = FromHex("#1a6b3f");
    private static readonly Vector3 HouseAccent = FromHex("#f2c14e");
    private static readonly Vector3 HouseBack = FromHex("#33478f");
    private static readonly Vector3 HouseBack2 = FromHex("#4258a6");

    private static readonly List<(string Id, string Text)> Faces = [("cream", "Cream"), ("white", "White"), ("parchment", "Parchment")];
    private static readonly List<(string Id, string Text)> Corners = [("classic", "Classic"), ("round", "Round")];
    private static readonly List<(string Id, string Text)> Fonts = [("standard", "Standard"), ("serif", "Classic serif"), ("typewriter", "Typewriter")];
    private static readonly List<(string Id, string Text)> Backs =
        [("stripes", "Two-colour stripes"), ("solid", "One plain colour"), ("logo", "Your logo on its colour"), ("image", "Your own picture")];

    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private List<OwnedVenue> owned = [];
    private bool loaded;
    private bool busy;
    private string message = string.Empty;
    private bool messageIsError;
    private readonly Dictionary<string, string> newDealer = new();
    /// <summary>Promotion settings being edited, per venue, until they're saved.</summary>
    private readonly Dictionary<string, PromoEdit> promoEdits = new();

    /// <summary>Colours being picked, per venue, until they're saved.</summary>
    private readonly Dictionary<string, (Vector3 Felt, Vector3 Accent)> colours = new();

    /// <summary>Card looks being picked, per venue, until they're saved.</summary>
    private readonly Dictionary<string, CardEdit> cardEdits = new();

    /// <summary>Tournaments, for a venue with the Tournament License.</summary>
    private readonly TournamentsPanel tournaments;

    public VenueTab(Plugin plugin)
    {
        this.plugin = plugin;
        tournaments = new TournamentsPanel(plugin);
    }

    private static string BaseUrl => WebPublisher.ServerUrl.TrimEnd('/');

    public void Draw()
    {
        fileDialog.Draw();
        var auth = plugin.Auth;
        // Each venue is its own section, so a dealer in several venues isn't scrolling past all of them.
        // With only one, it starts open.
        var onlyOne = auth.Venues.Count == 1;
        foreach (var venue in auth.Venues)
        {
            if (venue.IsOwner)
                continue;
            if (!VenueHeader($"{venue.Name}   (you deal here)###deal{venue.Id}", onlyOne))
                continue;
            ImGui.Indent();
            ImGui.PushID(venue.Id); // each venue's Open and Copy buttons are its own
            ImGui.TextWrapped($"You deal for {venue.Name}. Its owner, {venue.OwnerName}, manages its dealers. " +
                              "Your table on the website shows the venue's name, logo and colours, and is listed on its page.");
            DrawPageLink(venue.Page);
            ImGui.PopID();
            ImGui.Unindent();
            ImGui.Spacing();
        }

        if (!auth.Venues.Any(v => v.IsOwner))
        {
            if (auth.HasIndividualLicense)
                ImGui.TextColored(DealerControls.Muted, "You also have your own individual license.");
            return;
        }

        if (!loaded && !busy)
            Load();

        ImGui.TextWrapped("You own a venue license. Add the dealers who deal for your venue: they can sign in to the plugin " +
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
        {
            // "###" keeps the section open or closed while the seat count in its title changes.
            if (!VenueHeader($"{venue.Name}   (you own it, {venue.Used} of {venue.Seats} dealer seats used)###own{venue.Id}", onlyOne))
                continue;
            ImGui.Indent();
            DrawOwned(venue);
            ImGui.Unindent();
            ImGui.Spacing();
        }
    }

    private static bool VenueHeader(string label, bool open)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DealerControls.Gold);
        var shown = ImGui.CollapsingHeader(label, open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        ImGui.PopStyleColor();
        return shown;
    }

    private void DrawOwned(OwnedVenue venue)
    {
        ImGui.PushID(venue.Id);

        // With the bad beat jackpot on, the owner picks whose tables play for it (and so take its cut).
        var jackpot = venue.Promotions is { BadOn: true };
        if (ImGui.BeginTable("##dealers", jackpot ? 3 : 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("dealer");
            if (jackpot)
                ImGui.TableSetupColumn("jackpot", ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableSetupColumn("remove", ImGuiTableColumnFlags.WidthFixed, 90);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{venue.OwnerName}  (owner)");
            if (jackpot)
            {
                ImGui.TableNextColumn();
                DrawJackpotToggle(venue, "owner", venue.OwnerName, venue.OwnerJackpot);
            }

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

                if (jackpot)
                {
                    ImGui.TableNextColumn();
                    DrawJackpotToggle(venue, dealer.Key, dealer.Label, dealer.Jackpot);
                }

                ImGui.TableNextColumn();
                ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl);
                if (ImGui.SmallButton("Remove"))
                    Send(HttpMethod.Delete, $"/api/venue/{venue.Id}/dealers/{Uri.EscapeDataString(dealer.Key)}", null,
                         $"Removed {dealer.Label}.");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Hold Ctrl and click. They're signed out unless they have another license.");
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
            ImGui.TextColored(DealerControls.Muted, "All seats are taken. Remove a dealer, or ask Elenor for more seats (Discord: elenoramdali).");

        ImGui.Spacing();
        ImGui.TextColored(DealerControls.Gold, "Venue page");
        ImGui.SameLine();
        DrawPageLink(venue.Page);
        ImGui.TextColored(DealerControls.Muted, "Lists your venue's open tables, so players can find them. Post it once on your Discord or Carrd.");

        ImGui.Spacing();
        DrawLogo(venue);
        ImGui.Spacing();
        DrawColours(venue);
        ImGui.Spacing();
        DrawCards(venue);
        ImGui.Spacing();
        DrawPromotions(venue);
        if (venue.TournamentLicense)
        {
            ImGui.Spacing();
            tournaments.Draw(venue.Id);
        }

        ImGui.PopID();
    }

    private void DrawJackpotToggle(OwnedVenue venue, string key, string label, bool plays)
    {
        ImGui.BeginDisabled(busy);
        if (ImGui.Checkbox("Bad beat jackpot##jp", ref plays))
        {
            var body = new StringContent(JsonSerializer.Serialize(new { key, on = plays }), Encoding.UTF8, "application/json");
            Send(HttpMethod.Post, $"/api/venue/{venue.Id}/jackpot", body,
                 plays ? $"{label}'s table plays for the bad beat jackpot from its next hand."
                       : $"{label}'s table is out of the bad beat jackpot from its next hand: no cut, and it can't hit it.");
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("On: this dealer's table takes the jackpot cut and can hit the jackpot.\n" +
                             "Off: no cut from its pots, it can't hit the jackpot, and its table page doesn't show it.");
    }

    private void DrawPromotions(OwnedVenue venue)
    {
        if (venue.Promotions is not { } saved)
            return; // an older server
        if (!promoEdits.TryGetValue(venue.Id, out var e))
            promoEdits[venue.Id] = e = PromoEdit.From(saved);

        ImGui.TextColored(DealerControls.Gold, "Promotions");
        ImGui.SameLine();
        ImGui.TextColored(DealerControls.Muted, "Shared by all your venue's tables. Each can be turned off.");
        ImGui.BeginDisabled(busy);

        ImGui.Checkbox("High hand bonus", ref e.HighOn);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The best hand shown at showdown (using both hole cards) since you started the round wins your prize.\n" +
                             "The plugin announces each new high hand; the venue pays the prize.");
        if (e.HighOn)
        {
            ImGui.Indent();
            Combo("Minimum hand##hh", saved.HighHandMinimums, ref e.HighMin);
            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("Prize##hh", "e.g. 500,000 gil", ref e.Prize, 60);
            ImGui.TextUnformatted(saved.BestPlayer is null ? "No high hand yet this round." : $"Best: {saved.BestPlayer}, {saved.BestHand}");
            ImGui.SameLine();
            ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("New round"))
                Send(HttpMethod.Post, $"/api/venue/{venue.Id}/promotions/reset-high-hand", null, "New high hand round started.");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click, e.g. at the start of each event night.");
            ImGui.Unindent();
        }

        ImGui.Checkbox("Bad beat jackpot", ref e.BadOn);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each hand that sees a flop puts a cut into the jackpot. When a big enough hand (using both hole cards)\n" +
                             "loses the main pot at showdown, the plugin pays the jackpot to the loser, the winner and the table.");
        if (e.BadOn)
        {
            ImGui.Indent();
            Combo("Losing hand must be at least##bb", saved.BadBeatQualifiers, ref e.Qualifier);
            ImGui.SetNextItemWidth(120);
            ImGui.InputFloat("Cut per pot (%)##bb", ref e.DropPercent, 0.25f, 1f, "%.2f");
            ImGui.SetNextItemWidth(120);
            DealerControls.InputLong("Cut cap per hand (0 = no cap)##bb", ref e.DropCap);
            ImGui.Checkbox("Take the cut from the rake (the house pays it)##bb", ref e.FromRake);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off: the cut comes out of the pot on top of the rake, so the players pay it.");
            ImGui.TextUnformatted("Split (%):");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputInt("loser##bb", ref e.Loser, 0, 0);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputInt("winner##bb", ref e.Winner, 0, 0);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputInt("rest of the table##bb", ref e.TableShare, 0, 0);
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Kept to start the next jackpot (%)##bb", ref e.Seed, 0, 0);
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("Players dealt in, at least##bb", ref e.MinPlayers, 0, 0);

            ImGui.TextColored(DealerControls.Gold, $"Jackpot now: {saved.Balance:N0}");
            if (saved.LastHit is { } hit)
            {
                ImGui.SameLine();
                ImGui.TextColored(DealerControls.Muted, $"(last hit: {hit})");
            }

            ImGui.SetNextItemWidth(140);
            DealerControls.InputLong("##newbalance", ref e.NewBalance);
            ImGui.SameLine();
            ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("Set jackpot"))
            {
                var body = new StringContent(JsonSerializer.Serialize(new { badBeat = new { balance = e.NewBalance } }), Encoding.UTF8, "application/json");
                Send(HttpMethod.Post, $"/api/venue/{venue.Id}/promotions", body, $"Jackpot set to {e.NewBalance:N0}.");
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click: sets the jackpot to this amount, e.g. to start it with gil the venue adds.");
            ImGui.Unindent();
        }

        if (ImGui.Button("Save promotions"))
        {
            var body = new StringContent(JsonSerializer.Serialize(new
            {
                highHand = new { enabled = e.HighOn, min = e.HighMin, prize = e.Prize.Trim() },
                badBeat = new
                {
                    enabled = e.BadOn,
                    qualifier = e.Qualifier,
                    dropPercent = Math.Round(e.DropPercent, 2),
                    dropCap = e.DropCap,
                    fromRake = e.FromRake,
                    split = new { loser = e.Loser, winner = e.Winner, table = e.TableShare },
                    seedPercent = e.Seed,
                    minPlayers = e.MinPlayers,
                },
            }), Encoding.UTF8, "application/json");
            Send(HttpMethod.Post, $"/api/venue/{venue.Id}/promotions", body, "Promotions saved. Your dealers' tables use them from their next hand.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Undo changes"))
            promoEdits.Remove(venue.Id);
        ImGui.EndDisabled();
    }

    private static void Combo(string label, List<(string Id, string Text)> choices, ref string value)
    {
        var current = value;
        var shown = choices.Find(c => c.Id == current).Text ?? value;
        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo(label, shown))
            return;
        foreach (var (id, text) in choices)
        {
            if (ImGui.Selectable(text, id == value))
                value = id;
        }

        ImGui.EndCombo();
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

    private void DrawCards(OwnedVenue venue)
    {
        if (!venue.CardsSupported)
            return; // an older server
        if (!cardEdits.TryGetValue(venue.Id, out var e))
            cardEdits[venue.Id] = e = CardEdit.From(venue.Cards);

        ImGui.TextColored(DealerControls.Gold, "Cards");
        ImGui.SameLine();
        ImGui.TextUnformatted(venue.Cards is null ? "The Eorzean Poker cards." : "Your own, on your tables.");
        ImGui.BeginDisabled(busy);
        Combo("Face##cards", Faces, ref e.Face);
        Combo("Corners##cards", Corners, ref e.Corners);
        Combo("Font##cards", Fonts, ref e.Font);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("For the ranks (A, K, 10...). Standard is the site's own font, Classic serif looks like printed cards,\n" +
                             "Typewriter is an old typewriter font.");
        Combo("Back##cards", Backs, ref e.Back);

        var flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.DisplayHex;
        ImGui.ColorEdit3(e.Back == "stripes" ? "##back1" : e.Back == "image" ? "Colour around the picture##back1" : "Back colour##back1",
                         ref e.Colour1, flags);
        if (e.Back == "stripes")
        {
            ImGui.SameLine();
            ImGui.ColorEdit3("Stripe colours##back2", ref e.Colour2, flags);
        }

        if (e.Back == "logo" && venue.Logo is null)
            ImGui.TextColored(DealerControls.Gold, "Upload a logo first (above). Until it's approved, the back shows the stripes.");
        if (e.Back == "image")
            DrawCardBackPicture(venue);

        ImGui.BeginDisabled(e.Matches(venue.Cards));
        if (ImGui.Button("Save cards"))
        {
            var body = new StringContent(JsonSerializer.Serialize(new
            {
                face = e.Face, corners = e.Corners, font = e.Font, back = e.Back, backColours = new[] { ToHex(e.Colour1), ToHex(e.Colour2) },
            }), Encoding.UTF8, "application/json");
            Send(HttpMethod.Post, $"/api/venue/{venue.Id}/cards", body, "Cards saved. Open tables change straight away.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Undo##cards"))
            cardEdits.Remove(venue.Id);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(venue.Cards is null);
        if (ImGui.Button("Use house cards"))
        {
            cardEdits.Remove(venue.Id);
            Send(HttpMethod.Post, $"/api/venue/{venue.Id}/cards", new StringContent("null", Encoding.UTF8, "application/json"),
                 "Back to the Eorzean Poker cards.");
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.TextColored(DealerControls.Muted, "Only on the website: game chat can't show them. The faces keep black and red suits, so every card stays easy to read.");
    }

    private void DrawCardBackPicture(OwnedVenue venue)
    {
        ImGui.Indent();
        ImGui.TextUnformatted(venue.CardBack is null ? "No picture yet." : "Your picture is on your tables.");
        if (venue.CardBack is { } back)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("View##back"))
                Util.OpenLink(BaseUrl + back);
            ImGui.SameLine();
            ImGui.BeginDisabled(busy || !ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("Remove picture"))
                Send(HttpMethod.Delete, $"/api/venue/{venue.Id}/card-back?which=live", null, "Card back picture removed.");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click. The back shows the stripes until you upload another.");
        }

        if (venue.CardBackPending)
        {
            ImGui.TextColored(DealerControls.Gold, "Your new picture is waiting for Elenor's approval.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Withdraw##back"))
                Send(HttpMethod.Delete, $"/api/venue/{venue.Id}/card-back?which=pending", null, "Upload withdrawn.");
        }

        if (ImGui.Button(venue.CardBackPending ? "Upload a different picture..." : "Upload picture..."))
        {
            var id = venue.Id;
            fileDialog.OpenFileDialog("Choose your card back", "Images{.png,.jpg,.jpeg,.webp}", (ok, file) =>
            {
                if (ok)
                    UploadImage(id, file, "card-back", "Card back uploaded. It shows on your tables once Elenor approves it.");
            });
        }

        ImGui.TextColored(DealerControls.Muted, "PNG, JPEG or WebP under 256 KB, upright (about 5 wide by 7 tall). It fills the back, " +
                                                "so keep it simple: cards are small on phones. Elenor checks each new picture before it shows.");
        ImGui.Unindent();
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
                    UploadImage(id, file, "logo", "Logo uploaded. It shows on your tables once Elenor approves it.");
            });
        }

        ImGui.EndDisabled();
        ImGui.TextColored(DealerControls.Muted, "PNG, JPEG or WebP under 256 KB, ideally square with a transparent background. " +
                                                "Elenor checks each new logo before it shows.");
    }

    /// <summary>Uploads the venue's logo ("logo") or card back ("card-back"), which waits for Elenor's approval.</summary>
    private void UploadImage(string venueId, string file, string kind, string success)
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
            ShowMessage($"That image is {data.Length / 1024} KB; it must be under 256 KB.", true);
            return;
        }

        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Send(HttpMethod.Post, $"/api/venue/{venueId}/{kind}", content, success);
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
                    {
                        colours.Remove(venue.Id);
                        cardEdits.Remove(venue.Id);
                        promoEdits.Remove(venue.Id);
                    }
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
                d.GetProperty("pending").GetBoolean(),
                !d.TryGetProperty("jackpot", out var plays) || plays.ValueKind != JsonValueKind.False));
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
            ColourOf(theme, "accent"),
            v.TryGetProperty("promotions", out var promo) && promo.ValueKind == JsonValueKind.Object ? OwnerPromotions.Parse(promo) : null,
            !v.TryGetProperty("ownerJackpot", out var ownerPlays) || ownerPlays.ValueKind != JsonValueKind.False,
            v.TryGetProperty("cards", out var cards),
            cards.ValueKind == JsonValueKind.Object ? CardStyle.Parse(cards) : null,
            v.TryGetProperty("cardBack", out var cardBack) && cardBack.ValueKind == JsonValueKind.String ? cardBack.GetString() : null,
            v.TryGetProperty("cardBackPending", out var backPending) && backPending.ValueKind == JsonValueKind.True,
            v.TryGetProperty("tournamentLicense", out var tournament) && tournament.ValueKind == JsonValueKind.True);
    }

    private sealed record OwnedVenue(
        string Id, string Name, int Seats, int Used, string OwnerName, List<VenueDealer> Dealers, string? Logo, bool LogoPending,
        string Page, Vector3? Felt, Vector3? Accent, OwnerPromotions? Promotions, bool OwnerJackpot,
        bool CardsSupported, CardStyle? Cards, string? CardBack, bool CardBackPending, bool TournamentLicense);

    /// <summary>How the venue's cards look on the website, as saved.</summary>
    private sealed record CardStyle(string Face, string Corners, string Font, string Back, Vector3 Colour1, Vector3 Colour2)
    {
        public static CardStyle Parse(JsonElement c)
        {
            var colours = c.TryGetProperty("backColours", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                : [];
            Vector3 Colour(int i, Vector3 house) => i < colours.Count && colours[i].Length == 7 ? FromHex(colours[i]) : house;
            string Text(string name, string house) =>
                c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? house : house;
            return new CardStyle(Text("face", "cream"), Text("corners", "classic"), Text("font", "standard"), Text("back", "stripes"),
                                 Colour(0, HouseBack), Colour(1, HouseBack2));
        }
    }

    /// <summary>The owner's unsaved card looks.</summary>
    private sealed class CardEdit
    {
        public string Face = "cream";
        public string Corners = "classic";
        public string Font = "standard";
        public string Back = "stripes";
        public Vector3 Colour1 = HouseBack;
        public Vector3 Colour2 = HouseBack2;

        public static CardEdit From(CardStyle? c) => c is null
            ? new CardEdit()
            : new CardEdit { Face = c.Face, Corners = c.Corners, Font = c.Font, Back = c.Back, Colour1 = c.Colour1, Colour2 = c.Colour2 };

        /// <summary>Nothing to save: the same as what's saved (or the house cards, if none are).</summary>
        public bool Matches(CardStyle? saved)
        {
            var s = From(saved);
            return Face == s.Face && Corners == s.Corners && Font == s.Font && Back == s.Back &&
                   ToHex(Colour1) == ToHex(s.Colour1) && ToHex(Colour2) == ToHex(s.Colour2);
        }
    }

    /// <summary>The venue's promotion settings as the owner sees them, with the choices for the lists.</summary>
    private sealed record OwnerPromotions(
        bool HighOn, string HighMin, string Prize, string? BestPlayer, string? BestHand,
        bool BadOn, string Qualifier, float DropPercent, long DropCap, bool FromRake, int Loser, int Winner, int TableShare,
        int Seed, int MinPlayers, long Balance, string? LastHit,
        List<(string Id, string Text)> HighHandMinimums, List<(string Id, string Text)> BadBeatQualifiers)
    {
        public static OwnerPromotions Parse(JsonElement p)
        {
            var hh = p.GetProperty("highHand");
            var bb = p.GetProperty("badBeat");
            var split = bb.GetProperty("split");
            var best = hh.TryGetProperty("best", out var b) && b.ValueKind == JsonValueKind.Object ? b : (JsonElement?)null;
            var hit = bb.TryGetProperty("lastHit", out var h) && h.ValueKind == JsonValueKind.Object
                ? $"{Text(h, "loser")}'s {Text(h, "loserHand")}, {h.GetProperty("amount").GetInt64():N0}"
                : null;
            return new OwnerPromotions(
                hh.GetProperty("enabled").GetBoolean(), Text(hh, "min"), Text(hh, "prize"),
                best is { } x ? Text(x, "player") : null, best is { } y ? Text(y, "hand") : null,
                bb.GetProperty("enabled").GetBoolean(), Text(bb, "qualifier"), (float)bb.GetProperty("dropPercent").GetDouble(),
                bb.GetProperty("dropCap").GetInt64(), bb.GetProperty("fromRake").GetBoolean(),
                split.GetProperty("loser").GetInt32(), split.GetProperty("winner").GetInt32(), split.GetProperty("table").GetInt32(),
                bb.GetProperty("seedPercent").GetInt32(), bb.GetProperty("minPlayers").GetInt32(), bb.GetProperty("balance").GetInt64(), hit,
                Choices(p, "highHandMinimums"), Choices(p, "badBeatQualifiers"));
        }

        private static List<(string Id, string Text)> Choices(JsonElement p, string name)
        {
            var list = new List<(string, string)>();
            if (p.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in array.EnumerateArray())
                    list.Add((Text(c, "id"), Text(c, "text")));
            }

            return list;
        }

        private static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>The owner's unsaved promotion settings.</summary>
    private sealed class PromoEdit
    {
        public bool HighOn;
        public string HighMin = "full-house";
        public string Prize = string.Empty;
        public bool BadOn;
        public string Qualifier = "quads-8";
        public float DropPercent;
        public long DropCap;
        public bool FromRake;
        public int Loser;
        public int Winner;
        public int TableShare;
        public int Seed;
        public int MinPlayers;
        public long NewBalance;

        public static PromoEdit From(OwnerPromotions p) => new()
        {
            HighOn = p.HighOn, HighMin = p.HighMin, Prize = p.Prize, BadOn = p.BadOn, Qualifier = p.Qualifier,
            DropPercent = p.DropPercent, DropCap = p.DropCap, FromRake = p.FromRake, Loser = p.Loser, Winner = p.Winner,
            TableShare = p.TableShare, Seed = p.Seed, MinPlayers = p.MinPlayers, NewBalance = p.Balance,
        };
    }

    private sealed record VenueDealer(string Key, string Label, bool Pending, bool Jackpot);
}
