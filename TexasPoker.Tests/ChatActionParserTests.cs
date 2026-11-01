using TexasPoker.Engine;

namespace TexasPoker.Tests;

public class ChatActionParserTests
{
    // Facing a 10,000 bet with 100,000 behind: min raise to 20,000.
    private static readonly ActionOptions FacingBet = new(10_000, 10_000, false, true, false, 20_000, 100_000);

    // Nothing to call: can check or bet (min bet 10,000).
    private static readonly ActionOptions Unopened = new(0, 0, true, true, true, 10_000, 100_000);

    [Theory]
    [InlineData("fold", PlayerAction.Fold)]
    [InlineData("I fold", PlayerAction.Fold)]
    [InlineData("Call!", PlayerAction.Call)]
    [InlineData("i'll call", PlayerAction.Call)]
    [InlineData("c", PlayerAction.Call)]
    [InlineData("ALL IN", PlayerAction.AllIn)]
    [InlineData("all-in", PlayerAction.AllIn)]
    [InlineData("im going all in", PlayerAction.AllIn)]
    [InlineData("shove", PlayerAction.AllIn)]
    public void Reads_simple_actions(string message, PlayerAction expected)
    {
        Assert.Equal(expected, ChatActionParser.Parse(message, FacingBet, 10_000)?.Action);
    }

    [Theory]
    [InlineData("raise to 30000", 30_000)]
    [InlineData("raise 30,000", 30_000)]
    [InlineData("r 30k", 30_000)]
    [InlineData("raise by 15k", 25_000)]
    [InlineData("raise 2.5k", 20_000)] // below the minimum: picks the minimum for the dealer to confirm
    [InlineData("raise", 20_000)]
    public void Reads_raises_as_raise_to(string message, long expected)
    {
        var parsed = ChatActionParser.Parse(message, FacingBet, 10_000);
        Assert.Equal(new ParsedAction(PlayerAction.BetOrRaise, expected), parsed);
    }

    [Fact]
    public void Raise_for_whole_stack_is_all_in()
    {
        Assert.Equal(PlayerAction.AllIn, ChatActionParser.Parse("raise 100k", FacingBet, 10_000)?.Action);
        Assert.Equal(PlayerAction.AllIn, ChatActionParser.Parse("raise 1m", FacingBet, 10_000)?.Action);
    }

    [Fact]
    public void Check_only_when_allowed_and_call_with_nothing_to_call_is_check()
    {
        Assert.Null(ChatActionParser.Parse("check", FacingBet, 10_000));
        Assert.Equal(PlayerAction.Check, ChatActionParser.Parse("check", Unopened, 0)?.Action);
        Assert.Equal(PlayerAction.Check, ChatActionParser.Parse("call", Unopened, 0)?.Action);
        Assert.Equal(new ParsedAction(PlayerAction.BetOrRaise, 15_000), ChatActionParser.Parse("bet 15k", Unopened, 0));
    }

    [Theory]
    [InlineData("call me later")]
    [InlineData("I'll fold the laundry")]
    [InlineData("nice hand")]
    [InlineData("10000")]
    [InlineData("raise the roof")]
    [InlineData("")]
    public void Ignores_table_talk(string message)
    {
        Assert.Null(ChatActionParser.Parse(message, FacingBet, 10_000));
    }
}
