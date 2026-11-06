using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace BlazeCs;

/// <summary>
/// The client's side of the daemon protocol: find the solution, connect to its socket, send one command
/// and stream the output. Compiled into both the CLI and the native client (BlazeCs.Client), so it must
/// stay free of reflection and of Roslyn types, which Native AOT cannot take.
/// </summary>
static class DaemonClient
{
    /// <summary>Ends every command's output: this prefix and the exit code.</summary>
    public const string ExitPrefix = "\u0001exit:";

    /// <summary>A daemon of another build answers with this prefix and its process id, and exits.</summary>
    public const string RestartPrefix = "\u0001restart:";

    /// <summary>The solution in this directory or the nearest parent, or null after saying why there is none.</summary>
    public static string? FindSolution(string directory)
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
        Console.Error.WriteLine("blaze-cs: no .sln or .slnx file here or in any parent directory");
        return null;
    }

    /// <summary>Per solution, under the runtime directory: socket paths must stay short.</summary>
    public static string SocketPath(string solutionPath)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(solutionPath)))[..16];
        var directory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath();
        return Path.Combine(directory, $"blaze-cs-{hash}.sock");
    }

    public static Socket? Connect(string socketPath)
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

    /// <summary>
    /// Sends the command with the identity of the blaze-cs build the daemon must be, and relays the output
    /// as it arrives. A daemon of another build answers with its process id instead (otherBuildProcess)
    /// and exits.
    /// </summary>
    public static int Exchange(Socket socket, string command, Guid build, out int? otherBuildProcess)
    {
        otherBuildProcess = null;
        using var stream = new NetworkStream(socket);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var reader = new StreamReader(stream);
        // Written by hand, not with JsonSerializer: reflection-based serialization costs ~30 ms of JIT at
        // start-up, and the command is one of a few fixed words.
        writer.WriteLine($"{{\"Command\":\"{command}\",\"Build\":\"{build}\"}}");
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(ExitPrefix, StringComparison.Ordinal))
                return int.Parse(line[ExitPrefix.Length..]);
            if (line.StartsWith(RestartPrefix, StringComparison.Ordinal))
            {
                otherBuildProcess = int.Parse(line[RestartPrefix.Length..]);
                return 0;
            }
            Console.WriteLine(line);
        }
        Console.Error.WriteLine("blaze-cs: lost the daemon mid-command");
        return 1;
    }

    /// <summary>Says a daemon of another build is being replaced, and waits for it to go.</summary>
    public static void WaitForRestart(int processId)
    {
        Console.Error.WriteLine("blaze-cs: the running daemon is from another build of blaze-cs; restarting it");
        // So the new daemon doesn't start beside one still shutting down.
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException) { } // already gone
    }
}
