using System.Text;

namespace BlazeCs;

// The Roslyn compiler server's wire format, which the SDK's Csc task speaks to the pipe its SharedCompilationId
// names (Microsoft.CodeAnalysis.CommandLine.BuildRequest and BuildResponse, in Roslyn's
// src/Compilers/Shared/BuildProtocol.cs). Roslyn keeps those types internal; these write and read the same
// bytes. Every message is a 32-bit length, then its body. Integers are little-endian. A request's id and
// compiler hash are BinaryWriter strings (a 7-bit encoded byte count, then UTF-16); every other string is a
// 32-bit character count (-1 for null), then UTF-16.

enum RequestLanguage : uint
{
    CSharpCompile = 0x44532521,
    VisualBasicCompile = 0x44532522,
}

enum ArgumentId
{
    CurrentDirectory = 0x51147221,
    CommandLineArgument,
    LibEnvVariable,
    KeepAlive,
    Shutdown,
    TempDirectory,
}

/// <param name="Index">Which command line argument this is (0 to N); 0 for the others.</param>
readonly record struct RequestArgument(ArgumentId Id, int Index, string? Value);

/// <summary>A compile request: the client's working directory and csc's arguments, without the compiler.</summary>
sealed record ServerRequest(string RequestId, RequestLanguage Language, string CompilerHash, IReadOnlyList<RequestArgument> Arguments)
{
    /// <summary>The largest request the SDK's server reads.</summary>
    const int MaximumSize = 0x500000;

    public string? CurrentDirectory => Value(ArgumentId.CurrentDirectory);

    public string? TempDirectory => Value(ArgumentId.TempDirectory);

    /// <summary>`dotnet build-server shutdown` asks a server to exit with one Shutdown argument.</summary>
    public bool IsShutdown => Arguments is [{ Id: ArgumentId.Shutdown }];

    /// <summary>csc's arguments by index, as the SDK's server collects them (BuildProtocolUtil.GetCommandLineArguments).</summary>
    public string[] CommandLineArguments
    {
        get
        {
            var arguments = new List<string>();
            foreach (var argument in Arguments)
            {
                if (argument is not { Id: ArgumentId.CommandLineArgument, Value: { } value })
                    continue;
                while (argument.Index >= arguments.Count)
                    arguments.Add("");
                arguments[argument.Index] = value;
            }
            return [.. arguments];
        }
    }

    string? Value(ArgumentId id) => Arguments.LastOrDefault(a => a.Id == id).Value;

    /// <summary>The request as the Csc task builds it (BuildRequest.Create).</summary>
    public static ServerRequest Create(string requestId, string compilerHash, string workingDirectory, string? tempDirectory,
        IEnumerable<string> arguments, string? keepAlive = null, string? libDirectory = null)
    {
        List<RequestArgument> all = [new(ArgumentId.CurrentDirectory, 0, workingDirectory), new(ArgumentId.TempDirectory, 0, tempDirectory)];
        if (keepAlive is not null)
            all.Add(new(ArgumentId.KeepAlive, 0, keepAlive));
        if (libDirectory is not null)
            all.Add(new(ArgumentId.LibEnvVariable, 0, libDirectory));
        all.AddRange(arguments.Select((argument, index) => new RequestArgument(ArgumentId.CommandLineArgument, index, argument)));
        return new ServerRequest(requestId, RequestLanguage.CSharpCompile, compilerHash, all);
    }

    /// <summary>The request `dotnet build-server shutdown` sends (BuildRequest.CreateShutdown).</summary>
    public static ServerRequest Shutdown(string compilerHash) =>
        new("", RequestLanguage.CSharpCompile, compilerHash, [new(ArgumentId.Shutdown, 0, "")]);

    public static ServerRequest Read(Stream stream)
    {
        var length = BitConverter.ToInt32(ServerMessage.ReadExactly(stream, 4));
        if (length is < 0 or > MaximumSize)
            throw new InvalidDataException($"compile request of {length} bytes; at most {MaximumSize} are read");
        using var reader = new BinaryReader(new MemoryStream(ServerMessage.ReadExactly(stream, length)), Encoding.Unicode);
        var requestId = reader.ReadString();
        var language = (RequestLanguage)reader.ReadUInt32();
        var compilerHash = reader.ReadString();
        var count = reader.ReadUInt32();
        var arguments = new List<RequestArgument>();
        for (var i = 0; i < count; i++)
            arguments.Add(new RequestArgument((ArgumentId)reader.ReadInt32(), reader.ReadInt32(), ServerMessage.ReadString(reader)));
        return new ServerRequest(requestId, language, compilerHash, arguments);
    }

    public void Write(Stream stream) => ServerMessage.Write(stream, writer =>
    {
        writer.Write(RequestId);
        writer.Write((uint)Language);
        writer.Write(CompilerHash);
        writer.Write(Arguments.Count);
        foreach (var argument in Arguments)
        {
            writer.Write((int)argument.Id);
            writer.Write(argument.Index);
            ServerMessage.WriteString(writer, argument.Value);
        }
    });
}

/// <summary>
/// A server's answer. The Csc task relays a Completed one's exit code and output; on any other it runs csc
/// itself, and on IncorrectHash or MismatchedVersion it also logs an error, which fails the build.
/// </summary>
abstract record ServerResponse
{
    /// <summary>The response types, numbered as BuildResponse.ResponseType numbers them.</summary>
    public enum Kind
    {
        MismatchedVersion,
        Completed,
        AnalyzerInconsistency,
        Shutdown,
        Rejected,
        IncorrectHash,
        CannotConnect,
    }

    public abstract Kind Type { get; }

    protected virtual void WriteBody(BinaryWriter writer) { }

    public void Write(Stream stream) => ServerMessage.Write(stream, writer =>
    {
        writer.Write((int)Type);
        WriteBody(writer);
    });

    public static ServerResponse Read(Stream stream)
    {
        var length = BitConverter.ToUInt32(ServerMessage.ReadExactly(stream, 4));
        using var reader = new BinaryReader(new MemoryStream(ServerMessage.ReadExactly(stream, checked((int)length))), Encoding.Unicode);
        return (Kind)reader.ReadInt32() switch
        {
            Kind.Completed => Completed.Read(reader),
            Kind.Rejected => new Rejected(ServerMessage.ReadString(reader) ?? ""),
            Kind.Shutdown => new Shutdown(reader.ReadInt32()),
            Kind.AnalyzerInconsistency => new AnalyzerInconsistency([.. Enumerable.Range(0, reader.ReadInt32()).Select(_ => ServerMessage.ReadString(reader) ?? "")]),
            Kind.IncorrectHash => new IncorrectHash(),
            Kind.MismatchedVersion => new MismatchedVersion(),
            var other => throw new InvalidDataException($"compile response of unknown type {other}"),
        };
    }

    /// <summary>
    /// csc's result. Roslyn since August 2026 (dotnet/roslyn#84725) follows the output with telemetry events: a
    /// count, then each event's name and properties. This writes a count of none, which a newer task reads as
    /// no events and an older one, reading only the fields it knows from a message of known length, ignores.
    /// </summary>
    /// <param name="ReturnCode">csc's exit code.</param>
    /// <param name="Utf8Output">Whether csc ran with /utf8output.</param>
    /// <param name="Output">What csc would print: one diagnostic per line.</param>
    public sealed record Completed(int ReturnCode, bool Utf8Output, string Output) : ServerResponse
    {
        public override Kind Type => Kind.Completed;

        protected override void WriteBody(BinaryWriter writer)
        {
            writer.Write(ReturnCode);
            writer.Write(Utf8Output);
            ServerMessage.WriteString(writer, Output);
            writer.Write(0); // telemetry events
        }

        public static Completed Read(BinaryReader reader)
        {
            var completed = new Completed(reader.ReadInt32(), reader.ReadBoolean(), ServerMessage.ReadString(reader) ?? "");
            // Telemetry events, from a newer server; each is a name, then a count of key and value pairs.
            if (reader.BaseStream.Length - reader.BaseStream.Position >= 4)
            {
                for (var events = reader.ReadInt32(); events > 0; events--)
                {
                    ServerMessage.ReadString(reader);
                    for (var properties = reader.ReadInt32() * 2; properties > 0; properties--)
                        ServerMessage.ReadString(reader);
                }
            }
            return completed;
        }
    }

    public sealed record Rejected(string Reason) : ServerResponse
    {
        public override Kind Type => Kind.Rejected;

        protected override void WriteBody(BinaryWriter writer) => ServerMessage.WriteString(writer, Reason);
    }

    public sealed record Shutdown(int ServerProcessId) : ServerResponse
    {
        public override Kind Type => Kind.Shutdown;

        protected override void WriteBody(BinaryWriter writer) => writer.Write(ServerProcessId);
    }

    public sealed record AnalyzerInconsistency(IReadOnlyList<string> ErrorMessages) : ServerResponse
    {
        public override Kind Type => Kind.AnalyzerInconsistency;

        protected override void WriteBody(BinaryWriter writer)
        {
            writer.Write(ErrorMessages.Count);
            foreach (var message in ErrorMessages)
                ServerMessage.WriteString(writer, message);
        }
    }

    public sealed record IncorrectHash : ServerResponse
    {
        public override Kind Type => Kind.IncorrectHash;
    }

    public sealed record MismatchedVersion : ServerResponse
    {
        public override Kind Type => Kind.MismatchedVersion;
    }
}

static class ServerMessage
{
    /// <summary>A message: its body's length, then the body, in one write.</summary>
    public static void Write(Stream stream, Action<BinaryWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0); // the length, once known
            body(writer);
        }
        var bytes = buffer.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), bytes.Length - 4);
        stream.Write(bytes);
        stream.Flush();
    }

    /// <summary>A whole message as it came, its length included: what a proxy passes on unread.</summary>
    public static byte[] ReadMessage(Stream stream)
    {
        var length = ReadExactly(stream, 4);
        var body = ReadExactly(stream, checked((int)BitConverter.ToUInt32(length)));
        return [.. length, .. body];
    }

    public static byte[] ReadExactly(Stream stream, int count)
    {
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A 32-bit character count (-1 for null), then the characters (BuildProtocolConstants.ReadLengthPrefixedString).</summary>
    public static string? ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        return length < 0 ? null : new string(reader.ReadChars(length));
    }

    public static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }
        writer.Write(value.Length);
        writer.Write(value.ToCharArray());
    }
}
