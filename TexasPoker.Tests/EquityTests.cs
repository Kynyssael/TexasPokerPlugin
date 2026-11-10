using TexasPoker.Engine;

namespace TexasPoker.Tests;

public sealed class EquityTests
{
    private static IReadOnlyList<Card> C(string text) => Card.ParseMany(text);

    [Fact]
    public void Fast_score_matches_the_hand_evaluator()
    {
        var random = new Random(7);
        for (var i = 0; i < 20_000; i++)
        {
            var deck = Deck.Ordered().OrderBy(_ => random.Next()).ToList();
            var cards = deck.Take(5 + (i % 3)).ToList();
            Assert.Equal(HandEvaluator.Evaluate(cards).Score, Equity.FastScore(cards.ToArray()));
        }
    }

    [Fact]
    public void Known_hands_score_like_the_evaluator()
    {
        foreach (var hand in new[] { "Ah Kh Qh Jh Th 2c 3d", "5c 4c 3c 2c Ac Kd Kh", "As Ad Ah Kc Kd Ks 2h", "9h 9d 9c 9s Ah Kh Qh",
                                     "Ah 2h 3h 4h 7h 5c 9d", "Ac 2d 3h 4s 5c Kd Kh", "Tc Td 9h 9s 8c 8d Ah", "2h 3h 4h 5h 6h 7h 8h" })
            Assert.Equal(HandEvaluator.Evaluate(C(hand)).Score, Equity.FastScore(C(hand).ToArray()));
    }

    [Fact]
    public void Aces_are_about_four_to_one_against_kings()
    {
        var result = Equity.Calculate([C("As Ah"), C("Ks Kh")], [], seed: 1);

        Assert.InRange(result[0].Win, 0.80, 0.84);
        Assert.InRange(result[1].Win, 0.16, 0.20);
    }

    [Fact]
    public void On_the_flop_every_run_out_counts()
    {
        // A flush draw against an overpair on the flop: about 35% (9 outs, two cards to come).
        var result = Equity.Calculate([C("Ah Kh"), C("Qs Qc")], C("Qh 7h 2d"), seed: 1);

        Assert.InRange(result[0].Win, 0.20, 0.30); // the queens have a set: the flush still gets there sometimes
        Assert.Equal(1.0, result[0].Win + result[0].Tie + result[1].Win + result[1].Tie, 6);
    }

    [Fact]
    public void A_split_board_is_a_tie()
    {
        var result = Equity.Calculate([C("2c 3d"), C("2h 3s")], C("Ah Kh Qh Jh Th"));

        Assert.Equal(0, result[0].Win);
        Assert.Equal(1, result[0].Tie);
    }

    [Fact]
    public void On_the_turn_a_flush_draw_has_nine_outs()
    {
        var result = Equity.Calculate([C("Ah Kh"), C("Qs Qc")], C("9h 7h 2d 3c"));

        Assert.Equal(0, result[1].Outs); // the queens are ahead
        Assert.Equal(9 + 6, result[0].Outs); // 9 hearts, plus the 3 aces and 3 kings left (a pair over the queens)
        Assert.Equal(HandCategory.Flush, result[0].OutsTo);
    }

    [Fact]
    public void Commentary_opens_only_with_the_commentator_key()
    {
        using var commentator = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var other = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = CommentatorSealer.PublicKeyOf(commentator);

        var sealedText = CommentatorSealer.Seal(publicKey, "{\"cards\":\"As Kd\"}");

        Assert.True(CommentatorSealer.IsPublicKey(publicKey));
        Assert.False(CommentatorSealer.IsPublicKey("not-a-key"));
        Assert.DoesNotContain("As", sealedText);
        Assert.Equal("{\"cards\":\"As Kd\"}", CommentatorSealer.Open(commentator, sealedText));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => CommentatorSealer.Open(other, sealedText));
    }
}
