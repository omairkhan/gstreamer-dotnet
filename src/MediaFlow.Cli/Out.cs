namespace MediaFlow.Cli;

internal static class Out
{
    public static void Title(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n== {text} ==");
        Console.ResetColor();
    }

    public static void Pipeline(string description)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"pipeline: {description}\n");
        Console.ResetColor();
    }

    public static void Info(string text) => Console.WriteLine(text);

    public static void Event(string tag, string text, ConsoleColor color = ConsoleColor.Yellow)
    {
        Console.ForegroundColor = color;
        Console.Write($"[{DateTime.Now:HH:mm:ss}] {tag,-10} ");
        Console.ResetColor();
        Console.WriteLine(text);
    }

    public static string Time(TimeSpan? t) => t is { } v ? v.ToString(@"hh\:mm\:ss\.f") : "--:--:--";
}
