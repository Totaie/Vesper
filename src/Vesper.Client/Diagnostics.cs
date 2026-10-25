namespace Vesper;

/// <summary>An in-memory diagnostic log (never written to disk), shown in Settings → About.</summary>
public static class Diagnostics
{
    private static readonly Queue<string> Lines = new();

    public static void Add(string line)
    {
        lock (Lines)
        {
            Lines.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Lines.Count > 200) Lines.Dequeue();
        }
#if DEBUG
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "debug-diagnostics.log"), $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}"); } catch { }
#endif
    }

    public static string Text
    {
        get { lock (Lines) return string.Join(Environment.NewLine, Lines); }
    }
}
