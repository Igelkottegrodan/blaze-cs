using System.Net.Sockets;
using System.Text.Json;

namespace BlazeCs;

/// <summary>
/// Serves one solution over a Unix socket, one command at a time. Each command streams its output
/// lines back and ends with an exit-code line.
/// </summary>
static class Daemon
{
    public const string ExitPrefix = "\u0001exit:";

    public static int Run(string solutionPath, string socketPath, string sdkDirectory)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        File.Delete(socketPath);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(16);
        Console.WriteLine($"{DateTime.Now:O} serving {solutionPath} on {socketPath}");

        // Projects compile in parallel and Roslyn parallelizes inside each one; both block on the
        // shared thread pool, which otherwise adds threads one at a time while cores sit idle.
        ThreadPool.GetMinThreads(out _, out var completionThreads);
        ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, completionThreads);

        var builder = new SolutionBuilder(solutionPath, sdkDirectory);
        using var tests = new TestRunner(builder);
        var loaded = false;

        while (true)
        {
            using var client = listener.Accept();
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
                if (request?.Command == "stop")
                {
                    Log("blaze-cs: daemon stopped");
                    writer.WriteLine($"{ExitPrefix}0");
                    File.Delete(socketPath);
                    return 0;
                }

                // Loading runs the first `dotnet build`, so its output belongs to the first command.
                if (!loaded)
                    loaded = builder.Load(Log);
                exitCode = !loaded ? 1 : request?.Command switch
                {
                    "build" => builder.Build(Log).Success ? 0 : 1,
                    "test" => builder.Build(Log) is { Success: true } result ? tests.Run(result.Changed, Log) : 1,
                    _ => 2,
                };
            }
            catch (Exception e)
            {
                Console.WriteLine($"{DateTime.Now:O} {e}");
                Log($"blaze-cs: internal error: {e.Message} (details in .blaze-cs/daemon.log)");
                exitCode = 1;
            }

            try { writer.WriteLine($"{ExitPrefix}{exitCode}"); }
            catch (IOException) { }
        }
    }
}
