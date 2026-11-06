using System.Text.Json.Serialization;

namespace BlazeCs;

// What the daemon and a test host say to each other; the host compiles this file and the daemon links it.
//
// The daemon writes messages to the host's stdin, one JSON line each (TestHostMessage). The host writes test
// output to stdout and, after each run, its result on a line of its own that starts with
// TestHostProtocol.ResultPrefix (test output may end mid-line, so the prefix may follow it on that line).
//
// 1. Run (TestHostRequest), always first: the host runs that test assembly and reports a result.
// 2. If the result's CannotHotReload is null, the host stays up and waits for the next message:
//    - Update (TestHostUpdate): the host applies each delta to the assembly whose module version id it
//      names (loading it from the delta's baseline if the tests haven't loaded it), with
//      MetadataUpdater.ApplyUpdate, then runs the same test assembly again with a new runner (so the
//      framework discovers its tests anew) and reports a result, back to 2. An update with Classes runs
//      only the tests of those classes (none if it is empty), which is how the daemon runs only what a delta
//      can reach (TestSelection.cs). A delta it can't apply ends it: that result has Unsupported and
//      CannotHotReload set, and ran nothing. So does an update without deltas: the daemon runs unchanged
//      tests in a new host, where their statics start anew. An assembly loaded from a baseline has the
//      baseline's Assembly.Location (the daemon's copy), not the path the tests would have loaded it from.
//    - Exit (TestHostExit): the host exits.
//    Otherwise CannotHotReload says why, and the host exits after that one run, as hosts always did.
// A line the host can't read, or a message out of turn, gets a result with Error and CannotHotReload set
// that names it, and ends the host. Stdin closing ends a host at once, whatever it is doing, and even with
// a message it hasn't acted on yet (its result would go nowhere); only a host still preparing finishes that
// first. A host ends by killing itself, so threads its tests started don't keep it running.
//
// A host can hot reload when the runtime allows updates (MetadataUpdater.IsSupported), which takes
// DOTNET_MODIFIABLE_ASSEMBLIES=debug in its environment, and only assemblies compiled without
// optimizations (Debug) take a delta. The daemon starts only the hosts it prepares for a Debug project with it
// (TestRunner.StartHost).

static class TestHostProtocol
{
    public const string ResultPrefix = "\u0001result:";

    /// <summary>The environment variable, set to "debug", that lets the runtime apply updates.</summary>
    public const string ModifiableAssemblies = "DOTNET_MODIFIABLE_ASSEMBLIES";
}

/// <summary>A line from the daemon to a test host: Run (TestHostRequest), Update or Exit.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TestHostRequest), "run")]
[JsonDerivedType(typeof(TestHostUpdate), "update")]
[JsonDerivedType(typeof(TestHostExit), "exit")]
abstract record TestHostMessage;

/// <summary>What a test host runs: a framework (xunit3, xunit2, nunit, mstest), an assembly, and the .runsettings it applies, if any.</summary>
sealed record TestHostRequest(string Framework, string Assembly, string? RunSettings) : TestHostMessage;

/// <summary>
/// Deltas to apply, in order, before the host runs its test assembly again; at least one. Classes, if set, are
/// the test classes whose tests run, named as a result's Classes names them; the others don't run, and an empty
/// list runs no test.
/// </summary>
sealed record TestHostUpdate(TestHostDelta[] Deltas, string[]? Classes = null) : TestHostMessage;

/// <summary>Ends a host that waits for an update.</summary>
sealed record TestHostExit : TestHostMessage;

/// <summary>
/// One edit's change to one assembly, as Compilation.EmitDifference (or Roslyn's hot reload service) writes
/// it: the assembly is the one whose manifest module has this version id (MVID), and UpdatedTypes are the
/// TypeDef tokens of the types the delta changes, if known (null: the host clears the caches of every type).
/// Baseline is a file of that module, which a host that hasn't loaded the assembly yet loads into its tests'
/// context before applying the delta (the assembly's file on disk may since have been rebuilt); without it,
/// the host must have loaded the module.
/// </summary>
sealed record TestHostDelta(Guid Module, byte[] Metadata, byte[] IL, byte[] Pdb, int[]? UpdatedTypes = null, string? Baseline = null);

/// <summary>
/// What a test host reports for one run. Unsupported means it ran nothing (it doesn't know this framework
/// version, or couldn't apply an update), so the daemon runs the project another way. CannotHotReload is
/// null when the host waits for an update, and otherwise says why it exits instead. Classes are the run's counts
/// by test class, named as the framework names a test's class (the type's full name, a nested type's after its
/// declaring type's and a '+'); null when the framework's results don't account for every test by class.
/// </summary>
sealed record TestHostResult(int Total, int Passed, int Failed, int Skipped, string? Error, string? Unsupported, string? CannotHotReload = null,
    Dictionary<string, TestCounts>? Classes = null);

/// <summary>A test class's results in one run.</summary>
sealed record TestCounts(int Passed, int Failed, int Skipped)
{
    [JsonIgnore]
    public int Total => Passed + Failed + Skipped;

    public static TestCounts operator +(TestCounts a, TestCounts b) => new(a.Passed + b.Passed, a.Failed + b.Failed, a.Skipped + b.Skipped);
}
