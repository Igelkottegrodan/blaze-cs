namespace BlazeCs;

/// <summary>
/// How this OS compares file paths: ignoring case on Windows, exactly elsewhere. For paths the daemon
/// matches up itself; where it reproduces csc (/pathmap), it compares the way csc does instead.
/// </summary>
static class PathComparer
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Instance { get; } = StringComparer.FromComparison(Comparison);
}
