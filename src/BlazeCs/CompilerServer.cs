using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace BlazeCs;

/// <summary>
/// The daemon's end of the Roslyn compiler-server protocol, on the pipe the SDK's Csc task sends each compile to
/// when no SharedCompilationId names another: the pipe the SDK's own server (VBCSCompiler) listens on, named for
/// the user and the SDK's compiler directory. So plain dotnet build, dotnet test and IDE builds compile through
/// the daemon. That pipe is the same for every solution built with the SDK. The daemon answers its own
/// solution's compiles (<c>serve</c>) and passes every other compile on, byte for byte, to a VBCSCompiler of
/// the same SDK that it starts on a private pipe: those builds get what the SDK's server would give them.
/// Each request is handled on a thread of its own; a broken one is dropped, and the task then compiles with
/// csc itself.
/// </summary>
/// <remarks>
/// The pipe has one server at a time: whoever holds its server mutex (<c>Global\&lt;pipe&gt;.server</c>), which
/// is what a client checks before it connects, and what a VBCSCompiler checks before it listens. The daemon takes
/// the pipe at its start and before each command (<see cref="Take"/>): if another server holds it, a
/// VBCSCompiler or another solution's daemon, it asks that one to shut down, as `dotnet build-server shutdown`
/// does, and takes the mutex once it is given up. Asked to shut down itself, the daemon gives the pipe up and
/// keeps running. It gives it up when it exits too, and the next build starts the SDK's server as usual.
/// </remarks>
sealed class CompilerServer : IDisposable
{
    /// <summary>BLAZE_CS_COMPILER_SERVER=0 in the environment the daemon starts in turns the pipe off.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("BLAZE_CS_COMPILER_SERVER") != "0";

    /// <summary>
    /// The pipe the Csc task of the SDK whose compiler is in <paramref name="clientDirectory"/> (its
    /// Roslyn/bincore) connects to, as Roslyn's BuildServerConnection.GetPipeName names it: SHA-256 of
    /// "user.admin.directory", the directory without a trailing separator and lower-cased, in base64 with '/'
    /// as '_' and no padding. admin is True only for an elevated process on Windows.
    /// </summary>
    public static string PipeName(string clientDirectory)
    {
        var admin = false;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        var input = $"{Environment.UserName}.{admin}.{clientDirectory.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()}";
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(input))).Replace("/", "_").Replace("=", "");
    }

    /// <summary>
    /// The private pipe of the VBCSCompiler that compiles what the daemon passes on: one per SDK pipe, so the
    /// next daemon of any solution finds it warm.
    /// </summary>
    public static string PrivatePipeName(string pipeName) =>
        "blaze-cs-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pipeName)))[..16];

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
    public static string PipePath(string pipeName) => OperatingSystem.IsWindows() ? pipeName : Path.Combine("/tmp", pipeName);

    static string ServerMutexName(string pipeName) => $@"Global\{pipeName}.server";

    /// <summary>Whether a server holds the pipe (BuildServerConnection.WasServerMutexOpen).</summary>
    public static bool Held(string pipeName)
    {
        try
        {
            var open = Mutex.TryOpenExisting(ServerMutexName(pipeName), out var mutex);
            mutex?.Dispose();
            return open;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    readonly string _pipeName;
    readonly string _privatePipeName;
    readonly string _clientDirectory;
    readonly string _compilerHash;
    readonly string _dotnet;
    readonly Func<ServerRequest, ServerResponse?> _serve;
    readonly Lock _gate = new();
    readonly Lock _starting = new();
    Listening? _listening;
    Task _taking = Task.CompletedTask;
    bool _disposed;
    long _lastRequest = DateTime.UtcNow.Ticks;

    /// <param name="clientDirectory">The SDK's Roslyn/bincore, where its csc and VBCSCompiler are.</param>
    /// <param name="compilerHash">The commit of the SDK's Roslyn, which every request must carry.</param>
    /// <param name="dotnet">The dotnet host that starts VBCSCompiler.</param>
    /// <param name="serve">
    /// Answers a compile of the daemon's solution, Rejected for one csc must do (VBCSCompiler then compiles
    /// it); null for a compile of another solution.
    /// </param>
    public CompilerServer(string clientDirectory, string compilerHash, string dotnet, Func<ServerRequest, ServerResponse?> serve)
    {
        _clientDirectory = clientDirectory;
        _pipeName = PipeName(clientDirectory);
        _privatePipeName = PrivatePipeName(_pipeName);
        _compilerHash = compilerHash;
        _dotnet = dotnet;
        _serve = serve;
    }

    public string Name => _pipeName;

    /// <summary>When the last compile of the daemon's solution arrived: the daemon stays up while builds use it.</summary>
    public DateTime LastRequest => new(Interlocked.Read(ref _lastRequest), DateTimeKind.Utc);

    static void Log(string message) => Console.WriteLine($"{DateTime.Now:O} compiler server: {message}");

    /// <summary>
    /// Takes the pipe unless the daemon holds it: at once if no server does, else after asking the one that
    /// does to shut down. Runs in the background; the task ends once the pipe is the daemon's or stays another's.
    /// </summary>
    public Task Take()
    {
        lock (_gate)
        {
            if (_disposed || _listening is not null || !_taking.IsCompleted)
                return _taking;
            return _taking = Task.Run(TakeOver);
        }
    }

    void TakeOver()
    {
        Mutex mutex;
        bool created;
        try { mutex = new Mutex(initiallyOwned: false, ServerMutexName(_pipeName), out created); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Log($"no mutex for {_pipeName} ({e.Message}); builds compile without the daemon");
            return;
        }
        // Another server has the pipe: a VBCSCompiler a build started while no daemon held it, or another
        // solution's daemon. The daemon keeps the mutex open meanwhile, so a client in between finds a server and
        // connects (or, while none listens, compiles with csc) instead of starting another VBCSCompiler, and a
        // VBCSCompiler started anyway finds the mutex taken and exits.
        if (!created && GiveUp(_pipeName) is { } kept)
        {
            mutex.Dispose();
            Log($"{_pipeName} stays with another server: {kept}; the next blaze-cs command asks again");
            return;
        }
        lock (_gate)
        {
            if (_disposed)
            {
                mutex.Dispose();
                return;
            }
            _listening = new Listening(this, mutex);
        }
        Log($"listening on {_pipeName}, the pipe of the SDK's Csc task (Roslyn {_compilerHash}): dotnet build compiles through this daemon");
    }

    /// <summary>
    /// Asks the server on the pipe to shut down, as `dotnet build-server shutdown` does, and waits until it has
    /// stopped listening: a VBCSCompiler exits once its compiles finish, and a daemon stops listening and keeps
    /// running. Returns why it didn't, or null.
    /// </summary>
    string? GiveUp(string pipeName)
    {
        var answer = Send(pipeName, ServerRequest.Shutdown(_compilerHash), TimeSpan.FromSeconds(5));
        Log($"another server holds {pipeName}; asked it to shut down: {Describe(answer)}");
        if (answer is not ServerResponse.Shutdown { ServerProcessId: var pid })
            return Describe(answer);
        Process? server = null;
        try { server = Process.GetProcessById(pid); }
        catch (ArgumentException) { } // gone already
        using (server)
        {
            // On Unix its socket goes with its listener; on Windows its pipe instances do.
            bool Stopped() => server is null || server.HasExited
                || (OperatingSystem.IsWindows() ? !Answers(pipeName) : !File.Exists(PipePath(pipeName)));
            if (!WaitUntil(Stopped, TimeSpan.FromSeconds(60)))
                return $"its server (pid {pid}) still listens 60 s after a shutdown request";
        }
        return null;
    }

    /// <summary>Whether a server accepts a connection on the pipe within a moment.</summary>
    static bool Answers(string pipeName)
    {
        using var client = new NamedPipeClientStream(".", PipePath(pipeName), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect(100);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > until)
                return false;
            Thread.Sleep(20);
        }
        return true;
    }

    /// <summary>Gives the pipe up: no more connections, and the mutex goes, so a client starts the SDK's server.</summary>
    void Release()
    {
        Listening? listening;
        lock (_gate)
        {
            listening = _listening;
            _listening = null;
        }
        if (listening is null)
            return;
        listening.Dispose();
        Log($"gave up {_pipeName}");
    }

    /// <summary>One connection at a time on each of a few pending server ends, while the daemon holds the pipe.</summary>
    sealed class Listening : IDisposable
    {
        readonly CancellationTokenSource _stopping = new();
        readonly Task[] _listeners;
        readonly Mutex _mutex;

        public Listening(CompilerServer server, Mutex mutex)
        {
            _mutex = mutex;
            // As many pending connections as the SDK's server keeps, so parallel project builds don't queue.
            _listeners = [.. Enumerable.Range(0, Math.Min(4, Environment.ProcessorCount)).Select(_ => Task.Run(() => server.Listen(_stopping.Token)))];
        }

        /// <summary>Stops listening; requests being served still get their answers while the daemon runs.</summary>
        public void Dispose()
        {
            _stopping.Cancel();
            try { Task.WaitAll(_listeners, TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
            _mutex.Dispose();
        }
    }

    async Task Listen(CancellationToken token)
    {
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
                Log($"listening on {_pipeName}: {e.Message}");
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
                if (!string.Equals(request.CompilerHash, _compilerHash, StringComparison.OrdinalIgnoreCase))
                {
                    // The SDK's server answers IncorrectHash, which fails the build; Rejected has the task run csc.
                    var reason = $"compiler hash {request.CompilerHash} is not that of the Roslyn this daemon loaded ({_compilerHash})";
                    Log($"{request.RequestId}: rejected: {reason}");
                    new ServerResponse.Rejected(reason).Write(client);
                    return;
                }
                if (request.IsShutdown)
                {
                    // `dotnet build-server shutdown`, or another daemon taking the pipe: that waits until the
                    // mutex goes. The VBCSCompiler compiles were passed to goes too.
                    Log("shutdown request (dotnet build-server shutdown, or another daemon): giving the pipe up until the next blaze-cs command");
                    new ServerResponse.Shutdown(Environment.ProcessId).Write(client);
                    Release();
                    ShutDownPrivateServer();
                    return;
                }

                var started = Stopwatch.GetTimestamp();
                var response = _serve(request);
                if (response is not null)
                    Interlocked.Exchange(ref _lastRequest, DateTime.UtcNow.Ticks);
                try { Answer(); }
                finally
                {
                    // Idle time counts from the answer, as it does from a command's end.
                    if (response is not null)
                        Interlocked.Exchange(ref _lastRequest, DateTime.UtcNow.Ticks);
                }

                void Answer()
                {
                    if (response is ServerResponse.Rejected or null)
                    {
                        // What csc must compile, VBCSCompiler compiles, as it would if the daemon didn't hold the pipe.
                        var (raw, forwarded) = Forward(request);
                        Log($"{request.RequestId}: {(response is null ? "not of this solution" : "rejected")}; VBCSCompiler: {Describe(forwarded)} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
                        if (raw is not null)
                            client.Write(raw);
                        else
                            forwarded.Write(client);
                        return;
                    }
                    response.Write(client);
                }
            }
            catch (Exception e)
            {
                // The client gave up, or sent something else; it compiles on its own.
                Log($"{e.GetType().Name}: {e.Message}");
            }
        }
    }

    static string Describe(ServerResponse response) => response switch
    {
        ServerResponse.Completed completed => $"compiled, exit code {completed.ReturnCode}",
        ServerResponse.Rejected rejected => $"rejected: {rejected.Reason}",
        ServerResponse.Shutdown shutdown => $"shutting down (pid {shutdown.ServerProcessId})",
        var other => other.Type.ToString(),
    };

    /// <summary>
    /// Passes a request to the private VBCSCompiler, started if none listens there, and returns its response
    /// as it came. If that fails, a Rejected response: the task runs csc.
    /// </summary>
    (byte[]? Raw, ServerResponse Response) Forward(ServerRequest request)
    {
        try
        {
            using var pipe = ConnectToPrivateServer();
            if (pipe is null)
                return (null, new ServerResponse.Rejected($"no VBCSCompiler on {_privatePipeName}"));
            request.Write(pipe);
            var raw = ServerMessage.ReadMessage(pipe);
            return (raw, ServerResponse.Read(new MemoryStream(raw)));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or TimeoutException)
        {
            return (null, new ServerResponse.Rejected($"VBCSCompiler on {_privatePipeName}: {e.Message}"));
        }
    }

    NamedPipeClientStream? ConnectToPrivateServer()
    {
        // Started once for the compiles that find it missing together; another one would exit for the mutex.
        var timeout = TimeSpan.FromSeconds(5);
        lock (_starting)
        {
            if (!Held(_privatePipeName))
            {
                StartPrivateServer();
                timeout = TimeSpan.FromSeconds(20);
            }
        }
        var pipe = new NamedPipeClientStream(".", PipePath(_privatePipeName), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(timeout);
            return pipe;
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            pipe.Dispose();
            return null;
        }
    }

    /// <summary>
    /// VBCSCompiler on the private pipe, as the Csc task starts it (BuildServerConnection.TryCreateServer): in the
    /// compiler's directory, with nothing to read or write. It exits after its own idle timeout.
    /// </summary>
    void StartPrivateServer()
    {
        var start = new ProcessStartInfo(_dotnet)
        {
            WorkingDirectory = _clientDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "exec", Path.Combine(_clientDirectory, "VBCSCompiler.dll"), $"-pipename:{_privatePipeName}" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        Log($"started VBCSCompiler on {_privatePipeName} (pid {process?.Id}) for the compiles the daemon passes on");
    }

    void ShutDownPrivateServer()
    {
        if (Held(_privatePipeName))
            Send(_privatePipeName, ServerRequest.Shutdown(_compilerHash), TimeSpan.FromSeconds(1));
    }

    /// <summary>Sends a request to a pipe's server and reads its answer; Rejected if there was none.</summary>
    static ServerResponse Send(string pipeName, ServerRequest request, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipePath(pipeName), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeout);
            request.Write(pipe);
            return ServerResponse.Read(pipe);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or TimeoutException)
        {
            return new ServerResponse.Rejected($"no answer: {e.Message}");
        }
    }

    /// <summary>Gives the pipe up and shuts the private VBCSCompiler down; compiles in flight still get their answers.</summary>
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Release();
        ShutDownPrivateServer();
    }
}
