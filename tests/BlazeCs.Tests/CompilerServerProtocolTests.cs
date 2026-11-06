using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// The compiler server's wire format (<see cref="ServerRequest"/>, <see cref="ServerResponse"/>) against the SDK's
/// own implementation: Roslyn's internal BuildRequest and BuildResponse in the SDK's
/// Microsoft.Build.Tasks.CodeAnalysis.dll, the assembly the Csc task runs from. Each message goes one way through
/// Roslyn's code and the other through blaze-cs's.
/// </summary>
public class CompilerServerProtocolTests
{
    static readonly SdkProtocol Sdk = new();

    // A request id and hash past 63 characters take a two-byte length; others test nulls, quotes and non-ASCII.
    static readonly string LongId = "Lib (net10.0) " + new string('x', 80);
    static readonly string[] Arguments = ["/out:obj/Debug/net10.0/Lib.dll", "/define:TRACE;DEBUG", "/features:\"a=b;c\"", "Grüße.cs", "", "path with spaces/x.cs"];

    [Fact]
    public void ReadsTheRequestTheCscTaskWrites()
    {
        var bytes = Sdk.WriteRequest(LongId, "5a2162bea6845eabc1fc721624a43568c65db3b1", "/src/Lib", tempDirectory: null, Arguments, keepAlive: "600", libDirectory: "/lib");
        var request = ServerRequest.Read(new MemoryStream(bytes));

        Assert.Equal(LongId, request.RequestId);
        Assert.Equal(RequestLanguage.CSharpCompile, request.Language);
        Assert.Equal("5a2162bea6845eabc1fc721624a43568c65db3b1", request.CompilerHash);
        Assert.Equal("/src/Lib", request.CurrentDirectory);
        Assert.Null(request.TempDirectory);
        Assert.Contains(new RequestArgument(ArgumentId.KeepAlive, 0, "600"), request.Arguments);
        Assert.Contains(new RequestArgument(ArgumentId.LibEnvVariable, 0, "/lib"), request.Arguments);
        Assert.Equal(Arguments, request.CommandLineArguments);
        Assert.False(request.IsShutdown);
    }

    [Fact]
    public void WritesRequestsTheSdkServerReads()
    {
        var ours = ServerRequest.Create(LongId, "hash", "C:\\src\\Lib", "/tmp/", Arguments, libDirectory: "Ü");
        var buffer = new MemoryStream();
        ours.Write(buffer);
        var theirs = Sdk.ReadRequest(buffer.ToArray());

        Assert.Equal(LongId, theirs.RequestId);
        Assert.Equal("CSharpCompile", theirs.Language);
        Assert.Equal("hash", theirs.CompilerHash);
        Assert.Equal(ours.Arguments.Select(a => (a.Id.ToString(), a.Index, a.Value)), theirs.Arguments);
    }

    [Fact]
    public void ReadsTheSdkShutdownRequest()
    {
        var request = ServerRequest.Read(new MemoryStream(Sdk.WriteShutdownRequest()));
        Assert.True(request.IsShutdown);
    }

    public static TheoryData<int, bool, string> Completions => new()
    {
        { 0, false, "" },
        { 1, true, "/src/Lib/Greeter.cs(3,5): error CS0103: The name 'x' does not exist in the current context\nGrüße\n" },
        { -1, false, new string('w', 70_000) },
    };

    [Theory]
    [MemberData(nameof(Completions))]
    public void TheCscTaskReadsCompletedResponses(int returnCode, bool utf8Output, string output)
    {
        var theirs = Sdk.ReadResponse(Bytes(new ServerResponse.Completed(returnCode, utf8Output, output)));
        Assert.Equal("Completed", theirs.Type);
        Assert.Equal(returnCode, theirs.Field<int>("ReturnCode"));
        Assert.Equal(utf8Output, theirs.Field<bool>("Utf8Output"));
        Assert.Equal(output, theirs.Field<string>("Output"));
    }

    [Fact]
    public void TheCscTaskReadsRejectedResponses()
    {
        var theirs = Sdk.ReadResponse(Bytes(new ServerResponse.Rejected("Lib.dll is not the output of a project of Fixture.slnx")));
        Assert.Equal("Rejected", theirs.Type);
        Assert.Equal("Lib.dll is not the output of a project of Fixture.slnx", theirs.Field<string>("Reason"));
    }

    [Fact]
    public void ReadsTheResponsesTheSdkServerWrites()
    {
        Assert.Equal(new ServerResponse.Completed(1, true, "error\n"), ServerResponse.Read(new MemoryStream(Sdk.WriteCompleted(1, true, "error\n"))));
        Assert.Equal(new ServerResponse.Rejected("busy"), ServerResponse.Read(new MemoryStream(Sdk.WriteRejected("busy"))));
        Assert.Equal(new ServerResponse.Shutdown(4242), ServerResponse.Read(new MemoryStream(Sdk.WriteShutdown(4242))));
    }

    /// <summary>A daemon frees its pipe of a VBCSCompiler with the request `dotnet build-server shutdown` sends.</summary>
    [Fact]
    public void WritesTheSdkShutdownRequest()
    {
        var hash = Sdk.CommitHash()!;
        Assert.Equal(Sdk.WriteShutdownRequest(), Bytes(ServerRequest.Shutdown(hash)));
    }

    /// <summary>
    /// A Completed response as Roslyn since August 2026 writes it, with telemetry events after the output; a
    /// task of that Roslyn reads the count blaze-cs writes there (none) from the same place.
    /// </summary>
    [Fact]
    public void ReadsCompletedResponsesWithTelemetry()
    {
        var buffer = new MemoryStream();
        ServerMessage.Write(buffer, writer =>
        {
            writer.Write((int)ServerResponse.Kind.Completed);
            writer.Write(0);
            writer.Write(true);
            ServerMessage.WriteString(writer, "ok");
            writer.Write(1); // one event, with one property
            ServerMessage.WriteString(writer, "compilation");
            writer.Write(1);
            ServerMessage.WriteString(writer, "key");
            ServerMessage.WriteString(writer, "value");
        });
        buffer.Position = 0;
        Assert.Equal(new ServerResponse.Completed(0, true, "ok"), ServerResponse.Read(buffer));

        byte[] ours = Bytes(new ServerResponse.Completed(0, true, "ok"));
        Assert.Equal([0, 0, 0, 0], ours[^4..]);
    }

    /// <summary>The layout itself, byte by byte, for a Rejected response: lengths, type, then the reason's characters.</summary>
    [Fact]
    public void RejectedIsLengthTypeAndCountedCharacters()
    {
        byte[] expected = [14, 0, 0, 0, 4, 0, 0, 0, 3, 0, 0, 0, (byte)'a', 0, (byte)'b', 0, (byte)'c', 0];
        Assert.Equal(expected, Bytes(new ServerResponse.Rejected("abc")));
        Assert.Equal(expected, Sdk.WriteRejected("abc"));
    }

    /// <summary>The hash the daemon compares requests with (the SDK's server's) is the one the Csc task sends.</summary>
    [Fact]
    public void CommitHashOfTheSdkServerIsTheTasks()
    {
        var hash = CompilerServer.CommitHash(Path.Combine(Sdk.Directory, "Roslyn", "bincore", "VBCSCompiler.dll"));
        Assert.Matches("^[0-9a-f]{40}$", hash);
        Assert.Equal(Sdk.CommitHash(), hash);
    }

    /// <summary>
    /// The pipe the daemon listens on is the one the Csc task of that SDK connects to without a SharedCompilationId:
    /// for the SDK's compiler directory, for others, and with a trailing separator or other casing, which the task
    /// normalizes away. On Windows the name also says whether the process runs elevated.
    /// </summary>
    [Fact]
    public void PipeNameIsTheOneTheCscTaskDerives()
    {
        var bincore = Path.Combine(Sdk.Directory, "Roslyn", "bincore");
        foreach (var directory in new[] { bincore, bincore + Path.DirectorySeparatorChar, bincore.ToUpperInvariant(), Path.Combine(Path.GetTempPath(), "Ünïcode dir") })
            Assert.Equal(Sdk.PipeName(directory), CompilerServer.PipeName(directory));
        Assert.Equal(Sdk.PipeName(bincore), CompilerServer.PipeName(Sdk.ToolDirectory()));
    }

    static byte[] Bytes(ServerResponse response)
    {
        var buffer = new MemoryStream();
        response.Write(buffer);
        return buffer.ToArray();
    }

    static byte[] Bytes(ServerRequest request)
    {
        var buffer = new MemoryStream();
        request.Write(buffer);
        return buffer.ToArray();
    }

    /// <summary>Roslyn's protocol classes in the SDK this repository builds with, called by reflection.</summary>
    sealed class SdkProtocol
    {
        const string Namespace = "Microsoft.CodeAnalysis.CommandLine.";
        readonly Assembly _tasks;

        public string Directory { get; }

        public SdkProtocol()
        {
            var repository = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repository, "global.json")))
                repository = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repository)) ?? throw new InvalidOperationException("no global.json above the tests");
            Directory = SdkRoslyn.FindSdkDirectory(repository);
            _tasks = new SdkContext(Directory).LoadFromAssemblyPath(Path.Combine(Directory, "Roslyn", "Microsoft.Build.Tasks.CodeAnalysis.dll"));
        }

        Type Type(string name) => _tasks.GetType(Namespace + name, throwOnError: true)!;

        static object? Await(object? task)
        {
            ((Task)task!).GetAwaiter().GetResult();
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        static object? Call(Type type, string name, object? target, params object?[] arguments) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!.Invoke(target, arguments);

        /// <summary>BuildRequest.Create, the factory the Csc task uses, with its parameters given by name.</summary>
        public byte[] WriteRequest(string requestId, string compilerHash, string workingDirectory, string? tempDirectory, string[] arguments,
            string? keepAlive, string? libDirectory)
        {
            var create = Type("BuildRequest").GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            var values = new Dictionary<string, object?>
            {
                ["language"] = Enum.Parse(Type("RequestLanguage"), "CSharpCompile"),
                ["args"] = arguments.ToList(),
                ["workingDirectory"] = workingDirectory,
                ["tempDirectory"] = tempDirectory,
                ["compilerHash"] = compilerHash,
                ["requestId"] = requestId,
                ["keepAlive"] = keepAlive,
                ["libDirectory"] = libDirectory,
            };
            var request = create.Invoke(null, [.. create.GetParameters().Select(p => values.TryGetValue(p.Name!, out var value) ? value : p.DefaultValue)]);
            return Write(request!);
        }

        public byte[] WriteShutdownRequest() => Write(Call(Type("BuildRequest"), "CreateShutdown", null)!);

        public (string RequestId, string Language, string CompilerHash, List<(string, int, string?)> Arguments) ReadRequest(byte[] bytes)
        {
            var request = Await(Call(Type("BuildRequest"), "ReadAsync", null, new MemoryStream(bytes), CancellationToken.None))!;
            object? Field(object target, string name) => target.GetType().GetField(name)!.GetValue(target);
            var arguments = ((IEnumerable)Field(request, "Arguments")!).Cast<object>()
                .Select(a => (Field(a, "ArgumentId")!.ToString()!, (int)Field(a, "ArgumentIndex")!, (string?)Field(a, "Value")))
                .ToList();
            return ((string)Field(request, "RequestId")!, Field(request, "Language")!.ToString()!, (string)Field(request, "CompilerHash")!, arguments);
        }

        public Response ReadResponse(byte[] bytes) =>
            new(Await(Call(Type("BuildResponse"), "ReadAsync", null, new MemoryStream(bytes), CancellationToken.None))!);

        public byte[] WriteCompleted(int returnCode, bool utf8Output, string output) =>
            Write(Activator.CreateInstance(Type("CompletedBuildResponse"), returnCode, utf8Output, output)!);

        public byte[] WriteRejected(string reason) => Write(Activator.CreateInstance(Type("RejectedBuildResponse"), reason)!);

        public byte[] WriteShutdown(int serverProcessId) => Write(Activator.CreateInstance(Type("ShutdownBuildResponse"), serverProcessId)!);

        public string? CommitHash() => (string?)Call(Type("BuildProtocolConstants"), "GetCommitHash", null);

        /// <summary>BuildServerConnection.GetPipeName(clientDirectory), which the Csc task names the pipe with.</summary>
        public string PipeName(string clientDirectory) =>
            (string)Type("BuildServerConnection").GetMethod("GetPipeName", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)])!.Invoke(null, [clientDirectory])!;

        /// <summary>ManagedToolTask.GetToolDirectory(): the client directory of the task assembly loaded from the SDK.</summary>
        public string ToolDirectory() =>
            (string)_tasks.GetType("Microsoft.CodeAnalysis.BuildTasks.ManagedToolTask", throwOnError: true)!
                .GetMethod("GetToolDirectory", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

        static byte[] Write(object message)
        {
            var buffer = new MemoryStream();
            Await(Call(message.GetType(), "WriteAsync", message, buffer, CancellationToken.None));
            return buffer.ToArray();
        }
    }

    sealed record Response(object Value)
    {
        public string Type => Value.GetType().GetProperty("Type")!.GetValue(Value)!.ToString()!;
        public T Field<T>(string name) => (T)Value.GetType().GetField(name)!.GetValue(Value)!;
    }

    /// <summary>The task assembly with the SDK's MSBuild and Roslyn beside it, apart from the Roslyn the tests run on.</summary>
    sealed class SdkContext(string sdk) : AssemblyLoadContext("sdk-roslyn-tasks")
    {
        static readonly HashSet<string> Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        protected override Assembly? Load(AssemblyName name)
        {
            if (Framework.Contains(name.Name!))
                return null;
            foreach (var directory in new[] { Path.Combine(sdk, "Roslyn"), Path.Combine(sdk, "Roslyn", "bincore"), sdk })
            {
                var candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate))
                    return LoadFromAssemblyPath(candidate);
            }
            return null;
        }
    }
}
