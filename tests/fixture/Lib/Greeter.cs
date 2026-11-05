namespace Lib;

public static class Greeter
{
    public static string Greet(string name) => "Hello, " + name;

    public static int Twice(int value) => value * 2;
#if NET10_0_OR_GREATER

    // Only in the net10.0 build, so a change here touches one target framework's API.
    public static string Shout(string name) => name.ToUpperInvariant();
#endif
}
