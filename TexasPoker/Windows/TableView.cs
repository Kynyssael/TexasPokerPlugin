using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using TexasPoker.Engine;

namespace TexasPoker.Windows;

/// <summary>
/// Draws the table as a poker table seen from above: the dealer sits at the bottom (south) and players
/// take the seats around it clockwise in the order they joined, with the pot, board, bets and the
/// dealer button on the felt.
/// </summary>
public sealed class TableView
{
    private static readonly Vector2 SeatSize = new(150, 64);
    private static readonly Vector2 SmallCard = new(22, 30);
    private static readonly Vector2 BoardCard = new(38, 52);

    private static readonly uint Rail = Col(0.33f, 0.19f, 0.09f);
    private static readonly uint RailEdge = Col(0.2f, 0.11f, 0.05f);
    private static readonly uint Felt = Col(0.09f, 0.36f, 0.2f);
    private static readonly uint FeltLine = Col(0.16f, 0.46f, 0.28f);
    private static readonly uint SeatBg = Col(0.08f, 0.08f, 0.09f, 0.92f);
    private static readonly uint SeatBorder = Col(0.35f, 0.35f, 0.38f);
    private static readonly uint DealerBg = Col(0.18f, 0.12f, 0.3f, 0.95f);
    private static readonly uint White = Col(1f, 1f, 1f);
    private static readonly uint CardFace = Col(0.96f, 0.96f, 0.93f);
    private static readonly uint CardBack = Col(0.2f, 0.28f, 0.6f);
    private static readonly uint CardBackLine = Col(0.45f, 0.55f, 0.85f);
    private static readonly uint Black = Col(0.08f, 0.08f, 0.08f);
    private static readonly uint RedSuit = Col(0.85f, 0.12f, 0.12f);
    private static readonly uint Chip = Col(0.9f, 0.72f, 0.2f);
    private static readonly uint ChipEdge = Col(0.55f, 0.4f, 0.05f);
    private static readonly uint GoldU = Col(DealerControls.Gold);
    private static readonly uint MutedU = Col(DealerControls.Muted);
    private static readonly uint GoodU = Col(DealerControls.Good);

    private readonly Plugin plugin;

    public TableView(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private Table Table => plugin.Table;

    private Configuration Config => plugin.Configuration;

    public void Draw(float height)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));

        var draw = ImGui.GetWindowDrawList();
        var center = origin + new Vector2(width / 2, height / 2);

        // The felt leaves room around it for the seat boxes, which sit on the rail.
        var half = new Vector2(Math.Max(120, (width / 2) - (SeatSize.X * 0.62f)),
                               Math.Max(70, (height / 2) - (SeatSize.Y * 0.62f)));
        var rail = half + new Vector2(12, 12);
        draw.AddRectFilled(center - rail - new Vector2(2, 2), center + rail + new Vector2(2, 2), RailEdge, rail.Y + 2);
        draw.AddRectFilled(center - rail, center + rail, Rail, rail.Y);
        draw.AddRectFilled(center - half, center + half, Felt, half.Y);
        var line = half - new Vector2(14, 14);
        draw.AddRect(center - line, center + line, FeltLine, line.Y, ImDrawFlags.None, 2);

        DrawCenter(draw, center);

        var seats = Table.Seats;
        if (seats.Count == 0)
            CenteredText(draw, center + new Vector2(0, 40), MutedU, "Add players in the Players tab to seat them here");

        // The dealer takes the south slot; players fill the other slots clockwise from the dealer's left.
        var slots = seats.Count + 1;
        DrawDealer(draw, SeatPoint(center, rail, 0, slots));
        for (var i = 0; i < seats.Count; i++)
        {
            var point = SeatPoint(center, rail, i + 1, slots);
            DrawSeat(draw, seats[i], i, point, center);
        }
    }

    public string PositionLabel(int index)
    {
        if (!Table.HandInProgress && Table.Street != Street.HandOver)
            return string.Empty;

        var labels = new List<string>();
        if (index == Table.ButtonIndex)
            labels.Add("D");
        if (index == Table.SmallBlindIndex)
            labels.Add("SB");
        if (index == Table.BigBlindIndex)
            labels.Add("BB");
        return string.Join(" ", labels);
    }

    public string StatusText(Seat seat)
    {
        if (seat.SittingOut)
            return "Sitting out";
        if (!seat.InHand)
            return Table.HandInProgress ? "Not in hand" : seat.Stack == 0 ? "Busted" : string.Empty;
        if (seat.Folded)
            return "Folded";
        if (seat.AllIn)
            return "All-in";
        if (Table.Actor == seat)
            return "To act";
        return Table.HandInProgress ? "In hand" : string.Empty;
    }

    public Vector4 StatusColor(Seat seat) =>
        seat.Folded || seat.SittingOut || !seat.InHand ? DealerControls.Muted
        : Table.Actor == seat ? DealerControls.Gold
        : DealerControls.Good;

    /// <summary>Slot 0 is straight down (south); the rest go clockwise around the rail.</summary>
    private static Vector2 SeatPoint(Vector2 center, Vector2 radius, int slot, int slots)
    {
        var angle = (MathF.PI / 2) + (slot * 2 * MathF.PI / slots);
        return center + new Vector2(MathF.Cos(angle) * radius.X, MathF.Sin(angle) * radius.Y);
    }

    private void DrawCenter(ImDrawListPtr draw, Vector2 center)
    {
        var board = Table.Board;
        if (Table.HandInProgress || board.Count > 0)
        {
            var total = (BoardCard.X * 5) + (6 * 4);
            var x = center.X - (total / 2);
            for (var i = 0; i < 5; i++)
            {
                var min = new Vector2(x + (i * (BoardCard.X + 6)), center.Y - (BoardCard.Y / 2) - 8);
                if (i < board.Count)
                    DrawCard(draw, min, BoardCard, board[i], 1.35f);
                else
                    draw.AddRect(min, min + BoardCard, FeltLine, 4, ImDrawFlags.None, 1.5f);
            }
        }

        if (Table.HandInProgress)
        {
            CenteredText(draw, center + new Vector2(0, (BoardCard.Y / 2) + 4), GoldU, $"Pot {Table.Pot:N0}", 1.3f);
            CenteredText(draw, center - new Vector2(0, (BoardCard.Y / 2) + 30), MutedU, $"Hand #{Table.HandNumber}  -  {Table.Street}");
        }
        else
        {
            var title = Table.Street == Street.HandOver ? $"Hand #{Table.HandNumber} finished" : "Texas Hold'em";
            CenteredText(draw, center - new Vector2(0, (BoardCard.Y / 2) + 30), MutedU, title);
            CenteredText(draw, center + new Vector2(0, (BoardCard.Y / 2) + 4), MutedU,
                         $"Blinds {Table.SmallBlind:N0}/{Table.BigBlind:N0}");
        }
    }

    private void DrawDealer(ImDrawListPtr draw, Vector2 point)
    {
        var size = new Vector2(120, 40);
        var min = point - (size / 2);
        draw.AddRectFilled(min, min + size, DealerBg, 8);
        draw.AddRect(min, min + size, GoldU, 8, ImDrawFlags.None, 1.5f);
        CenteredText(draw, point - new Vector2(0, 15), GoldU, "DEALER");
        CenteredText(draw, point + new Vector2(0, 1), MutedU, plugin.Chat.Pending > 0 ? $"{plugin.Chat.Pending} msg queued" : $"Chat {Config.Channel}");
    }

    private void DrawSeat(ImDrawListPtr draw, Seat seat, int index, Vector2 point, Vector2 center)
    {
        var isActor = Table.Actor == seat;
        var faded = seat.SittingOut || seat.Folded || (Table.HandInProgress && !seat.InHand);
        var min = point - (SeatSize / 2);
        var max = min + SeatSize;

        draw.AddRectFilled(min, max, SeatBg, 8);
        draw.AddRect(min, max, isActor ? GoldU : SeatBorder, 8, ImDrawFlags.None, isActor ? 3 : 1);

        var nameColor = isActor ? GoldU : faded ? MutedU : White;
        var name = Fit(seat.Name, SeatSize.X - 12);
        draw.AddText(min + new Vector2(8, 5), nameColor, name);

        var tags = PositionLabel(index).Replace("D", string.Empty).Trim();
        if (tags.Length > 0)
        {
            var tagWidth = ImGui.CalcTextSize(tags).X;
            draw.AddText(new Vector2(max.X - tagWidth - 8, min.Y + 5), GoldU, tags);
        }

        draw.AddText(min + new Vector2(8, 23), faded ? MutedU : GoodU, $"{seat.Stack:N0}");

        // Cards on the right, status text on the left of the bottom row.
        if (seat.InHand && seat.HoleCards.Count > 0 && !seat.Folded)
        {
            var reveal = Config.ShowHoleCards || (Table.Street == Street.HandOver && seat.Contending);
            for (var c = 0; c < seat.HoleCards.Count; c++)
            {
                var cardMin = new Vector2(max.X - 8 - ((SmallCard.X + 3) * (2 - c)) + 3, max.Y - SmallCard.Y - 6);
                if (reveal)
                    DrawCard(draw, cardMin, SmallCard, seat.HoleCards[c], 1f);
                else
                    DrawCardBack(draw, cardMin, SmallCard);
            }
        }

        var status = StatusText(seat);
        if (status.Length > 0)
            draw.AddText(min + new Vector2(8, 42), Col(StatusColor(seat)), status);

        // Bets and the dealer button sit on the felt just past the edge of the seat box, facing the middle.
        var toward = Vector2.Normalize(center - point);
        var edge = EdgeDistance(toward, SeatSize / 2);
        var gap = Math.Clamp(Config.BetDistance, 0, 150);
        if (index == Table.ButtonIndex && (Table.HandInProgress || Table.Street == Street.HandOver))
        {
            // Beside the bet, a little to one side, so the two never sit on top of each other.
            var side = new Vector2(-toward.Y, toward.X);
            var button = point + (toward * (edge + 14)) + (side * 40);
            draw.AddCircleFilled(button, 11, White);
            draw.AddCircle(button, 11, Black, 0, 1.5f);
            CenteredText(draw, button - new Vector2(0, ImGui.GetTextLineHeight() / 2), Black, "D");
        }

        if (seat.StreetBet > 0)
        {
            var chip = point + (toward * (edge + gap + 9));
            draw.AddCircleFilled(chip, 9, Chip);
            draw.AddCircle(chip, 9, ChipEdge, 0, 2);

            // The amount goes on the side of the chip away from the seat, so it never covers the box.
            var amount = $"{seat.StreetBet:N0}";
            var textSize = ImGui.CalcTextSize(amount);
            var textPos = toward.X < -0.3f
                ? chip + new Vector2(-13 - textSize.X, -textSize.Y / 2)
                : chip + new Vector2(13, -textSize.Y / 2);
            draw.AddText(textPos, White, amount);
        }

        if (ImGui.IsMouseHoveringRect(min, max))
        {
            ImGui.SetTooltip($"{seat.DisplayName}\nStack {seat.Stack:N0}\nBought in {seat.TotalBuyIn:N0}\n" +
                             $"Net {seat.Net:+#,0;-#,0;0}" + (seat.StreetBet > 0 ? $"\nBet this round {seat.StreetBet:N0}" : string.Empty));
        }
    }

    /// <summary>How far from a box's centre its edge is, going in <paramref name="direction"/>.</summary>
    private static float EdgeDistance(Vector2 direction, Vector2 halfSize)
    {
        var x = MathF.Abs(direction.X) > 0.001f ? halfSize.X / MathF.Abs(direction.X) : float.MaxValue;
        var y = MathF.Abs(direction.Y) > 0.001f ? halfSize.Y / MathF.Abs(direction.Y) : float.MaxValue;
        return MathF.Min(x, y);
    }

    private void DrawCard(ImDrawListPtr draw, Vector2 min, Vector2 size, Card card, float scale)
    {
        draw.AddRectFilled(min, min + size, CardFace, 3);
        draw.AddRect(min, min + size, Black, 3);
        var color = card.IsRed ? RedSuit : Black;
        var rank = card.RankText;
        var suit = card.SuitText(Config.UiSuitSymbols);
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * scale;
        var rankSize = ImGui.CalcTextSize(rank) * scale;
        var suitSize = ImGui.CalcTextSize(suit) * scale;
        draw.AddText(font, fontSize, new Vector2(min.X + ((size.X - rankSize.X) / 2), min.Y + 1), color, rank);
        draw.AddText(font, fontSize, new Vector2(min.X + ((size.X - suitSize.X) / 2), min.Y + size.Y - suitSize.Y - 1), color, suit);
    }

    private static void DrawCardBack(ImDrawListPtr draw, Vector2 min, Vector2 size)
    {
        draw.AddRectFilled(min, min + size, CardBack, 3);
        draw.AddRect(min + new Vector2(3, 3), min + size - new Vector2(3, 3), CardBackLine, 2);
    }

    private static void CenteredText(ImDrawListPtr draw, Vector2 topCenter, uint color, string text, float scale = 1f)
    {
        var size = ImGui.CalcTextSize(text) * scale;
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, new Vector2(topCenter.X - (size.X / 2), topCenter.Y), color, text);
    }

    private static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;
        while (text.Length > 1 && ImGui.CalcTextSize(text + "...").X > width)
            text = text[..^1];
        return text + "...";
    }

    private static uint Col(float r, float g, float b, float a = 1f) => Col(new Vector4(r, g, b, a));

    // ImGui packs colours as ABGR; done by hand so the static colours don't need ImGui loaded.
    private static uint Col(Vector4 c)
    {
        static uint Byte(float v) => (uint)Math.Clamp(MathF.Round(v * 255f), 0, 255);
        return (Byte(c.W) << 24) | (Byte(c.Z) << 16) | (Byte(c.Y) << 8) | Byte(c.X);
    }
}
