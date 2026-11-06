namespace TexasPoker.Engine;

/// <summary>What the turn clock wants done on this tick.</summary>
public enum TurnClockEvent
{
    None,

    /// <summary>Half the time is gone: say whose turn it is again.</summary>
    CallTurn,

    /// <summary>The time is up: check if the player can, otherwise fold.</summary>
    TimeUp,
}

/// <summary>
/// The dealer's turn timer. Each turn (a new player to act, or any change to the table) starts the clock again.
/// At half the time it asks for a Call turn, and when the time is up for an automatic check or fold, once each.
/// </summary>
public sealed class TurnClock
{
    private (int Hand, int Sequence, int Actor) turn = (-1, -1, -1);
    private DateTime started;
    private bool called;
    private bool timedOut;
    private TimeSpan pausedFor;
    private DateTime? pausedAt;
    private TimeSpan total;

    /// <summary>When the current turn's time runs out, or null when no one is to act (or the clock is off).</summary>
    public DateTime? EndsAt { get; private set; }

    public bool Paused => pausedAt is not null;

    /// <summary>
    /// Catches up with the table without acting: a new turn starts its time and <see cref="EndsAt"/> is set.
    /// Called right after an action too, so the update sent for it already carries the new turn's time.
    /// Returns false when no clock runs (it's off, or no one is to act).
    /// </summary>
    public bool Sync(Table table, DateTime now, int seconds)
    {
        if (seconds <= 0 || table.Actor is null)
        {
            Reset();
            return false;
        }

        var current = (table.HandNumber, table.Sequence, table.ActorIndex);
        if (current != turn)
        {
            turn = current;
            Restart(now);
        }

        total = TimeSpan.FromSeconds(seconds);
        EndsAt = started + PausedFor(now) + total;
        return true;
    }

    private TimeSpan PausedFor(DateTime now) => pausedFor + (pausedAt is { } at ? now - at : TimeSpan.Zero);

    /// <summary>
    /// Moves the clock on. <paramref name="seconds"/> is the time a player has (0 = off). While
    /// <paramref name="hold"/> is set (the dealer has picked an action to confirm), the time doesn't run out.
    /// </summary>
    public TurnClockEvent Tick(Table table, DateTime now, int seconds, bool hold = false)
    {
        if (!Sync(table, now, seconds) || Paused)
            return TurnClockEvent.None;

        var elapsed = now - started - PausedFor(now);
        if (!timedOut && !hold && elapsed >= total)
        {
            timedOut = true;
            called = true;
            return TurnClockEvent.TimeUp;
        }

        if (!called && elapsed >= total / 2)
        {
            called = true;
            return TurnClockEvent.CallTurn;
        }

        return TurnClockEvent.None;
    }

    /// <summary>Gives the player to act their full time again (the dealer granted more time).</summary>
    public void Restart(DateTime now)
    {
        if (EndsAt is not null)
            EndsAt = now + total;
        started = now;
        called = false;
        timedOut = false;
        pausedFor = TimeSpan.Zero;
        pausedAt = null;
    }

    /// <summary>Stops the time running out (for a ruling, a question...) until it's resumed.</summary>
    public void Pause(DateTime now) => pausedAt ??= now;

    public void Resume(DateTime now)
    {
        if (pausedAt is { } at)
            pausedFor += now - at;
        pausedAt = null;
    }

    /// <summary>The seconds left this turn, or null when the clock isn't running.</summary>
    public int? SecondsLeft(DateTime now) =>
        EndsAt is { } end ? (int)Math.Max(0, Math.Ceiling((end - now).TotalSeconds)) : null;

    private void Reset()
    {
        turn = (-1, -1, -1);
        EndsAt = null;
        pausedAt = null;
        pausedFor = TimeSpan.Zero;
    }
}
