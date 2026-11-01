using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

/// <summary>The main window's Messages tab: the dealer edits the wording of every table announcement.</summary>
public sealed class MessagesTab
{
    private readonly Plugin plugin;

    public MessagesTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private MessageTemplates Messages => plugin.Table.Messages;

    private Configuration Config => plugin.Configuration;

    public void Draw()
    {
        ImGui.TextWrapped($"Edit what the dealer says in chat. Each line is its own message in {Config.Channel}. " +
                          "Start a line with / to send it exactly as typed: an emote (/em shuffles the deck), " +
                          "or another channel (/p, /s). Leave a message empty to say nothing for that event.");
        ImGui.TextColored(DealerControls.Muted, "Words in {braces} are filled in from the table. Hover them to see what they mean.");

        ImGui.BeginDisabled(!ImGui.GetIO().KeyCtrl || Messages.Overrides.Count == 0);
        if (ImGui.Button("Reset all to default"))
        {
            Messages.ResetAll();
            plugin.Save();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl and click. Puts every message back to the built-in wording.");

        ImGui.Separator();
        if (!ImGui.BeginChild("##messages"))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var group in MessageTemplates.All.GroupBy(m => m.Group))
        {
            var edited = group.Count(m => Messages.IsCustom(m.Kind));
            var header = edited > 0 ? $"{group.Key} ({edited} edited)###{group.Key}" : $"{group.Key}###{group.Key}";
            if (!ImGui.CollapsingHeader(header))
                continue;

            foreach (var info in group)
                DrawMessage(info);
        }

        ImGui.EndChild();
    }

    private void DrawMessage(MessageInfo info)
    {
        ImGui.PushID(info.Kind.ToString());
        ImGui.Spacing();

        var custom = Messages.IsCustom(info.Kind);
        ImGui.TextColored(custom ? DealerControls.Gold : DealerControls.Good, info.Title + (custom ? "  (edited)" : string.Empty));
        if (custom)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"))
            {
                Messages.Reset(info.Kind);
                plugin.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Back to:\n{info.Default}");
        }

        var text = Messages.Get(info.Kind);
        var lines = Math.Max(2, text.Count(c => c == '\n') + 2);
        var height = (ImGui.GetTextLineHeight() * lines) + (ImGui.GetStyle().FramePadding.Y * 2);
        if (ImGui.InputTextMultiline("##template", ref text, 1000, new Vector2(-1, height)))
            Messages.Set(info.Kind, text);
        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.Save();

        ImGui.TextColored(DealerControls.Muted, "Use:");
        foreach (var key in info.Placeholders)
        {
            ImGui.SameLine();
            ImGui.TextColored(DealerControls.Muted, "{" + key + "}");
            if (ImGui.IsItemHovered() && MessageTemplates.PlaceholderHelp.TryGetValue(key, out var help))
                ImGui.SetTooltip(help);
            if (ImGui.IsItemClicked())
                ImGui.SetClipboardText("{" + key + "}");
        }

        var preview = MessageTemplates.Fill(text, SampleValues(info.Kind));
        if (preview.Count == 0)
        {
            ImGui.TextColored(DealerControls.Muted, "Preview: (nothing is said)");
        }
        else
        {
            foreach (var line in preview)
                ImGui.TextColored(DealerControls.Muted, "  " + (line.StartsWith('/') ? line : $"{Config.Channel} {line}"));
        }

        ImGui.PopID();
    }

    /// <summary>Made-up values so the dealer can see how a message will read.</summary>
    private Dictionary<string, string> SampleValues(MessageKind kind)
    {
        string Cards(string text) => Card.Join(Card.ParseMany(text), Config.ChatSuitSymbols);

        var values = new Dictionary<string, string>
        {
            ["player"] = "Y'shtola",
            ["amount"] = "3,000",
            ["allin"] = string.Empty,
            ["stack"] = "47,000",
            ["pot"] = "12,500",
            ["hand"] = "12",
            ["sb"] = plugin.Table.SmallBlind.ToString("N0"),
            ["bb"] = plugin.Table.BigBlind.ToString("N0"),
            ["button"] = "Thancred",
            ["players"] = "4",
            ["pot_name"] = "Main pot",
            ["rake"] = string.Empty,
        };

        switch (kind)
        {
            case MessageKind.SmallBlind:
                values["amount"] = plugin.Table.SmallBlind.ToString("N0");
                break;
            case MessageKind.BigBlind:
                values["amount"] = plugin.Table.BigBlind.ToString("N0");
                break;
            case MessageKind.Flop:
                values["street"] = "Flop";
                values["board"] = values["cards"] = Cards("As Th 7c");
                break;
            case MessageKind.Turn:
                values["street"] = "Turn";
                values["board"] = Cards("As Th 7c Kd");
                values["cards"] = Cards("Kd");
                break;
            case MessageKind.River:
                values["street"] = "River";
                values["board"] = Cards("As Th 7c Kd 2s");
                values["cards"] = Cards("2s");
                break;
            case MessageKind.Shows:
                values["cards"] = Cards("Ah Qd");
                values["hand"] = "Pair of Aces";
                break;
            case MessageKind.PotWon:
                values["amount"] = "24,000";
                values["hand"] = "Pair of Aces, Queen kicker";
                break;
            case MessageKind.PotSplit:
                values["amount"] = "24,000";
                values["player"] = "Y'shtola, Thancred";
                values["hand"] = "Straight";
                break;
        }

        return values;
    }
}
