using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BlazeCs;

/// <summary>
/// `blaze-cs` without a .NET runtime start: compiled Native AOT, it serves the commands that reach a
/// running daemon (build, test, stop) and hands every other case to the CLI (blaze-cs.dll, next to this
/// executable), which starts the daemon, runs the app, and prints usage.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        // Only a bare command: options (such as `test --all`) and their parsing stay in the CLI.
        if (args is not [("build" or "test" or "stop") and var command])
            return RunCli(args);

        var solutionPath = DaemonClient.FindSolution(Environment.CurrentDirectory);
        if (solutionPath is null)
            return 2;
        using var socket = DaemonClient.Connect(DaemonClient.SocketPath(solutionPath));
        if (socket is null)
            return RunCli(args);

        // The daemon is the CLI's build, so the identity sent is the CLI's, not this executable's.
        var exitCode = DaemonClient.Exchange(socket, command, CliBuild(), all: false, out var otherBuild);
        if (otherBuild is not { } daemonProcess)
            return exitCode;
        // The daemon has handed over and exits; the CLI starts one of its own build.
        DaemonClient.WaitForRestart(daemonProcess);
        return RunCli(args);
    }

    /// <summary>The module version id of blaze-cs.dll, which is what the daemon compares (Daemon.Build).</summary>
    static Guid CliBuild()
    {
        using var pe = new PEReader(File.OpenRead(CliPath));
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    static string CliPath => Path.Combine(AppContext.BaseDirectory, "blaze-cs.dll");

    static int RunCli(string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(CliPath);
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var cli = Process.Start(start)!;
        cli.WaitForExit();
        return cli.ExitCode;
    }
}
