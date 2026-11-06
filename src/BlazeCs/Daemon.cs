using System.Net.Sockets;
using System.Text.Json;

namespace BlazeCs;

/// <summary>
/// Serves one solution over a Unix socket, one command at a time. Each command streams its output
/// lines back and ends with an exit-code line. A daemon that serves no command for two hours exits,
/// and one asked by a client from another build of blaze-cs hands over: it answers with its process id
/// and exits, and the client starts a daemon of its own build.
/// </summary>
static class Daemon
{
    /// <summary>The identity of this build of blaze-cs: clients send theirs with each command.</summary>
    public static readonly Guid Build = typeof(Daemon).Module.ModuleVersionId;

    /// <summary>BLAZE_CS_IDLE_SECONDS lets the fixture check see an idle exit without waiting two hours.</summary>
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("BLAZE_CS_IDLE_SECONDS"), out var seconds) && seconds > 0 ? seconds : 2 * 60 * 60);

    public static int Run(string solutionPath, string socketPath, string sdkDirectory)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        File.Delete(socketPath);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(16);
        Console.WriteLine($"{DateTime.Now:O} serving {solutionPath} on {socketPath} (pid {Environment.ProcessId}, exits after {IdleTimeout.TotalSeconds:F0} s idle)");

        // Projects compile in parallel and Roslyn parallelizes inside each one; both block on the
        // shared thread pool, which otherwise adds threads one at a time while cores sit idle.
        ThreadPool.GetMinThreads(out _, out var completionThreads);
        ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, completionThreads);

        var builder = new SolutionBuilder(solutionPath, sdkDirectory);
        using var tests = new TestRunner(builder);
        var loaded = false;

        while (true)
        {
            Socket accepted;
            using (var idle = new CancellationTokenSource(IdleTimeout))
            {
                try { accepted = listener.AcceptAsync(idle.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"{DateTime.Now:O} no command for {IdleTimeout.TotalSeconds:F0} s; exiting");
                    File.Delete(socketPath);
                    return 0;
                }
            }
            using var client = accepted;
            using var stream = new NetworkStream(client);
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };

            var request = JsonSerializer.Deserialize<Request>(reader.ReadLine() ?? "null");
            void Log(string line)
            {
                lock (writer)
                {
                    try { writer.WriteLine(line); }
                    catch (IOException) { } // the client went away; keep building
                }
            }

            int exitCode;
            try
            {
                // The socket goes before the reply: once the client has its answer it may start a daemon that
                // binds the same path, and a later delete would remove that daemon's socket.
                if (request?.Command == "stop")
                {
                    File.Delete(socketPath);
                    Log("blaze-cs: daemon stopped");
                    writer.WriteLine($"{DaemonClient.ExitPrefix}0");
                    return 0;
                }

                if (request?.Build != Build)
                {
                    Console.WriteLine($"{DateTime.Now:O} a client of another build sent {request?.Command}; exiting");
                    File.Delete(socketPath);
                    writer.WriteLine($"{DaemonClient.RestartPrefix}{Environment.ProcessId}");
                    return 0;
                }

                // Loading runs the first `dotnet build`, so its output belongs to the first command. That build
                // is the command's build: the command returns once the solution is loaded (tests run on what
                // dotnet build wrote), and the warm-up runs on until the next command waits for it.
                if (!loaded)
                {
                    loaded = builder.Load(Log, warmUpLater: true);
                    if (loaded)
                        tests.Prestart();
                    exitCode = !loaded ? 1 : request?.Command switch
                    {
                        "build" => 0,
                        "test" => tests.Run(request is { All: true }, Log),
                        _ => 2,
                    };
                }
                else
                {
                    exitCode = request?.Command switch
                    {
                        "build" => builder.Build(Log) ? 0 : 1,
                        "test" => builder.Build(Log) ? tests.Run(request is { All: true }, Log) : 1,
                        _ => 2,
                    };
                }
                // A build may have reloaded the solution, and a run took hosts.
                if (loaded)
                    tests.Prestart();
            }
            catch (Exception e)
            {
                Console.WriteLine($"{DateTime.Now:O} {e}");
                Log($"blaze-cs: internal error: {e.Message} (details in .blaze-cs/daemon.log)");
                exitCode = 1;
            }

            try { writer.WriteLine($"{DaemonClient.ExitPrefix}{exitCode}"); }
            catch (IOException) { }
        }
    }
}
