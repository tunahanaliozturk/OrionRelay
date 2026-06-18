namespace Moongazing.OrionRelay.Demo;

/// <summary>
/// Tiny console formatting helpers so each feature demo prints a consistent,
/// readable section without pulling in a logging dependency.
/// </summary>
internal static class DemoConsole
{
    public static void Banner(string title)
    {
        var line = new string('=', 70);
        Console.WriteLine();
        Console.WriteLine(line);
        Console.WriteLine($"  {title}");
        Console.WriteLine(line);
    }

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {title} " + new string('-', Math.Max(0, 64 - title.Length)));
    }

    public static void Item(string label, string value)
        => Console.WriteLine($"   {label,-22}: {value}");

    public static void Note(string text)
        => Console.WriteLine($"   {text}");

    public static void Ok(string text)
        => Console.WriteLine($"   [PASS] {text}");

    public static void Reject(string text)
        => Console.WriteLine($"   [REJECTED] {text}");
}
