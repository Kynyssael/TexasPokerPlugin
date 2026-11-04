using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TexasPoker.Windows;

/// <summary>A small window with just the hand summary and action buttons, for dealing with the main window closed.</summary>
public sealed class DealerBarWindow : Window
{
    private readonly Plugin plugin;
    private readonly DealerControls controls;

    public DealerBarWindow(Plugin plugin, DealerControls controls)
        : base("Poker Dealer Bar###TexasPokerBar", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        this.controls = controls;
        IsOpen = plugin.Configuration.ShowDealerBar;
    }

    public override void Draw()
    {
        if (!plugin.Auth.Allowed)
        {
            ImGui.TextColored(DealerControls.Muted, plugin.TermsAccepted ? "Sign in with XIVAuth to deal." : "Accept the terms of use to deal.");
            if (ImGui.Button(plugin.TermsAccepted ? "Open sign-in" : "Open the terms"))
                plugin.OpenMainWindow();
            return;
        }

        controls.DrawError();
        controls.DrawHandSummary(compact: true);
        ImGui.Separator();

        if (plugin.Table.HandInProgress)
            controls.DrawActionPanel(compact: true);
        else
            controls.DrawBetweenHands(compact: true);

        if (plugin.Chat.Pending > 0)
            ImGui.TextColored(DealerControls.Gold, $"{plugin.Chat.Pending} chat message(s) queued");

        if (ImGui.SmallButton("Open full table"))
            plugin.OpenMainWindow();
    }

    public override void OnOpen() => Remember(true);

    public override void OnClose() => Remember(false);

    private void Remember(bool open)
    {
        if (plugin.Configuration.ShowDealerBar == open)
            return;
        plugin.Configuration.ShowDealerBar = open;
        plugin.Save();
    }
}
