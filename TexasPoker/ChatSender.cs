using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace TexasPoker;

/// <summary>
/// Sends chat commands one at a time with a delay between them, so a burst of table talk doesn't
/// trip the game's spam filter. Messages are only queued in response to dealer clicks.
/// </summary>
public sealed class ChatSender : IDisposable
{
    // The game rejects chat input over 500 bytes; leave room for the command prefix.
    private const int MaxMessageBytes = 440;

    // A whole chat input over 500 bytes can crash the game: every command (tells too) is cut to fit.
    private const int MaxCommandBytes = 490;

    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly Configuration config;
    private readonly ConcurrentQueue<string> queue = new();
    private readonly Func<bool> canSend;

    // Tells whose echo in the dealer's own chat is hidden (hole cards while the dealer plays), with when they
    // were queued. The game shows every tell you send as ">> Name: text"; this is how that line is caught.
    private readonly ConcurrentDictionary<string, DateTime> hiddenEchoes = new();
    private DateTime nextSend = DateTime.MinValue;

    public ChatSender(IFramework framework, IChatGui chatGui, Configuration config, Func<bool> canSend)
    {
        this.canSend = canSend;
        this.framework = framework;
        this.chatGui = chatGui;
        this.config = config;
        framework.Update += OnUpdate;
    }

    public int Pending => queue.Count;

    public void Clear() => queue.Clear();

    /// <summary>
    /// Queues table lines on the configured channel, one chat message per line. A line that starts with
    /// "/" is sent as typed, so dealer messages can use emotes ("/em shuffles") or another channel.
    /// </summary>
    public void SayToTable(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            Enqueue(line.TrimStart().StartsWith('/') ? Truncate(line.Trim()) : $"{config.Channel} {Truncate(line)}");
    }

    public void Tell(string target, string message) => Enqueue($"/tell {target} {message}");

    /// <summary>
    /// A tell whose copy in your own chat is hidden: the player gets it, but the ">> Name: ..." line you'd
    /// normally see is dropped (see <see cref="TakeHiddenEcho"/>), and a preview doesn't print its text.
    /// </summary>
    public void TellHidden(string target, string message)
    {
        var clean = Clean(message);
        hiddenEchoes[Key(clean)] = DateTime.UtcNow;
        Enqueue($"/tell {target} {clean}");
    }

    /// <summary>Whether this outgoing tell's text is one queued with <see cref="TellHidden"/>; it's forgotten once seen.</summary>
    public bool TakeHiddenEcho(string text)
    {
        foreach (var old in hiddenEchoes.Where(e => DateTime.UtcNow - e.Value > TimeSpan.FromMinutes(10)).Select(e => e.Key).ToList())
            hiddenEchoes.TryRemove(old, out _);
        return hiddenEchoes.TryRemove(Key(text), out _);
    }

    // The game may adjust spacing, so the match ignores whitespace.
    private static string Key(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    // No control characters, and nothing outside the basic plane (emoji, say, from a Discord name): the game's chat
    // can't show them, and they're not worth the risk in input the game parses.
    private static string Clean(string text) => new string(text.Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).ToArray()).Trim();

    /// <summary>Queues an /echo: shown (and any &lt;se.N&gt; played) in the dealer's own chat only.</summary>
    public void Echo(string message) => Enqueue($"/echo {message}");

    /// <summary>Prints a message to the dealer's own chat log only.</summary>
    public void PrintLocal(string message) => chatGui.Print(message, "Poker");

    public void Dispose() => framework.Update -= OnUpdate;

    private void Enqueue(string command)
    {
        var clean = Clean(command);
        while (Encoding.UTF8.GetByteCount(clean) > MaxCommandBytes)
            clean = clean[..^1];
        if (clean.Length > 1)
            queue.Enqueue(clean);
    }

    private void OnUpdate(IFramework _)
    {
        // Held (not dropped) on the title screen, a loading screen or in a cutscene: sent once the game can take them.
        if (DateTime.UtcNow < nextSend || queue.IsEmpty || !canSend() || !queue.TryDequeue(out var command))
            return;

        nextSend = DateTime.UtcNow.AddMilliseconds(Math.Max(500, config.MessageDelayMs));
        if (config.SendToChat || command.StartsWith("/echo ", StringComparison.Ordinal))
            Send(command);
        else if (command.StartsWith("/tell ", StringComparison.Ordinal) && TakeHiddenEcho(TellText(command)))
            PrintLocal($"(preview) {TellTarget(command)} (hidden while you play)");
        else
            PrintLocal($"(preview) {command}");
    }

    private static unsafe void Send(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        var text = Utf8String.FromString(command);
        uiModule->ProcessChatBoxEntry(text);
        text->Dtor(true);
    }

    // "/tell First Last@World text": the target is the next two words.
    private static string TellTarget(string command) => string.Join(' ', command.Split(' ').Skip(1).Take(2));

    private static string TellText(string command) => string.Join(' ', command.Split(' ').Skip(3));

    private static string Truncate(string line)
    {
        while (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
            line = line[..^1];
        return line;
    }
}
