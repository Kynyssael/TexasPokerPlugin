using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TexasPoker.Windows;

/// <summary>
/// The terms of use, shown in the main window before anything else until the dealer accepts them.
/// Raise <see cref="Version"/> when the terms change, so everyone is asked again.
/// </summary>
public sealed class TermsPanel
{
    public const int Version = 2;

    private static readonly (string Heading, string[] Lines)[] Sections =
    [
        ("What this is",
        [
            "Eorzean Poker is a dealer tool for running Texas Hold'em at FFXIV venues. Chips are in-game gil only.",
            "It is a fan project. It isn't made, endorsed or supported by Square Enix.",
        ]),
        ("Third-party tools",
        [
            "Dalamud plugins are third-party tools, which Square Enix's User Agreement doesn't allow. You use this plugin at your own risk, including any action taken against your account.",
        ]),
        ("Your license",
        [
            "Dealing needs a license: one for your character from Elenor, or a dealer seat from a venue that has one.",
            "A license is for the character it was given to. Don't share your sign-in, sell or pass on access, or try to get around the sign-in.",
            "Don't copy, change or redistribute the plugin.",
            "A license can be taken away at any time, for example for breaking these terms.",
        ]),
        ("No real money",
        [
            "Don't use this plugin, or the website, for games played for real money, or to buy or sell gil for real money (RMT).",
        ]),
        ("Deal fairly",
        [
            "Deal every hand straight. Never use what the plugin shows you (for example \"Show all hole cards\") to help or hurt a player.",
            "Players trust you with their gil: pay out what the table says and keep the ledger honest.",
        ]),
        ("Your data",
        [
            "Signing in sends your character's name, world and Lodestone ID (through XIVAuth) to the Eorzean Poker server.",
            "When the web table is on, the table (players' names, stacks, bets and the board) is sent to eorzeanpoker.app, where anyone with the link can watch. Hole cards are encrypted so that only each player's private link, or their one-time seat PIN, can open them.",
            "A seat PIN works once, for 10 minutes, and only for the seat it was given to. After 5 wrong tries the seat's PIN stops working and you are told in chat.",
            "The plugin keeps a log of each whole session on your PC (hands, gil moves, and players' chat and tell actions word for word). It stays on your PC unless you export or share it.",
        ]),
        ("No guarantees",
        [
            "The plugin and website are provided as they are, without any warranty. They may have bugs, go offline or change.",
            "Elenor isn't responsible for lost gil, disputes between dealers and players, or problems with your game account.",
        ]),
        ("Changes",
        [
            "These terms may change. When they do, the plugin asks you to read and accept them again.",
        ]),
    ];

    private readonly Plugin plugin;
    private bool agree;

    public TermsPanel(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.SetWindowFontScale(1.4f);
        ImGui.TextColored(DealerControls.Gold, "Terms of use");
        ImGui.SetWindowFontScale(1f);
        ImGui.TextWrapped(plugin.Configuration.AcceptedTermsVersion > 0
            ? "The terms have changed since you last accepted them. Please read them again before you continue."
            : "Please read these terms before you use Eorzean Poker.");
        ImGui.Spacing();

        // The terms scroll; the checkbox and buttons stay below them.
        var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2;
        if (ImGui.BeginChild("##terms", new Vector2(0, -footer), true))
        {
            foreach (var (heading, lines) in Sections)
            {
                ImGui.TextColored(DealerControls.Gold, heading);
                foreach (var line in lines)
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextWrapped(line);
                }

                ImGui.Spacing();
            }
        }

        ImGui.EndChild();

        ImGui.Checkbox("I have read and agree to these terms", ref agree);
        ImGui.BeginDisabled(!agree);
        if (ImGui.Button("Accept and continue"))
        {
            plugin.Configuration.AcceptedTermsVersion = Version;
            plugin.Save();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Not now"))
            plugin.CloseMainWindow();
    }
}
