using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

/// <summary>The main window's Web tab: turns on the live web table and hands out its links.</summary>
public sealed class WebTab
{
    private readonly Plugin plugin;
    private string restoreCode = string.Empty;

    public WebTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private Configuration Config => plugin.Configuration;

    private WebPublisher Web => plugin.Web;

    public void Draw()
    {
        DrawAccount();
        ImGui.Separator();

        ImGui.TextWrapped("Players can watch your table live in a browser. The table link shows seats, stacks, bets " +
                          "and the board, but no hole cards. Each player's private link also shows their own two cards. " +
                          "Your table link is permanent: share it once and it keeps working.");
        ImGui.Spacing();

        var enabled = Config.WebEnabled;
        if (ImGui.Checkbox("Show my table on the web", ref enabled))
        {
            Config.WebEnabled = enabled;
            plugin.Save();
        }

        if (!Config.WebEnabled)
            return;

        ImGui.TextColored(Web.LastSendFailed ? DealerControls.Red : DealerControls.Good, Web.Status);
        ImGui.SameLine();
        if (ImGui.SmallButton("Send now"))
            Web.Push();

        DrawShowAs();
        ImGui.Separator();
        DrawTableLink();
        ImGui.Spacing();
        DrawPrivateLinks();
        ImGui.Spacing();
        DrawBackup();
    }

    private void DrawAccount()
    {
        ImGui.TextColored(DealerControls.Gold, "Account");
        ImGui.SameLine();
        ImGui.TextUnformatted(plugin.Auth.SignedIn
            ? $"Signed in with XIVAuth as {plugin.Auth.SignedInAs}"
            : "Not signed in");
        ImGui.SameLine();
        if (ImGui.SmallButton("Sign out"))
            ImGui.OpenPopup("signout");

        if (ImGui.BeginPopup("signout"))
        {
            ImGui.TextUnformatted("Sign out of XIVAuth?");
            ImGui.TextColored(DealerControls.Muted, plugin.Table.HandInProgress
                ? "You can finish this hand; the plugin locks after it until you sign in again."
                : "The plugin locks until you sign in again.");
            if (ImGui.Button("Sign out##confirm"))
            {
                plugin.Auth.SignOut();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##signout"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// For dealers with more than one licence (their own and a venue's, or several venues): which one the
    /// table is shown under on the website. The server only honours a licence they really hold.
    /// </summary>
    private void DrawShowAs()
    {
        var auth = plugin.Auth;
        var options = new List<(string Value, string Label)>();
        if (auth.HasIndividualLicence)
            options.Add(("own", "My own table (no venue)"));
        foreach (var venue in auth.Venues)
            options.Add((venue.Id, venue.Name));
        if (options.Count < 2)
            return;

        // Empty means the server's default: the venue you own, else the first that added you.
        var current = options.FindIndex(o => o.Value == Config.WebShowAs);
        if (current < 0)
            current = options.FindIndex(o => o.Value != "own");

        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Show my table as", options[current].Label))
        {
            for (var i = 0; i < options.Count; i++)
            {
                if (ImGui.Selectable(options[i].Label, i == current) && i != current)
                {
                    Config.WebShowAs = options[i].Value;
                    plugin.Save();
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A venue shows its name and logo on your table page. Your own table shows only Eorzean Poker.");
    }

    private void DrawTableLink()
    {
        ImGui.TextColored(DealerControls.Gold, "Table link (anyone can watch)");
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextUnformatted(Web.TableUrl);
        ImGui.SetWindowFontScale(1f);
        if (ImGui.Button("Copy##table"))
            ImGui.SetClipboardText(Web.TableUrl);
        ImGui.SameLine();
        if (ImGui.Button($"Post in {Config.Channel}"))
            plugin.Chat.SayToTable([$"Watch the table live: {Web.TableUrl}"]);
    }

    private void DrawPrivateLinks()
    {
        ImGui.TextColored(DealerControls.Gold, "Private links (show that player's cards)");
        if (plugin.Table.Seats.Count == 0)
        {
            ImGui.TextColored(DealerControls.Muted, "Seat players to make their links.");
            return;
        }

        if (ImGui.Button("Tell every player their private link"))
            plugin.TellWebLinks();

        ImGui.SameLine();
        ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
        if (ImGui.Button("New links for everyone"))
        {
            Config.WebTokens.Clear();
            plugin.Save();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click. Every private link stops working; tell players their new ones.\n" +
                             "Good to do at the start of an event. A player's link also ends when they cash out.");

        ImGui.TextColored(DealerControls.Muted, "Game chat links can't be clicked: players type them in, or copy them with a chat plugin.");
        ImGui.TextColored(DealerControls.Muted, "Each player's cards are encrypted for their link only: not even the server can read them.");

        var webActions = Config.WebActions;
        if (ImGui.Checkbox("Players can act from their private page", ref webActions))
        {
            Config.WebActions = webActions;
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On their turn, a player's private page shows Fold / Check / Call / Raise buttons.\n" +
                             "Actions are signed with the player's link, so nobody else can send one for them.");

        ImGui.BeginDisabled(!Config.WebActions);
        ImGui.Indent();
        var auto = Config.WebActionsAuto;
        if (ImGui.Checkbox("Apply web actions automatically", ref auto))
        {
            Config.WebActionsAuto = auto;
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: a web action is only picked, and you press Confirm, like an action typed in chat.");
        ImGui.Unindent();
        ImGui.EndDisabled();

        if (!ImGui.BeginTable("##links", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("player", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("link");
        ImGui.TableSetupColumn("buttons", ImGuiTableColumnFlags.WidthFixed, 170);
        foreach (var seat in plugin.Table.Seats)
        {
            ImGui.PushID(seat.DisplayName);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(seat.Name);
            ImGui.TableNextColumn();
            var link = Web.PlayerUrl(seat);
            ImGui.TextColored(DealerControls.Muted, link);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText(link);
            ImGui.SameLine();
            if (ImGui.SmallButton("Tell"))
            {
                plugin.TellWebLink(seat);
                plugin.Save();
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl);
            if (ImGui.SmallButton("New link"))
                ResetLink(seat);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click if this player's link leaked. Only their old private link stops working.");

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void ResetLink(Seat seat)
    {
        Config.WebTokens.Remove(seat.DisplayName.ToLowerInvariant());
        plugin.Save();
    }

    private void DrawBackup()
    {
        if (!ImGui.CollapsingHeader("Move my table to another PC"))
            return;

        ImGui.TextWrapped("Your table belongs to this copy of the plugin. To keep the same link after reinstalling or " +
                          "on another PC, copy this backup code and restore it there. Keep it secret: anyone with it can run your table.");
        if (ImGui.Button("Copy backup code"))
            ImGui.SetClipboardText(Web.BackupCode);

        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("##restore", "paste a backup code", ref restoreCode, 120, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        ImGui.BeginDisabled(restoreCode.Trim().Length == 0 || !ImGui.GetIO().KeyCtrl);
        if (ImGui.Button("Restore"))
        {
            if (Web.Restore(restoreCode))
            {
                restoreCode = string.Empty;
                plugin.Save();
            }
            else
            {
                plugin.Chat.PrintLocal("That backup code isn't valid.");
            }
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click. This copy then uses the restored table and link instead of its own.");
    }
}
