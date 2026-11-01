using System.Security.Cryptography;
using TexasPoker.Engine;

namespace TexasPoker.Tests;

public class CardSealerTests
{
    [Fact]
    public void Sealed_cards_open_with_the_right_token_only()
    {
        var sealedText = CardSealer.Seal("aB3xQ9", "k7Hq2mXz9PwR", "{\"cards\":[\"As\",\"Kd\"]}");

        Assert.Equal("{\"cards\":[\"As\",\"Kd\"]}", CardSealer.Open("aB3xQ9", "k7Hq2mXz9PwR", sealedText));
        Assert.ThrowsAny<CryptographicException>(() => CardSealer.Open("aB3xQ9", "wrongToken12", sealedText));
        Assert.ThrowsAny<CryptographicException>(() => CardSealer.Open("otherT", "k7Hq2mXz9PwR", sealedText));
    }

    [Fact]
    public void Same_cards_seal_differently_each_time()
    {
        Assert.NotEqual(CardSealer.Seal("t", "token", "x"), CardSealer.Seal("t", "token", "x"));
    }

    [Fact]
    public void Lookup_is_stable_and_reveals_nothing_of_the_token()
    {
        var lookup = CardSealer.Lookup("aB3xQ9", "k7Hq2mXz9PwR");

        Assert.Equal(lookup, CardSealer.Lookup("aB3xQ9", "k7Hq2mXz9PwR"));
        Assert.Equal(22, lookup.Length);
        Assert.DoesNotContain("k7Hq2mXz9PwR", lookup);
        Assert.NotEqual(lookup, CardSealer.Lookup("aB3xQ9", "k7Hq2mXz9PwS"));
    }

    [Fact]
    public void Changed_ciphertext_is_rejected()
    {
        var sealedText = CardSealer.Seal("t", "token", "secret cards").ToCharArray();
        sealedText[20] = sealedText[20] == 'A' ? 'B' : 'A';

        Assert.ThrowsAny<CryptographicException>(() => CardSealer.Open("t", "token", new string(sealedText)));
    }
}
