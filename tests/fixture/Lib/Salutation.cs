namespace Lib;

// Edits a delta can't carry, which the hot reload check makes: a static field's initializer, which already
// ran in a host that stays up, and an embedded resource (greeting.txt), which isn't in a delta at all.
public static class Salutation
{
    public static readonly string Mark = "!";

    public static string Exclaim(string text) => text + Mark;

    public static string Greeting()
    {
        using var stream = typeof(Salutation).Assembly.GetManifestResourceStream("Lib.greeting.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
