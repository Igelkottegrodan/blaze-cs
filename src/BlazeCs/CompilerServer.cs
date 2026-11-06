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
/// The pipe has one server at a time: whoever owns its server mutex (<c>Global\&lt;pipe&gt;.server</c>). A client
/// checks that the mutex exists before it connects, and a VBCSCompiler owns it while it listens. The daemon takes
/// the pipe at its start and before each command (<see cref="Take"/>): if another server owns it, a VBCSCompiler
/// or another solution's daemon, it asks that one to shut down, as `dotnet build-server shutdown` does, and waits
/// to own the mutex; of several daemons that ask at once, only the one that gets it listens. Asked to shut down
/// itself, the daemon gives the pipe up and keeps running. It gives it up when it exits too, and the next build
/// starts the SDK's server as usual.
/// </remarks>
sealed class CompilerServer : IDisposable
{
    /// <summary>
    /// The process a shutdown answer names when no process will exit: no pid Linux, macOS or Windows gives
    /// (Windows' are multiples of 4), so a client finds it not running and waits for nothing.
    /// </summary>
    const int NoProcess = int.MaxValue;

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
    /// The private pipe of the VBCSCompiler that compiles what the daemon passes on: one per SDK pipe, shared by
    /// the daemons of every solution, so the next one finds it warm. It exits after its own idle timeout, or on
    /// `dotnet build-server shutdown`; never when a daemon exits or another takes the SDK's pipe.
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
    Holding? _holding;
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

    /// <summary>How long a takeover waits for the server it asked to shut down to give the mutex up.</summary>
    internal TimeSpan TakeoverTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Whether the daemon owns the pipe and listens on it.</summary>
    internal bool IsListening => Volatile.Read(ref _holding) is not null;

    /// <summary>
    /// The request id of a daemon's shutdown request: the daemon that gets it gives the pipe up but leaves the
    /// private VBCSCompiler, which the asking daemon passes compiles on to as well.
    /// </summary>
    const string TakeoverRequestId = "blaze-cs takeover";

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
            if (_disposed || _holding is not null || !_taking.IsCompleted)
                return _taking;
            var taken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // A mutex is owned by a thread: this one owns it for as long as the daemon holds the pipe.
            new Thread(() =>
            {
                try { Hold(taken); }
                finally { taken.TrySetResult(); }
            }) { IsBackground = true, Name = "blaze-cs compiler-server mutex" }.Start();
            return _taking = taken.Task;
        }
    }

    /// <summary>
    /// Owns the server mutex, listens on the pipe and, once <see cref="Release"/> asks, releases the mutex again.
    /// Whoever owns the mutex is the pipe's server: of the daemons that ask its server to shut down at once, one
    /// gets it, and the others find it owned again and leave the pipe to that one.
    /// </summary>
    void Hold(TaskCompletionSource taken)
    {
        Mutex mutex;
        bool owned;
        try { mutex = new Mutex(initiallyOwned: true, ServerMutexName(_pipeName), out owned); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Log($"no mutex for {_pipeName} ({e.Message}); builds compile without the daemon");
            return;
        }
        Holding? holding = null;
        try
        {
            // Not created: another server owns it, a VBCSCompiler a build started while no daemon held the pipe, or
            // another solution's daemon; or a client checking for one has it open. Asked to shut down, a server
            // stops listening, then releases the mutex. Meanwhile it exists, so a client in between finds a server
            // and connects (or, while none listens, compiles with csc) instead of starting another VBCSCompiler.
            if (!owned && !(owned = Own(mutex, TimeSpan.Zero)))
            {
                var answer = Send(_pipeName, ServerRequest.Shutdown(_compilerHash, TakeoverRequestId), TimeSpan.FromSeconds(5));
                Log($"another server holds {_pipeName}; asked it to shut down: {Describe(answer)}");
                owned = Own(mutex, answer is ServerResponse.Shutdown ? TakeoverTimeout : TimeSpan.Zero);
                if (!owned)
                {
                    var why = answer is ServerResponse.Shutdown
                        ? $"its server, or a daemon that asked it at the same time, owns the mutex {TakeoverTimeout.TotalSeconds:F0} s after the shutdown request"
                        : Describe(answer);
                    Log($"{_pipeName} stays with another server: {why}; the next blaze-cs command asks again");
                    return;
                }
            }
            lock (_gate)
            {
                if (_disposed)
                    return;
                Volatile.Write(ref _holding, holding = new Holding(new Listening(this)));
            }
            Log($"listening on {_pipeName}, the pipe of the SDK's Csc task (Roslyn {_compilerHash}): dotnet build compiles through this daemon");
            taken.TrySetResult();
            holding.Give.Wait();
        }
        finally
        {
            if (owned)
                mutex.ReleaseMutex();
            mutex.Dispose();
            holding?.Given.Set();
        }
    }

    /// <summary>Waits to own the mutex; one its owner exited without releasing is this thread's too.</summary>
    static bool Own(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>The pipe while the daemon holds it: its listeners, and the thread that owns its mutex.</summary>
    sealed class Holding(Listening listening)
    {
        public Listening Listening { get; } = listening;

        /// <summary>Set when the mutex is to be released.</summary>
        public ManualResetEventSlim Give { get; } = new();

        /// <summary>Set once it is.</summary>
        public ManualResetEventSlim Given { get; } = new();
    }

    /// <summary>
    /// Gives the pipe up: no more connections, then the mutex goes, so a client starts the SDK's server and a
    /// daemon waiting for it takes it. Returns once the mutex is released.
    /// </summary>
    void Release()
    {
        Holding? holding;
        lock (_gate)
        {
            holding = _holding;
            Volatile.Write(ref _holding, null);
        }
        if (holding is null)
            return;
        holding.Listening.Dispose();
        holding.Give.Set();
        if (!holding.Given.Wait(TimeSpan.FromSeconds(5)))
            Log($"the mutex of {_pipeName} not released within 5 s");
        Log($"gave up {_pipeName}");
    }

    /// <summary>One connection at a time on each of a few pending server ends, while the daemon holds the pipe.</summary>
    sealed class Listening : IDisposable
    {
        readonly CancellationTokenSource _stopping = new();
        readonly Task[] _listeners;

        public Listening(CompilerServer server)
        {
            // As many pending connections as the SDK's server keeps, so parallel project builds don't queue.
            _listeners = [.. Enumerable.Range(0, Math.Min(4, Environment.ProcessorCount)).Select(_ => Task.Run(() => server.Listen(_stopping.Token)))];
        }

        /// <summary>Stops listening; requests being served still get their answers while the daemon runs.</summary>
        public void Dispose()
        {
            _stopping.Cancel();
            try { Task.WaitAll(_listeners, TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
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
                    // `dotnet build-server shutdown`, or another daemon taking the pipe. The daemon gives the pipe up
                    // first. On `dotnet build-server shutdown` the VBCSCompiler compiles were passed to goes too;
                    // another daemon passes compiles on to it as well. The answer names no running process: the
                    // client waits for the one it names to exit, Roslyn up to SDK 10.0.1xx with no other way out
                    // (RunServerShutdownRequestAsync), and the daemon keeps running. Not 0 (or -1): on Unix,
                    // kill(0, 0) succeeds, so .NET takes process 0 as running and waits on it forever.
                    var takeover = request.RequestId == TakeoverRequestId;
                    Log($"shutdown request ({(takeover ? "another daemon" : "dotnet build-server shutdown")}): giving the pipe up until the next blaze-cs command");
                    Release();
                    if (!takeover)
                        ShutDownPrivateServer();
                    new ServerResponse.Shutdown(NoProcess).Write(client);
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
    /// compiler's directory, with nothing to read or write, and none of the daemon's handles. It outlives the
    /// daemon, so on Windows it must not inherit them: a plain start there passes it every inheritable handle,
    /// among them the daemon's output, which cmd redirects into daemon-start.log, and the next daemon start then
    /// can't write that file. ShellExecute passes none, as the Csc task's CreateProcess does
    /// (bInheritHandles: false). On Unix the redirects replace the daemon's output, and .NET opens every other
    /// file close-on-exec.
    /// </summary>
    void StartPrivateServer()
    {
        var start = new ProcessStartInfo(_dotnet) { WorkingDirectory = _clientDirectory };
        if (OperatingSystem.IsWindows())
        {
            start.UseShellExecute = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
        }
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
            // A shutdown is answered at once; a server that doesn't is given up on.
            var answer = Task.Run(() => ServerResponse.Read(pipe));
            if (!answer.Wait(TimeSpan.FromSeconds(10)))
            {
                pipe.Dispose(); // ends the read
                return new ServerResponse.Rejected("no answer within 10 s");
            }
            return answer.Result;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or TimeoutException or AggregateException)
        {
            return new ServerResponse.Rejected($"no answer: {e.Message}");
        }
    }

    /// <summary>
    /// Gives the pipe up; compiles in flight still get their answers. The private VBCSCompiler stays for the other
    /// daemons of the SDK, and exits after its idle timeout.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Release();
    }
}
