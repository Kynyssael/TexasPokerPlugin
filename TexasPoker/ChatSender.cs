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

    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly Configuration config;
    private readonly ConcurrentQueue<string> queue = new();
    private DateTime nextSend = DateTime.MinValue;

    public ChatSender(IFramework framework, IChatGui chatGui, Configuration config)
    {
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

    /// <summary>Queues an /echo: shown (and any &lt;se.N&gt; played) in the dealer's own chat only.</summary>
    public void Echo(string message) => Enqueue($"/echo {message}");

    /// <summary>Prints a message to the dealer's own chat log only.</summary>
    public void PrintLocal(string message) => chatGui.Print(message, "Poker");

    public void Dispose() => framework.Update -= OnUpdate;

    private void Enqueue(string command)
    {
        var clean = new string(command.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 1)
            queue.Enqueue(clean);
    }

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < nextSend || !queue.TryDequeue(out var command))
            return;

        nextSend = DateTime.UtcNow.AddMilliseconds(Math.Max(500, config.MessageDelayMs));
        if (config.SendToChat || command.StartsWith("/echo ", StringComparison.Ordinal))
            Send(command);
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

    private static string Truncate(string line)
    {
        while (Encoding.UTF8.GetByteCount(line) > MaxMessageBytes)
            line = line[..^1];
        return line;
    }
}
