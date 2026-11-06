using System.Diagnostics;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BlazeCs;

// The solution's units as a compiler server's: each compile request from the SDK's Csc task (see CompilerServer)
// is answered from the unit it names, as csc would answer it.
sealed partial class SolutionBuilder
{
    /// <summary>Requests share it, a request that replaces a unit holds it alone, and so does every daemon command.</summary>
    readonly ReaderWriterLockSlim _serving = new();
    int _commands;

    /// <summary>
    /// Taken by each daemon command for as long as it runs: requests that arrive meanwhile are rejected, so one
    /// from a build the command runs itself (a SharedCompilationId in Directory.Build.props) doesn't wait for it.
    /// </summary>
    public IDisposable Command()
    {
        Interlocked.Increment(ref _commands);
        _serving.EnterWriteLock();
        return new Release(() =>
        {
            _serving.ExitWriteLock();
            Interlocked.Decrement(ref _commands);
        });
    }

    sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>
    /// Answers a compile request for a unit of this solution: refresh its sources from disk, emit, write the
    /// files csc would write, and return csc's exit code and diagnostics. Anything the daemon can't answer as
    /// csc would is Rejected, and csc compiles it (the SDK's server, which <see cref="CompilerServer"/> passes it
    /// to). Null for a compile of another solution's project. Logged to the daemon log.
    /// </summary>
    public ServerResponse? Serve(ServerRequest request)
    {
        if (request.CurrentDirectory is { Length: > 0 } directory && OutputPath(request.CommandLineArguments, directory) is { } output
            && !Volatile.Read(ref _units).Any(u => PathComparer.Instance.Equals(u.AssemblyPath, output)) && Volatile.Read(ref _reload) is null)
            return null;
        var started = Stopwatch.GetTimestamp();
        ServerResponse response;
        try { response = Answer(request); }
        catch (Exception e)
        {
            Console.WriteLine($"{DateTime.Now:O} compiler server: {request.RequestId}: {e}");
            response = Reject($"internal error: {e.Message}");
        }
        Console.WriteLine($"{DateTime.Now:O} compiler server: {request.RequestId}: " + response switch
        {
            ServerResponse.Completed completed => $"compiled in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms, exit code {completed.ReturnCode}",
            ServerResponse.Rejected rejected => $"rejected: {rejected.Reason}",
            var other => other.Type.ToString(),
        });
        return response;
    }

    ServerResponse Answer(ServerRequest request)
    {
        if (request.Language != RequestLanguage.CSharpCompile)
            return Reject("blaze-cs compiles C# only");
        if (request.CurrentDirectory is not { Length: > 0 } directory)
            return Reject("the request has no working directory");
        var arguments = request.CommandLineArguments;
        if (OutputPath(arguments, directory) is not { } assembly)
            return Reject("the request has no /out:");

        // The first load's warm-up compiles every unit; a request waits for it, as the next command does.
        if (Volatile.Read(ref _warmUp) is { } warmUp)
        {
            try
            {
                if (warmUp.GetAwaiter().GetResult().Stale)
                    return Reject("the daemon's compiler arguments are stale until the next blaze-cs command reloads them");
            }
            catch (Exception e)
            {
                return Reject($"the daemon's warm-up failed: {e.Message}");
            }
        }

        CompileUnit wanted;
        if (Volatile.Read(ref _commands) > 0)
            return Reject("a blaze-cs command is running");
        _serving.EnterReadLock();
        try
        {
            if (Unserved(assembly, directory) is { } reason)
                return Reject(reason);
            var unit = _units.First(u => PathComparer.Instance.Equals(u.AssemblyPath, assembly));
            wanted = unit.Unit with { Arguments = CscArguments(unit.Unit.Arguments, arguments) };
            if (unit.Matches(wanted))
                return Compile(unit, fresh: false);
        }
        finally
        {
            _serving.ExitReadLock();
        }

        // Its arguments changed (a project file edit, say): a unit built from the new ones takes its place, so
        // the daemon's arguments stay those of the last compile, as when it reads them from a build's log.
        if (Volatile.Read(ref _commands) > 0)
            return Reject("a blaze-cs command is running");
        _serving.EnterWriteLock();
        try
        {
            if (Unserved(assembly, directory) is { } reason)
                return Reject(reason);
            var unit = _units.First(u => PathComparer.Instance.Equals(u.AssemblyPath, assembly));
            if (unit.Matches(wanted))
                return Compile(unit, fresh: false); // another request just replaced it
            ProjectCompiler replacement;
            try { replacement = new ProjectCompiler(wanted, ResolveReference, _analyzers); }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                return Reject($"{unit.Unit.Name}: its new arguments: {e.Message}");
            }
            if (!PathComparer.Instance.Equals(replacement.ImagePath, unit.ImagePath))
                return Reject($"{unit.Unit.Name}: its new arguments write another reference assembly");
            ReplaceUnit(unit, replacement);
            return Compile(replacement, fresh: true);
        }
        finally
        {
            _serving.ExitWriteLock();
        }
    }

    /// <summary>Why a request for this assembly can't be served now, or null if it can.</summary>
    string? Unserved(string assembly, string directory)
    {
        if (_reload is not null)
            return "the daemon must load the solution again; the next blaze-cs command does";
        if (_units.FirstOrDefault(u => PathComparer.Instance.Equals(u.AssemblyPath, assembly)) is not { } unit)
            return $"{assembly} is not the output of a project of {_solutionPath}";
        // csc reads every relative path in its arguments against the working directory.
        if (!PathComparer.Instance.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), Path.TrimEndingDirectorySeparator(unit.Unit.ProjectDirectory)))
            return $"{unit.Unit.Name}: the request's working directory {directory} is not the project's";
        return null;
    }

    ServerResponse Compile(ProjectCompiler unit, bool fresh)
    {
        if (unit.CscOnly() is { } only)
            return Reject($"{unit.Unit.Name}: {only}");
        lock (unit)
        {
            var started = Stopwatch.GetTimestamp();
            // csc reads every input from disk as the build left it: the sources it is given, and references.
            if (!fresh)
                unit.Realign(analyzed: false);
            if (unit.StaleArguments is { } stale)
                return Reject($"{unit.Unit.Name}: {stale}");
            ReadReferences(unit);
            unit.RefreshReferences();
            var prepared = unit.Prepare();
            unit.GeneratedChanged(prepared);
            var surface = _dependents[unit].Count > 0 ? Task.Run(() => new ApiSurface(prepared.Compilation)) : null;
            var analysis = unit.AnalyzeAsCsc(prepared);
            var (emitted, diagnostics) = unit.Emit(prepared);
            var reported = ProjectCompiler.AsCsc(diagnostics, analysis?.GetAwaiter().GetResult() ?? []);
            _lastDuration[unit] = Stopwatch.GetElapsedTime(started);

            // Errors that the daemon's options lower to warnings (warnings as errors, config severities): csc
            // reports them as errors, and the task's output is csc's.
            if (reported.FirstOrDefault(d => d.Severity != DiagnosticSeverity.Error && unit.ErrorUnderCsc(d)) is { } lowered)
            {
                lock (_retry)
                    _retry[unit] = null;
                return Reject($"{unit.Unit.Name}: csc reports {lowered.Id} as an error, which the daemon reports as a warning");
            }
            var output = string.Concat(reported.Select(d => CSharpDiagnosticFormatter.Instance.Format(d, CultureInfo.CurrentUICulture) + Environment.NewLine));
            // csc writes nothing when anything reports an error, an analyzer included (a rule at error by default,
            // or by a category or global severity in a config file, which the daemon doesn't lower). Until it
            // compiles again, what it wrote last is stale, and the next blaze-cs build compiles it.
            if (emitted is null || reported.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                lock (_retry)
                    _retry[unit] = null;
                return new ServerResponse.Completed(1, unit.Utf8Output, output);
            }

            WriteAsCsc(unit, emitted);
            unit.AnalyzedAsCsc(prepared);
            // csc's analysis ran the compilation-end rules too, and the build printed what they found: as after a
            // build outside the daemon, the background analysis has nothing left to report on this unit.
            lock (_wholeAnalysisPending)
                _wholeAnalysisPending.Remove(unit);
            // Dependents compile through MSBuild, which decides which of them to build; the API they last
            // compiled against is this one.
            if (PublishImage(unit, emitted) && surface is not null)
                SurfaceChange(unit, surface.Result);
            lock (_retry)
                _retry.Remove(unit);
            return new ServerResponse.Completed(0, unit.Utf8Output, output);
        }
    }

    /// <summary>
    /// Takes in each reference another unit produces as it is on disk, which is what csc reads: csc may have
    /// compiled that unit (a request the daemon rejected, a build without the pipe) to other bytes than the
    /// daemon's image.
    /// </summary>
    void ReadReferences(ProjectCompiler unit)
    {
        foreach (var reference in unit.References)
        {
            if (!_imagePaths.TryGetValue(reference.Reference, out var imagePath) || !File.Exists(reference.Reference))
                continue;
            var bytes = File.ReadAllBytes(reference.Reference);
            lock (_images)
            {
                if (_images.TryGetValue(imagePath, out var current) && current.Bytes.AsSpan().SequenceEqual(bytes))
                    continue;
                _images[imagePath] = new Image(bytes);
            }
            // The API it had before is unknown now: its next change counts as a change to all of it.
            if (_producers.GetValueOrDefault(reference.Reference) is { } producer)
                _surfaces.TryRemove(producer, out _);
        }
    }

    /// <summary>
    /// csc writes each of its outputs on every compile, changed or not: MSBuild's up-to-date checks read their times.
    /// The copies MSBuild makes in other projects' bin/ are brought up to date too, as a blaze-cs build would: a
    /// build of one project doesn't copy into the projects that reference it, and the daemon's next build, which
    /// finds this unit current, wouldn't write it again (#133).
    /// </summary>
    void WriteAsCsc(ProjectCompiler unit, EmitOutput output)
    {
        void Write(string path, byte[] bytes)
        {
            var content = OutputContent.Of(bytes);
            WriteCopies(path, bytes, content);
            Replace(path, bytes);
            _published.Wrote(path, content);
        }
        Write(unit.AssemblyPath, output.Assembly);
        if (unit.PdbPath is { } pdb && output.Pdb is { } pdbBytes)
            Write(pdb, pdbBytes);
        if (unit.DocumentationPath is { } documentation && output.Documentation is { } documentationBytes)
            Write(Path.GetFullPath(documentation, unit.Unit.ProjectDirectory), documentationBytes);
        if (unit.ReferenceAssemblyPath is { } reference)
            Write(reference, ImageBytes(unit, output));
    }

    /// <summary>Puts a unit built from new arguments in the place of the old one, and records the new arguments.</summary>
    void ReplaceUnit(ProjectCompiler old, ProjectCompiler replacement)
    {
        Assemble([.. _units.Select(u => u == old ? replacement : u)]);
        if (_surfaces.TryRemove(old, out var surface))
            _surfaces[replacement] = surface;
        if (_lastDuration.TryRemove(old, out var duration))
            _lastDuration[replacement] = duration;
        _retry.Remove(old);
        if (_wholeAnalysisPending.Remove(old))
            _wholeAnalysisPending.Add(replacement);
        UnitsCache.Write(_units.Select(u => u.Unit));
    }

    /// <summary>
    /// The Csc task sends a server its response file's arguments only, without /noconfig, which goes on csc's
    /// own command line; the arguments the build log records start with it.
    /// </summary>
    static string[] CscArguments(string[] cached, string[] request) =>
        cached is ["/noconfig", ..] && request is not ["/noconfig", ..] ? ["/noconfig", .. request] : request;

    /// <summary>The assembly csc writes (/out:, as <see cref="CompileUnit.AssemblyPath"/> reads it).</summary>
    static string? OutputPath(string[] arguments, string directory) =>
        arguments.LastOrDefault(a => a.StartsWith("/out:", StringComparison.Ordinal)) is { } argument
            ? Path.GetFullPath(argument["/out:".Length..].Trim('"'), directory)
            : null;

    static ServerResponse.Rejected Reject(string reason) => new(reason);
}
