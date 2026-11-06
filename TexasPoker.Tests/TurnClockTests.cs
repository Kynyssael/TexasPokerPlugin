using TexasPoker.Engine;

namespace TexasPoker.Tests;

public class TurnClockTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);

    private static Table StartedTable()
    {
        var table = new Table { SmallBlind = 50, BigBlind = 100 };
        for (var i = 0; i < 3; i++)
            table.AddSeat($"P{i}", "World", 1000);
        table.StartHand();
        return table;
    }

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Calls_the_turn_at_half_time_then_times_out_once()
    {
        var table = StartedTable();
        var clock = new TurnClock();

        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(0), 60));
        Assert.Equal(60, clock.SecondsLeft(At(0)));
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(29), 60));
        Assert.Equal(TurnClockEvent.CallTurn, clock.Tick(table, At(30), 60));
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(45), 60));
        Assert.Equal(TurnClockEvent.TimeUp, clock.Tick(table, At(60), 60));
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(61), 60));
        Assert.Equal(0, clock.SecondsLeft(At(61)));
    }

    [Fact]
    public void A_new_turn_starts_the_clock_again()
    {
        var table = StartedTable();
        var clock = new TurnClock();
        clock.Tick(table, At(0), 60);
        Assert.Equal(TurnClockEvent.CallTurn, clock.Tick(table, At(40), 60));

        table.Act(PlayerAction.Call);
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(41), 60));
        Assert.Equal(60, clock.SecondsLeft(At(41)));
        Assert.Equal(TurnClockEvent.CallTurn, clock.Tick(table, At(71), 60));
    }

    [Fact]
    public void Pause_stops_the_time_and_resume_carries_on()
    {
        var table = StartedTable();
        var clock = new TurnClock();
        clock.Tick(table, At(0), 60);
        clock.Tick(table, At(20), 60);
        clock.Pause(At(20));

        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(200), 60));
        Assert.Equal(40, clock.SecondsLeft(At(200)));

        clock.Resume(At(200));
        Assert.Equal(TurnClockEvent.CallTurn, clock.Tick(table, At(210), 60));
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(239), 60));
        Assert.Equal(TurnClockEvent.TimeUp, clock.Tick(table, At(240), 60));
    }

    [Fact]
    public void More_time_gives_the_full_time_again()
    {
        var table = StartedTable();
        var clock = new TurnClock();
        clock.Tick(table, At(0), 60);
        Assert.Equal(TurnClockEvent.CallTurn, clock.Tick(table, At(50), 60));

        clock.Restart(At(50));
        Assert.Equal(60, clock.SecondsLeft(At(50)));
        Assert.Equal(TurnClockEvent.CallTurn, clock.Tick(table, At(80), 60));
        Assert.Equal(TurnClockEvent.TimeUp, clock.Tick(table, At(110), 60));
    }

    [Fact]
    public void A_picked_action_holds_the_timeout_until_it_is_confirmed_or_unpicked()
    {
        var table = StartedTable();
        var clock = new TurnClock();
        clock.Tick(table, At(0), 60);
        clock.Tick(table, At(30), 60);

        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(70), 60, hold: true));
        Assert.Equal(TurnClockEvent.TimeUp, clock.Tick(table, At(71), 60, hold: false));
    }

    [Fact]
    public void Sync_starts_the_new_turn_without_acting_and_the_next_tick_agrees()
    {
        var table = StartedTable();
        var clock = new TurnClock();
        clock.Tick(table, At(0), 60);

        table.Act(PlayerAction.Call);
        Assert.True(clock.Sync(table, At(40), 60));
        var ends = clock.EndsAt;
        Assert.Equal(At(100), ends);

        // The plugin's next frame must see the same deadline, or it would send a second update.
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(40.02), 60));
        Assert.Equal(ends, clock.EndsAt);
        Assert.Equal(TurnClockEvent.CallTurn, clock.Tick(table, At(70), 60));
    }

    [Fact]
    public void Off_or_between_hands_the_clock_does_not_run()
    {
        var table = StartedTable();
        var clock = new TurnClock();
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(0), 0));
        Assert.Null(clock.EndsAt);

        table.CancelHand();
        Assert.Equal(TurnClockEvent.None, clock.Tick(table, At(500), 60));
        Assert.Null(clock.SecondsLeft(At(500)));
    }
}
