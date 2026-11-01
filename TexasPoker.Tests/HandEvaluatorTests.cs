using TexasPoker.Engine;

namespace TexasPoker.Tests;

public class HandEvaluatorTests
{
    private static HandValue Eval(string cards) => HandEvaluator.Evaluate(Card.ParseMany(cards));

    [Theory]
    [InlineData("As Ks Qs Js 10s 2d 3c", HandCategory.StraightFlush, "Royal flush")]
    [InlineData("9h 9d 9s 9c Kd 2c 3c", HandCategory.FourOfAKind, "Four of a kind, Nines")]
    [InlineData("Kh Kd Ks 5c 5d 2c 3h", HandCategory.FullHouse, "Full house, Kings full of Fives")]
    [InlineData("Ah 9h 6h 4h 2h Kc Qd", HandCategory.Flush, "Flush, Ace high")]
    [InlineData("Ah 2d 3c 4s 5h Kc 9d", HandCategory.Straight, "Straight, Five high")]
    [InlineData("7h 7d 7s Kc 2d 9c 4h", HandCategory.ThreeOfAKind, "Three of a kind, Sevens")]
    [InlineData("Kh Kd 6s 6c 2d 2c 9h", HandCategory.TwoPair, "Two pair, Kings and Sixes")]
    [InlineData("Qh Qd 6s 4c 2d 9c 8h", HandCategory.OnePair, "Pair of Queens")]
    [InlineData("Ah Jd 6s 4c 2d 9c 8h", HandCategory.HighCard, "High card Ace")]
    public void Classifies_hands(string cards, HandCategory category, string description)
    {
        var value = Eval(cards);
        Assert.Equal(category, value.Category);
        Assert.Equal(description, value.Describe());
    }

    [Fact]
    public void Chat_format_uses_suit_symbols()
    {
        Assert.Equal("[A♣][10♥][K♦][2♠]", Card.Join(Card.ParseMany("Ac 10h Kd 2s"), true));
        Assert.Equal("[Ac][10h]", Card.Join(Card.ParseMany("Ac 10h"), false));
    }

    [Fact]
    public void Wheel_loses_to_six_high_straight()
    {
        Assert.True(Eval("Ah 2d 3c 4s 5h") .Score < Eval("2d 3c 4s 5h 6d").Score);
    }

    [Fact]
    public void Kicker_breaks_tie()
    {
        Assert.True(Eval("Ah Ad Kc 7s 2h").Score > Eval("As Ac Qc 7d 2d").Score);
    }

    [Fact]
    public void Board_playing_is_a_tie()
    {
        Assert.Equal(Eval("2c 3d As Ks Qs Js 10s").Score, Eval("4c 5d As Ks Qs Js 10s").Score);
    }

    [Fact]
    public void Flush_beats_straight()
    {
        Assert.True(Eval("2h 5h 7h 9h Jh").Score > Eval("10c Jd Qs Kh Ac").Score);
    }
}
