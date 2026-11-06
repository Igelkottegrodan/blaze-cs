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
        var builder = new SolutionBuilder(solutionPath, sdkDirectory);
        var collector = new IdleCollector(IdleCollector.DefaultDelay, IdleCollector.CollectAndLog);
        // Commands and compiles through the pipe overlap: the idle collection waits for the last of them to end.
        var busy = 0;
        void Busy()
        {
            lock (collector)
            {
                if (busy++ == 0)
                    collector.Busy();
            }
        }
        void Idle()
        {
            lock (collector)
            {
                if (--busy == 0)
                    collector.Idle(builder.Idling);
            }
        }
        CompilerServer? server = null;
        if (CompilerServer.Enabled)
        {
            // The pipe and commit hash are those of the SDK's own server (VBCSCompiler, beside the compiler the
            // daemon loaded), which the Csc task derives from that directory and sends with each compile. The task
            // runs in MSBuild, which the dotnet host starts from the SDK's real path: through a symlinked SDK
            // directory the daemon's path to it would name another pipe.
            var bincore = BuildLog.RealPath(Path.GetDirectoryName(typeof(Microsoft.CodeAnalysis.Compilation).Assembly.Location)!, []);
            var compilerHash = CompilerServer.CommitHash(Path.Combine(bincore, "VBCSCompiler.dll")) ?? "";
            server = new CompilerServer(bincore, compilerHash, Program.DotnetHost(), request =>
            {
                Busy();
                try { return builder.Serve(request); }
                finally { Idle(); }
            });
            Console.WriteLine($"{DateTime.Now:O} compiler server: pipe {server.Name} of {bincore}");
        }
        else
            Console.WriteLine($"{DateTime.Now:O} compiler server off (BLAZE_CS_COMPILER_SERVER=0)");

        // Before any reply that ends this daemon: once the client has its answer it may start a daemon that
        // binds the same path, and disposing the listener after that deletes the file by its path too,
        // leaving that daemon unreachable (the next command starts another beside it). The pipe goes with it.
        void CloseSocket()
        {
            listener.Dispose();
            File.Delete(socketPath);
            server?.Dispose();
        }

        // Projects compile in parallel and Roslyn parallelizes inside each one; both block on the
        // shared thread pool, which otherwise adds threads one at a time while cores sit idle.
        ThreadPool.GetMinThreads(out _, out var completionThreads);
        ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, completionThreads);

        using var tests = new TestRunner(builder);
        var loaded = false;
        DateTime lastCommand;

        while (true)
        {
            // Idle time counts from the end of the last command: a long one (a first load) doesn't use it up.
            lastCommand = DateTime.UtcNow;
            Socket? accepted = null;
            while (accepted is null)
            {
                // Builds that compile through the pipe keep the daemon up too.
                var lastUse = server is null || server.LastRequest < lastCommand ? lastCommand : server.LastRequest;
                var left = lastUse + IdleTimeout - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    Console.WriteLine($"{DateTime.Now:O} no command for {IdleTimeout.TotalSeconds:F0} s; exiting");
                    CloseSocket();
                    return 0;
                }
                using var idle = new CancellationTokenSource(left);
                try { accepted = listener.AcceptAsync(idle.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
            // First of all, so a command never waits for it; then a collection already running ends first.
            builder.CancelBackgroundAnalysis();
            Busy();
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
            // Each command takes the pipe back if `dotnet build-server shutdown` or another daemon took it, and
            // returns once it has it (or tried), so a dotnet build right after compiles through this daemon.
            var taking = request is { Command: "build" or "test" } && request.Build == Build ? server?.Take() : null;
            // Released before the exit code goes out: a build that compiles through the pipe right after the
            // command returns must find the daemon free.
            var command = builder.Command();
            try
            {
                // The socket goes before the reply (see CloseSocket).
                if (request?.Command == "stop")
                {
                    CloseSocket();
                    Log("blaze-cs: daemon stopped");
                    writer.WriteLine($"{DaemonClient.ExitPrefix}0");
                    return 0;
                }

                if (request?.Build != Build)
                {
                    Console.WriteLine($"{DateTime.Now:O} a client of another build sent {request?.Command}; exiting");
                    CloseSocket();
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
                        // --all reports every analyzer's warnings, so it waits for the warm-up.
                        "build" => request is not { All: true } || builder.Build(Log, all: true) ? 0 : 1,
                        "test" => tests.Run(request is { All: true }, Log),
                        _ => 2,
                    };
                }
                else
                {
                    exitCode = request?.Command switch
                    {
                        "build" => builder.Build(Log, all: request is { All: true }) ? 0 : 1,
                        "test" => builder.Build(Log) ? tests.Run(request is { All: true }, Log) : 1,
                        _ => 2,
                    };
                }
                // After the build, which may have changed what it analyzed. build --all reported all of it.
                if (loaded && request is { Command: "build" or "test" } and not { Command: "build", All: true })
                    builder.ReportBackgroundAnalysis(Log);
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
            finally
            {
                command.Dispose();
                taking?.Wait(TimeSpan.FromSeconds(10));
            }

            try { writer.WriteLine($"{DaemonClient.ExitPrefix}{exitCode}"); }
            catch (IOException) { }
            if (loaded)
                builder.AnalyzeWhenIdle();
            // Once the idle work ends: the warm-up, then the background analysis, which allocates heavily.
            Idle();
        }
    }
}
