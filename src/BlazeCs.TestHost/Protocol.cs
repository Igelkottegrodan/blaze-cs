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
//    - Update (TestHostUpdate): the host applies each delta to the loaded assembly whose module version id
//      it names, with MetadataUpdater.ApplyUpdate, then runs the same test assembly again with a new runner
//      (so the framework discovers its tests anew) and reports a result, back to 2. A delta it can't
//      apply ends it: that result has Unsupported and CannotHotReload set, and ran nothing.
//    - Exit (TestHostExit): the host exits.
//    Otherwise CannotHotReload says why, and the host exits after that one run, as hosts always did.
// Stdin closing ends a host at any point: at once during a run, after preparing while it waits for a Run.
//
// A host can hot reload when the runtime allows updates (MetadataUpdater.IsSupported), which takes
// DOTNET_MODIFIABLE_ASSEMBLIES=debug in its environment, and only assemblies compiled without
// optimizations (Debug) take a delta. The daemon doesn't start its hosts with it yet (TestRunner.StartHost).

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

/// <summary>Deltas to apply, in order, before the host runs its test assembly again.</summary>
sealed record TestHostUpdate(TestHostDelta[] Deltas) : TestHostMessage;

/// <summary>Ends a host that waits for an update.</summary>
sealed record TestHostExit : TestHostMessage;

/// <summary>
/// One edit's change to one assembly the host has loaded, as Compilation.EmitDifference (or Roslyn's hot
/// reload service) writes it: the assembly is the one whose manifest module has this version id (MVID), and
/// UpdatedTypes are the TypeDef tokens of the types the delta changes, if known (null: the host clears the
/// caches of every type).
/// </summary>
sealed record TestHostDelta(Guid Module, byte[] Metadata, byte[] IL, byte[] Pdb, int[]? UpdatedTypes = null);

/// <summary>
/// What a test host reports for one run. Unsupported means it ran nothing (it doesn't know this framework
/// version, or couldn't apply an update), so the daemon runs the project another way. CannotHotReload is
/// null when the host waits for an update, and otherwise says why it exits instead.
/// </summary>
sealed record TestHostResult(int Total, int Passed, int Failed, int Skipped, string? Error, string? Unsupported, string? CannotHotReload = null);
