using TexasPoker.Engine;

namespace TexasPoker.Tests;

public sealed class SessionLogTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 6, 20, 15, 0, DateTimeKind.Local);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "texaspoker-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Lines_are_timed_and_written_to_the_session_file()
    {
        var log = new SessionLog(folder);
        log.Resume(null, T0);
        log.Add(["Hand #1 begins", "P1 calls 100"], T0.AddSeconds(30));

        Assert.NotNull(log.FilePath);
        Assert.EndsWith("session-2026-11-06_20-15-00.txt", log.FilePath);
        var file = File.ReadAllLines(log.FilePath!);
        Assert.Equal(["=== Eorzean Poker session started 2026-11-06 20:15 ===", "[20:15:30] Hand #1 begins", "[20:15:30] P1 calls 100"], file);
        Assert.Equal(file, log.Lines);
        Assert.Null(log.Error);
    }

    [Fact]
    public void A_reload_carries_on_with_the_same_file()
    {
        var first = new SessionLog(folder);
        first.Resume(null, T0);
        first.Add(["Hand #1 begins"], T0);

        var second = new SessionLog(folder);
        second.Resume(first.FilePath, T0.AddMinutes(5));
        second.Add(["Hand #2 begins"], T0.AddMinutes(6));

        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Equal(4, File.ReadAllLines(second.FilePath!).Length);
        Assert.Equal("[20:21:00] Hand #2 begins", second.Lines[^1]);
        Assert.Contains("(plugin started again, same session)", second.Lines[2]);
    }

    [Fact]
    public void A_new_session_gets_its_own_file_and_keeps_the_old_one()
    {
        var log = new SessionLog(folder);
        log.Resume(null, T0);
        var old = log.FilePath!;
        log.StartNew(T0);
        log.Add(["Hand #1 begins"], T0);

        Assert.NotEqual(old, log.FilePath);
        Assert.True(File.Exists(old));
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void A_file_outside_the_log_folder_is_never_resumed()
    {
        var outside = Path.Combine(Path.GetTempPath(), "texaspoker-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "not a session");
        try
        {
            var log = new SessionLog(folder);
            log.Resume(outside, T0);
            Assert.NotEqual(outside, log.FilePath);
            Assert.Equal("not a session", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Export_copies_the_whole_session()
    {
        var log = new SessionLog(folder);
        log.Resume(null, T0);
        log.Add(["P1 wins 500"], T0);
        var target = Path.Combine(folder, "export.txt");

        log.ExportTo(target);

        Assert.Equal(File.ReadAllLines(log.FilePath!), File.ReadAllLines(target));
    }
}
