using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TexasPoker.Engine;

/// <summary>
/// The whole session's log, for the dealer's records: everything the table announced plus the gil the dealer
/// took and paid (seating, rebuys, adjustments, cash-outs), each line with the time. Written to a text file as it
/// goes, so it survives a reload or a crash; the plugin picks the same file up again until the dealer starts a
/// new session. Old session files are kept.
/// </summary>
public sealed class SessionLog
{
    /// <summary>The most lines kept in memory (the file has them all).</summary>
    public const int MaxInMemory = 20000;

    private readonly string folder;
    private readonly List<string> lines = new();

    public SessionLog(string folder) => this.folder = folder;

    /// <summary>The session's lines, oldest first (the last <see cref="MaxInMemory"/>).</summary>
    public IReadOnlyList<string> Lines => lines;

    /// <summary>The file this session is written to, or null if it couldn't be made.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Why the last write failed, or null when the file is up to date.</summary>
    public string? Error { get; private set; }

    public string Folder => folder;

    /// <summary>
    /// Carries on with the session file the plugin was writing (after a reload, a crash or a restart), or starts a
    /// new session when there's none.
    /// </summary>
    public void Resume(string? path, DateTime now)
    {
        if (!string.IsNullOrEmpty(path) && IsInFolder(path) && File.Exists(path))
        {
            try
            {
                lines.Clear();
                lines.AddRange(File.ReadLines(path).TakeLast(MaxInMemory));
                FilePath = path;
                Add(["(plugin started again, same session)"], now);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
            }
        }

        StartNew(now);
    }

    /// <summary>Starts a new session file. The old one stays in the folder.</summary>
    public void StartNew(DateTime now)
    {
        lines.Clear();
        try
        {
            Directory.CreateDirectory(folder);
            var name = $"session-{now:yyyy-MM-dd_HH-mm-ss}";
            var path = Path.Combine(folder, name + ".txt");
            for (var i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, $"{name}-{i}.txt");
            FilePath = path;
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FilePath = null;
            Error = ex.Message;
        }

        Add([$"=== Eorzean Poker session started {now:yyyy-MM-dd HH:mm} ==="], now, stamp: false);
    }

    /// <summary>Adds lines with the time (local), in memory and in the file.</summary>
    public void Add(IEnumerable<string> newLines, DateTime now) => Add(newLines, now, stamp: true);

    private void Add(IEnumerable<string> newLines, DateTime now, bool stamp)
    {
        var stamped = newLines.Select(l => stamp ? $"[{now:HH:mm:ss}] {l}" : l).ToList();
        if (stamped.Count == 0)
            return;

        lines.AddRange(stamped);
        if (lines.Count > MaxInMemory)
            lines.RemoveRange(0, lines.Count - MaxInMemory);

        if (FilePath is null)
            return;
        try
        {
            File.AppendAllLines(FilePath, stamped);
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Copies the whole session file to <paramref name="path"/> (for records, a spreadsheet, Discord...).</summary>
    public void ExportTo(string path)
    {
        if (FilePath is null || !File.Exists(FilePath))
            throw new IOException("This session's log file couldn't be written, so there's nothing to export.");
        File.Copy(FilePath, path, overwrite: true);
    }

    private bool IsInFolder(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
