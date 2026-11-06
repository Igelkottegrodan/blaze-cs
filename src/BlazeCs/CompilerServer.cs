using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace BlazeCs;

/// <summary>
/// The daemon's end of the Roslyn compiler-server protocol: a named pipe the SDK's Csc task sends each compile
/// to when a build names it with SharedCompilationId, served as the SDK's own server (VBCSCompiler) serves its
/// pipe. Each request is answered by <c>serve</c> on a thread of its own; a broken one is dropped, and the
/// task then compiles with csc itself.
/// </summary>
sealed class CompilerServer : IDisposable
{
    /// <summary>BLAZE_CS_COMPILER_SERVER=0 in the environment the daemon starts in turns the pipe off.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("BLAZE_CS_COMPILER_SERVER") != "0";

    /// <summary>
    /// The pipe a solution's daemon listens on: one per user and solution, the same across daemons, so a
    /// SharedCompilationId set once keeps working. The one place the name is chosen.
    /// </summary>
    public static string PipeName(string solutionPath)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}|{solutionPath}")))[..16];
        return $"blaze-cs-{hash}";
    }

    /// <summary>
    /// The commit an assembly was built from (its CommitHashAttribute), which the Csc task sends with every
    /// request and the SDK's server compares with its own: the two must be the same build of Roslyn.
    /// </summary>
    public static string? CommitHash(string assemblyPath)
    {
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            var type = attribute.Constructor.Kind switch
            {
                HandleKind.MethodDefinition => metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()).Name,
                HandleKind.MemberReference when metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
                    => metadata.GetTypeReference((TypeReferenceHandle)parent).Name,
                _ => default,
            };
            if (type.IsNil || !metadata.StringComparer.Equals(type, "CommitHashAttribute"))
                continue;
            var value = metadata.GetBlobReader(attribute.Value);
            return value.ReadUInt16() == 1 ? value.ReadSerializedString() : null; // after the prolog, the hash
        }
        return null;
    }

    /// <summary>
    /// Where the Csc task connects for a pipe name: on Unix a socket under /tmp, whatever TMPDIR says
    /// (Roslyn's NamedPipeUtil), and on Windows the name itself.
    /// </summary>
    static string PipePath(string pipeName) => OperatingSystem.IsWindows() ? pipeName : Path.Combine("/tmp", pipeName);

    static string MutexName(string pipeName) => $@"Global\{pipeName}.server";

    /// <summary>
    /// Starts serving the pipe, or returns null if another server keeps it. A build through the pipe while no
    /// daemon ran started the SDK's VBCSCompiler on it, which keeps it until idle: two servers on one pipe
    /// split its requests, and the one that outlives the other keeps the mutex but not the socket, which then
    /// fails every client. Such a server is asked to shut down, as `dotnet build-server shutdown` asks it, and
    /// waited for. The daemon opens the mutex first and holds it throughout: a client in between finds a
    /// server and connects (or, while none listens, compiles with csc) instead of starting another
    /// VBCSCompiler, and a VBCSCompiler started anyway finds the mutex taken and exits.
    /// </summary>
    /// <param name="log">Told which server was shut down, or why the pipe stays with it.</param>
    public static CompilerServer? Start(string pipeName, string compilerHash, Func<ServerRequest, ServerResponse> serve, Action<string> log)
    {
        Mutex? running = null;
        var otherServer = false;
        try
        {
            running = new Mutex(initiallyOwned: false, MutexName(pipeName), out var createdNew);
            otherServer = !createdNew;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            log($"no mutex for {pipeName} ({e.Message}); clients start VBCSCompiler first");
        }
        if (otherServer && Free(pipeName, compilerHash, log) is { } taken)
        {
            running?.Dispose();
            log($"off: pipe {pipeName} stays with another server: {taken}");
            return null;
        }
        return new CompilerServer(pipeName, running, serve);
    }

    /// <summary>Asks the server on the pipe to shut down and waits for it to exit; returns why it didn't, or null.</summary>
    static string? Free(string pipeName, string compilerHash, Action<string> log)
    {
        ServerResponse response;
        try
        {
            using var client = new NamedPipeClientStream(".", PipePath(pipeName), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(5000);
            ServerRequest.Shutdown(compilerHash).Write(client);
            response = ServerResponse.Read(client);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"a server holds its mutex and doesn't answer ({e.Message})";
        }
        if (response is not ServerResponse.Shutdown { ServerProcessId: var pid })
            return $"the server on it answered a shutdown request with {response.Type}" + (response is ServerResponse.Rejected { Reason: var reason } ? $" ({reason})" : "");
        // It exits once its compiles finish. Its mutex stays open (this daemon holds it), so its process tells.
        try
        {
            using var server = System.Diagnostics.Process.GetProcessById(pid);
            if (!server.WaitForExit(TimeSpan.FromSeconds(30)))
                return $"its server (pid {pid}) is still running 30 s after a shutdown request";
        }
        catch (ArgumentException) { } // gone already
        log($"shut down the server that held the pipe (pid {pid})");
        return null;
    }

    readonly string _pipeName;
    readonly Func<ServerRequest, ServerResponse> _serve;
    readonly CancellationTokenSource _stopping = new();
    readonly Task[] _listeners;
    readonly Mutex? _running;
    long _lastRequest = DateTime.UtcNow.Ticks;

    CompilerServer(string pipeName, Mutex? running, Func<ServerRequest, ServerResponse> serve)
    {
        _pipeName = pipeName;
        _serve = serve;
        // A client that finds no server's mutex starts VBCSCompiler for the pipe instead of connecting
        // (BuildServerConnection.RunServerBuildRequestAsync): holding it open says a server is running.
        _running = running;
        // As many pending connections as the SDK's server keeps, so parallel project builds don't queue.
        _listeners = [.. Enumerable.Range(0, Math.Min(4, Environment.ProcessorCount)).Select(_ => Task.Run(Listen))];
    }

    /// <summary>When the last request arrived: the daemon stays up while builds use it.</summary>
    public DateTime LastRequest => new(Interlocked.Read(ref _lastRequest), DateTimeKind.Utc);

    async Task Listen()
    {
        var token = _stopping.Token;
        var stopped = Task.Delay(Timeout.Infinite, token);
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipePath(_pipeName), PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly, 0x10000, 0x10000);
                var connected = pipe.WaitForConnectionAsync(token);
                // On Unix a pending wait ignores its token (Roslyn's NamedPipeClientConnectionHost does the same).
                await Task.WhenAny(connected, stopped);
                if (!connected.IsCompleted)
                {
                    _ = connected.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                }
                await connected;
                var client = pipe;
                pipe = null;
                _ = Task.Factory.StartNew(() => Handle(client), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Console.WriteLine($"{DateTime.Now:O} compiler server: listening on {_pipeName}: {e.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    void Handle(NamedPipeServerStream client)
    {
        using (client)
        {
            try
            {
                ServerRequest request;
                try { request = ServerRequest.Read(client); }
                catch (EndOfStreamException)
                {
                    return; // closed before a whole request: a probe for whether the server is up, or a client gone
                }
                Interlocked.Exchange(ref _lastRequest, DateTime.UtcNow.Ticks);
                _serve(request).Write(client);
                // Idle time counts from the answer, as it does from a command's end.
                Interlocked.Exchange(ref _lastRequest, DateTime.UtcNow.Ticks);
            }
            catch (Exception e)
            {
                // The client gave up, or sent something else; it compiles on its own.
                Console.WriteLine($"{DateTime.Now:O} compiler server: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>Stops listening; requests being served still get their answers while the daemon runs.</summary>
    public void Dispose()
    {
        _stopping.Cancel();
        try { Task.WaitAll(_listeners, TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        _running?.Dispose();
    }
}
