using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlazeCs;

/// <summary>
/// `blaze-cs build|test|run|stop`: a thin client. The work happens in one daemon per solution that the
/// first command starts and later commands reuse.
/// </summary>
static class Program
{
    const string Usage = "usage: blaze-cs build | test | run [-- app arguments] | stop";

    static int Main(string[] args)
    {
        if (args is ["daemon", var solution])
            return RunDaemon(solution);
        if (args.Length == 0 || args[0] is not ("build" or "test" or "run" or "stop"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var solutionPath = FindSolution(Environment.CurrentDirectory);
        if (solutionPath is null)
        {
            Console.Error.WriteLine("blaze-cs: no .sln or .slnx file here or in any parent directory");
            return 2;
        }

        var command = args[0];
        var exitCode = Send(solutionPath, command == "run" ? "build" : command);
        if (command != "run" || exitCode != 0)
            return exitCode;

        // The app runs in this process's terminal, not the daemon's.
        var run = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var argument in new[] { "run", "--no-build" }.Concat(args.Skip(1)))
            run.ArgumentList.Add(argument);
        using var app = Process.Start(run)!;
        app.WaitForExit();
        return app.ExitCode;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunDaemon(string solutionPath)
    {
        // The daemon has no terminal: what it prints, crashes included, goes to its log.
        var log = DaemonLog(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine($"{DateTime.Now:O} {e.ExceptionObject}");

        // Before anything JIT-compiles a method that mentions a Roslyn type.
        var sdkDirectory = SdkRoslyn.Install(Path.GetDirectoryName(solutionPath)!);
        return Daemon.Run(solutionPath, SocketPath(solutionPath), sdkDirectory);
    }

    static int Send(string solutionPath, string command)
    {
        var socketPath = SocketPath(solutionPath);
        var connected = Connect(socketPath);
        // Only a new daemon picks an SDK, so warm commands skip the extra `dotnet --version`.
        if (connected is null && command != "stop" && SdkRoslyn.Unsupported(Path.GetDirectoryName(solutionPath)!) is { } problem)
        {
            Console.Error.WriteLine($"blaze-cs: {problem}");
            return 1;
        }
        using var socket = connected ?? StartDaemon(solutionPath, socketPath);
        if (socket is null)
        {
            if (command == "stop")
                return 0;
            Console.Error.WriteLine($"blaze-cs: the daemon did not start; see {DaemonLog(solutionPath)}");
            return 1;
        }

        using var stream = new NetworkStream(socket);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var reader = new StreamReader(stream);
        writer.WriteLine(JsonSerializer.Serialize(new Request(command)));
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(Daemon.ExitPrefix, StringComparison.Ordinal))
                return int.Parse(line[Daemon.ExitPrefix.Length..]);
            Console.WriteLine(line);
        }
        Console.Error.WriteLine("blaze-cs: lost the daemon mid-command");
        return 1;
    }

    static Socket? Connect(string socketPath)
    {
        if (!File.Exists(socketPath))
            return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    static Socket? StartDaemon(string solutionPath, string socketPath)
    {
        File.Delete(socketPath);
        var log = DaemonLog(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);

        // Detached into its own session so it outlives this terminal and ignores its Ctrl+C. The daemon
        // writes its log itself; the redirect only catches what the runtime prints when it dies.
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("setsid \"$0\" exec --runtimeconfig \"$1\" \"$2\" daemon \"$3\" </dev/null >>\"$4\" 2>&1 &");
        start.ArgumentList.Add(Environment.ProcessPath!);
        start.ArgumentList.Add(SdkRoslyn.CompilerRuntimeConfig(Path.GetDirectoryName(solutionPath)!));
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(solutionPath);
        start.ArgumentList.Add(log);
        // The daemon lives for many builds of a large heap: server GC, and the JIT straight to optimized
        // code for hot compiler paths. Measured on jellyfin, a body edit's compile went from ~1 s to ~0.3 s.
        start.Environment["DOTNET_gcServer"] = "1";
        start.Environment["DOTNET_TieredPGO"] = "0";
        start.Environment["DOTNET_TC_CallCountingDelayMs"] = "0";
        Process.Start(start)!.WaitForExit();

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (Connect(socketPath) is { } socket)
                return socket;
            Thread.Sleep(20);
        }
        return null;
    }

    static string DaemonLog(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(solutionPath)!, ".blaze-cs", "daemon.log");

    /// <summary>Per solution, under the runtime directory: socket paths must stay short.</summary>
    static string SocketPath(string solutionPath)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(solutionPath)))[..16];
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath();
        return Path.Combine(directory, $"blaze-cs-{hash}.sock");
    }

    static string? FindSolution(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var solutions = current.EnumerateFiles("*.sln").Concat(current.EnumerateFiles("*.slnx")).ToList();
            if (solutions.Count == 1)
                return solutions[0].FullName;
            if (solutions.Count > 1)
            {
                Console.Error.WriteLine($"blaze-cs: more than one solution in {current.FullName}");
                return null;
            }
        }
        return null;
    }
}

sealed record Request(string Command);
