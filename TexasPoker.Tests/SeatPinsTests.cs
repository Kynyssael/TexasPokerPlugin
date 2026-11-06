using System.Text.Json;
using TexasPoker.Engine;

namespace TexasPoker.Tests;

public class SeatPinsTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_pin_is_four_digits_and_works_for_ten_minutes()
    {
        var pins = new SeatPins();
        var pin = pins.Issue("alisaie leveilleur@sargatanas", T0);

        Assert.Matches("^[0-9]{4}$", pin);
        Assert.Equal(pin, pins.PinOf("Alisaie Leveilleur@Sargatanas", T0.AddMinutes(9)));
        Assert.Single(pins.Live(T0.AddMinutes(9)));
        Assert.Null(pins.PinOf("alisaie leveilleur@sargatanas", T0.AddMinutes(10)));
        Assert.Empty(pins.Live(T0.AddMinutes(10)));
    }

    [Fact]
    public void A_new_pin_replaces_the_old_one_and_players_who_left_lose_theirs()
    {
        var pins = new SeatPins();
        pins.Issue("a@w", T0);
        var second = pins.Issue("a@w", T0.AddMinutes(5));
        pins.Issue("b@w", T0);

        Assert.Equal(second, pins.PinOf("a@w", T0.AddMinutes(12)));
        pins.KeepOnly(["a@w"]);
        Assert.Null(pins.PinOf("b@w", T0));
        Assert.Single(pins.Live(T0.AddMinutes(12)));
    }

    [Fact]
    public void A_stopped_pin_is_forgotten()
    {
        var pins = new SeatPins();
        pins.Issue("Alisaie Leveilleur@Sargatanas", T0);
        pins.Forget("alisaie leveilleur@sargatanas");
        Assert.Null(pins.PinOf("Alisaie Leveilleur@Sargatanas", T0));
    }

    [Fact]
    public void The_token_sealed_with_a_pin_opens_with_that_pin_only()
    {
        // As the page does it: lookup and key from "pin-v1:Name@World:PIN", the token as a JSON string.
        var secret = SeatPins.Secret("Alisaie Leveilleur@Sargatanas", "4817");
        var sealedToken = CardSealer.Seal("aB3xQ9", secret, JsonSerializer.Serialize("k7Hq2mXz9PwR"));

        Assert.Equal("\"k7Hq2mXz9PwR\"", CardSealer.Open("aB3xQ9", secret, sealedToken));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            CardSealer.Open("aB3xQ9", SeatPins.Secret("Alisaie Leveilleur@Sargatanas", "4818"), sealedToken));
        Assert.NotEqual(CardSealer.Lookup("aB3xQ9", secret), CardSealer.Lookup("aB3xQ9", SeatPins.Secret("Y'shtola Rhul@Sargatanas", "4817")));
    }
}
