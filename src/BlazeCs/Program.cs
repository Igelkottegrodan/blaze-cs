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
        // The daemon logs to a capped file of its own; what the launcher redirects to StartLog is only
        // what comes before this or around it, such as a runtime that can't start.
        var log = new DaemonLog(DaemonLogPath(solutionPath));
        Console.SetOut(log);
        Console.SetError(log);
        try
        {
            // Before anything JIT-compiles a method that mentions a Roslyn type.
            var sdkDirectory = SdkRoslyn.Install(Path.GetDirectoryName(solutionPath)!);
            return Daemon.Run(solutionPath, SocketPath(solutionPath), sdkDirectory);
        }
        catch (Exception e)
        {
            Console.WriteLine($"{DateTime.Now:O} {e}");
            return 1;
        }
    }

    static int Send(string solutionPath, string command)
    {
        var socketPath = SocketPath(solutionPath);
        var socket = Connect(socketPath);
        for (var restarted = false; ; restarted = true)
        {
            if (socket is null)
            {
                if (command == "stop")
                    return 0;
                // Only a new daemon picks an SDK, so warm commands skip the extra `dotnet --version`.
                if (SdkRoslyn.Unsupported(Path.GetDirectoryName(solutionPath)!) is { } problem)
                {
                    Console.Error.WriteLine($"blaze-cs: {problem}");
                    return 1;
                }
                socket = StartDaemon(solutionPath, socketPath);
                if (socket is null)
                {
                    Console.Error.WriteLine($"blaze-cs: the daemon did not start; see {DaemonLogPath(solutionPath)} and {StartLogPath(solutionPath)}");
                    return 1;
                }
            }

            using (socket)
            {
                var exitCode = Exchange(socket, command, out var otherBuild);
                if (otherBuild is not { } daemonProcess)
                    return exitCode;
                if (restarted)
                {
                    Console.Error.WriteLine("blaze-cs: the new daemon is from another build too");
                    return 1;
                }
                Console.Error.WriteLine("blaze-cs: the running daemon is from another build of blaze-cs; restarting it");
                WaitForExit(daemonProcess);
            }
            socket = null;
        }
    }

    /// <summary>
    /// Sends the command and relays the output. A daemon of another build answers with its process id
    /// instead, and exits.
    /// </summary>
    static int Exchange(Socket socket, string command, out int? otherBuildProcess)
    {
        otherBuildProcess = null;
        using var stream = new NetworkStream(socket);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var reader = new StreamReader(stream);
        writer.WriteLine(JsonSerializer.Serialize(new Request(command, Daemon.Build)));
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(Daemon.ExitPrefix, StringComparison.Ordinal))
                return int.Parse(line[Daemon.ExitPrefix.Length..]);
            if (line.StartsWith(Daemon.RestartPrefix, StringComparison.Ordinal))
            {
                otherBuildProcess = int.Parse(line[Daemon.RestartPrefix.Length..]);
                return 0;
            }
            Console.WriteLine(line);
        }
        Console.Error.WriteLine("blaze-cs: lost the daemon mid-command");
        return 1;
    }

    /// <summary>So the new daemon doesn't start beside one still shutting down.</summary>
    static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException) { } // already gone
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
        var log = StartLogPath(solutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        DeleteWhenReleased(log);

        // The daemon lives for many builds of a large heap: server GC, and the JIT straight to optimized
        // code for hot compiler paths. Measured on jellyfin, a body edit's compile went from ~1 s to ~0.3 s.
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_gcServer"] = "1",
            ["DOTNET_TieredPGO"] = "0",
            ["DOTNET_TC_CallCountingDelayMs"] = "0",
        };
        string[] arguments = ["exec", "--runtimeconfig", SdkRoslyn.CompilerRuntimeConfig(Path.GetDirectoryName(solutionPath)!),
            typeof(Program).Assembly.Location, "daemon", solutionPath];
        if (OperatingSystem.IsWindows())
            StartDetachedOnWindows([Environment.ProcessPath!, .. arguments], log, environment);
        else
        {
            // Detached into its own session so it outlives this terminal and ignores its Ctrl+C.
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("log=$1; shift; setsid \"$@\" </dev/null >>\"$log\" 2>&1 &");
            foreach (var argument in new[] { "sh", log, Environment.ProcessPath! }.Concat(arguments))
                start.ArgumentList.Add(argument);
            foreach (var (name, value) in environment)
                start.Environment[name] = value;
            Process.Start(start)!.WaitForExit();
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (Connect(socketPath) is { } socket)
                return socket;
            Thread.Sleep(20);
        }
        return null;
    }

    /// <summary>
    /// Through ShellExecute, in a hidden console of its own: it gets none of this process's handles (a
    /// plain start would inherit the pipe a caller reads this process's output from, and that caller
    /// would wait for the daemon to exit), and this terminal's Ctrl+C doesn't reach it. cmd redirects
    /// its output into the log, as sh does on Linux. ShellExecute passes this process's environment, so
    /// the daemon's variables are set here for the start.
    /// </summary>
    static void StartDetachedOnWindows(string[] command, string log, Dictionary<string, string> environment)
    {
        // cmd /s /c "…": cmd strips the outer quotes and runs the rest as typed. Paths can't contain quotes.
        var line = string.Join(" ", command.Select(part => $"\"{part}\"")) + $" <NUL >>\"{log}\" 2>&1";
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{line}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        var previous = environment.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in environment)
            Environment.SetEnvironmentVariable(name, value);
        try { Process.Start(start)?.Dispose(); }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    static string DaemonLogPath(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(solutionPath)!, ".blaze-cs", "daemon.log");

    /// <summary>What the launcher redirects the daemon's own stdout and stderr to; see DaemonLog.</summary>
    static string StartLogPath(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(solutionPath)!, ".blaze-cs", "daemon-start.log");

    /// <summary>
    /// On Windows the launcher of a daemon that just exited can hold the file for a moment longer, and the
    /// next launcher can't open it meanwhile.
    /// </summary>
    static void DeleteWhenReleased(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

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

sealed record Request(string Command, Guid Build);
