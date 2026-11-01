using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

internal static class CardUi
{
    public static readonly Vector4 Red = new(0.95f, 0.35f, 0.35f, 1f);

    /// <summary>Draws cards on one line, hearts and diamonds in red.</summary>
    public static void Draw(IReadOnlyList<Card> cards, bool symbols)
    {
        for (var i = 0; i < cards.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 2);
            var card = cards[i];
            var text = $"[{card.ToText(symbols)}]";
            if (card.IsRed)
                ImGui.TextColored(Red, text);
            else
                ImGui.TextUnformatted(text);
        }
    }
}
