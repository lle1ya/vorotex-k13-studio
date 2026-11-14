using System.Diagnostics;

namespace Vorotex.K13;

/// <summary>
/// v5.3: the one playback loop for macros played on the PC (key/ring action and the «Проверка» window).
/// Delays are cancellable (Stop reacts at once instead of after the current pause), every key or mouse button that is
/// still down when playback ends or is cancelled gets released, and «по кругу» (Toggle) really repeats until stopped.
/// Pure logic – the actual SendInput is passed in, so it is unit-tested without touching the system.
/// </summary>
public static class MacroSequencer
{
    /// <summary>A looping (Toggle) macro without pauses would flood the input queue: each pass takes at least this long.</summary>
    public const int MinLoopPassMs = 50;

    /// <summary>How many passes the PC plays: the saved N for «N раз», endless for «по кругу» (until stopped), one for «пока зажата».</summary>
    public static int Passes(MacroRepeatMode mode, int repeat) => mode switch
    {
        MacroRepeatMode.Count => Math.Max(1, repeat),
        MacroRepeatMode.Toggle => int.MaxValue,
        _ => 1,
    };

    /// <summary>Plays <paramref name="events"/> <paramref name="passes"/> times. Returns the number of passes started.</summary>
    /// <param name="filter">Return false to skip an event (e.g. mouse events in the test window).</param>
    /// <param name="wait">Waits the given ms; returns true if cancelled meanwhile. Defaults to the token's wait handle.</param>
    public static int Run(IReadOnlyList<MacroEvent> events, int passes, Action<MacroEvent> send, CancellationToken ct,
        Func<MacroEvent, bool>? filter = null, Func<int, bool>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(send);
        wait ??= ms => ct.WaitHandle.WaitOne(ms);
        var held = new List<byte>();
        int done = 0;
        bool loop = passes == int.MaxValue;
        try
        {
            if (events.Count == 0) return 0;
            for (int r = 0; r < passes && !ct.IsCancellationRequested; r++)
            {
                done = r + 1;
                long start = Stopwatch.GetTimestamp();
                foreach (var e in events)
                {
                    if (ct.IsCancellationRequested) break;
                    if (e.DelayMs > 0 && wait(e.DelayMs)) break;
                    if (ct.IsCancellationRequested) break;
                    if (filter != null && !filter(e)) continue;
                    send(e);
                    if (!e.IsWheel && e.Code != MouseCodes.Move)
                    {
                        if (e.Down) { if (!held.Contains(e.Code)) held.Add(e.Code); }
                        else held.Remove(e.Code);
                    }
                }
                if (loop)
                {
                    int left = MinLoopPassMs - (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (left > 0 && wait(left)) break;
                }
            }
        }
        finally
        {
            // never leave a key or mouse button stuck (reverse order: modifiers last)
            for (int i = held.Count - 1; i >= 0; i--)
            {
                try { send(new MacroEvent { Down = false, Code = held[i] }); } catch (Exception) { /* keep releasing the rest */ }
            }
        }
        return done;
    }

    /// <summary>
    /// After recording: adds a release for every key/button that was pressed but whose release was not captured
    /// (e.g. «Стоп» clicked while a key was still down). Without this the keypad left that key held after the macro.
    /// Returns how many releases were added.
    /// </summary>
    public static int CloseDanglingPresses(List<MacroEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var down = new List<byte>();
        foreach (var e in events)
        {
            if (e.IsWheel || e.Code == MouseCodes.Move) continue;
            if (e.Down) { if (!down.Contains(e.Code)) down.Add(e.Code); }
            else down.Remove(e.Code);
        }
        for (int i = down.Count - 1; i >= 0; i--) events.Add(new MacroEvent { Down = false, Code = down[i], Delay = 1 });
        return down.Count;
    }
}

/// <summary>v5.3: how «Ввести текст» sends a string — CR/LF/CRLF become one Enter, TAB becomes Tab, the rest is Unicode.</summary>
public static class TextKeys
{
    public const ushort VkReturn = 0x0D, VkTab = 0x09;

    /// <returns>(character, virtual key) pairs; vk = 0 means «send the character as Unicode».</returns>
    public static IEnumerable<(char Ch, ushort Vk)> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; yield return ('\n', VkReturn); }
            else if (c == '\n') yield return ('\n', VkReturn);
            else if (c == '\t') yield return ('\t', VkTab);
            else yield return (c, 0);
        }
    }
}
