#!/usr/bin/env dotnet
#:include ../../scripts/PeIdentity.cs
// Regression check: makes each kind of edit to a copy of this fixture, rebuilds it with blaze-cs, and fails
// unless (1) blaze-cs rebuilt exactly the expected projects and (2) every assembly is what
// `dotnet build --no-incremental` produces. An assembly may differ only in debug identity (PeIdentity),
// and only in a project blaze-cs skipped: its PDB still records the old MVID of the reference that changed.
//
// usage: task check   (or: dotnet run tests/fixture/check.cs, after `dotnet build BlazeCs.slnx`)
using System.Diagnostics;
using System.Text.RegularExpressions;

var fixture = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "tests/fixture/check.cs"))!;
var root = Path.GetFullPath(Path.Combine(fixture, "../.."));
var work = Path.Combine(Path.GetTempPath(), "blaze-cs-fixture");
var blaze = Path.Combine(root, "blaze-cs");

// (name, edit, units blaze-cs must rebuild, command); the edits build on each other. Lib and Consumer
// target net8.0 and net10.0, and blaze-cs names each target framework's unit "Project (tfm)".
string[] lib = ["Lib (net10.0)", "Lib (net8.0)"], consumer = ["Consumer (net10.0)", "Consumer (net8.0)"];
(string Name, Action Edit, string[] Expected, string Command)[] cases =
[
    ("body", () => Replace("Lib/Greeter.cs", "\"Hello, \"", "\"Hello there, \""), lib, "build"),
    ("signature used by Consumer",
        () => Replace("Lib/Greeter.cs", "Greet(string name) => \"Hello there, \" + name", "Greet(string person) => \"Hello there, \" + person"),
        [.. consumer, .. lib], "build"),
    ("signature unused by Consumer",
        () => Replace("Lib/Greeter.cs", "Twice(int value) => value * 2", "Twice(int amount) => amount * 2"), lib, "build"),
    // Lib (net8.0) recompiles because its source changed, but its API doesn't, so Consumer (net8.0) doesn't.
    ("net10.0-only signature",
        () => Replace("Lib/Greeter.cs", "Shout(string name) => name.ToUpperInvariant()", "Shout(string who) => who.ToUpperInvariant()"),
        ["Consumer (net10.0)", .. lib], "build"),
    ("new file",
        () => File.WriteAllText(Path.Combine(work, "Lib/Probe.cs"), "namespace Lib;\n\ninternal static class Probe\n{\n    internal const int Edit = 1;\n}\n"),
        lib, "build"),
    ("generator output",
        () => Replace("Lib/Greeter.cs", "    public static int Twice",
            "    public static string Farewell(string name) => \"Bye, \" + name;\n\n    public static int Twice"),
        [.. consumer, .. lib], "build"),
    // A test run must also show each rebuilt test project's tests passing (RanTests).
    ("xUnit v3 test edit", () => Replace("Tests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["Tests"], "test"),
    ("NUnit test edit", () => Replace("NUnitTests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["NUnitTests"], "test"),
    ("MSTest test edit", () => Replace("MSTestTests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["MSTestTests"], "test"),
];

if (Directory.Exists(work))
    Directory.Delete(work, recursive: true);
CopyFixture(fixture, work);
File.Copy(Path.Combine(root, "global.json"), Path.Combine(work, "global.json"));
var passed = false;
try
{
    Run(blaze, "build"); // starts the daemon: a real dotnet build, then a warm-up compile
    foreach (var (name, edit, expected, command) in cases)
    {
        edit();
        var output = Run(blaze, command);
        var rebuilt = Rebuilt(output);
        if (!rebuilt.SetEquals(expected))
            Fail($"{name}: expected blaze-cs to rebuild {string.Join(", ", expected)}, it rebuilt {string.Join(", ", rebuilt.Order())}:\n{output}");
        if (command == "test")
            RanTests(name, output, expected);
        Verify(name, rebuilt);
    }
    passed = true;
}
catch (CheckFailed failure)
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}
finally
{
    Run(blaze, "stop", check: false);
    var log = Path.Combine(work, ".blaze-cs", "daemon.log");
    if (!passed && File.Exists(log))
        Console.Error.WriteLine($"daemon log ({log}):\n{File.ReadAllText(log)[^Math.Min(4000, (int)new FileInfo(log).Length)..]}");
}
return 0;

void Replace(string path, string old, string replacement)
{
    var file = Path.Combine(work, path);
    var text = File.ReadAllText(file);
    if (text.Split(old).Length != 2)
        Fail($"{path}: expected one {old}");
    File.WriteAllText(file, text.Replace(old, replacement));
}

// The projects blaze-cs reports it compiled; any fallback to MSBuild fails the check.
HashSet<string> Rebuilt(string output)
{
    if (output.Contains("running dotnet build"))
        Fail($"blaze-cs fell back to dotnet build:\n{output}");
    if (Regex.IsMatch(output, "^blaze-cs: up to date", RegexOptions.Multiline))
        return [];
    var match = Regex.Match(output, @"^blaze-cs: rebuilt (.+?) in \d+ ms", RegexOptions.Multiline);
    if (!match.Success)
        Fail($"no rebuilt line in blaze-cs output:\n{output}");
    return [.. match.Groups[1].Value.Split(", ")];
}

// The test projects a run reports, from their summary lines ("Passed!  - Failed: 0, Passed: 2, ... - Tests.dll",
// printed alike by the warm host and dotnet test): exactly the expected ones, once each, with both tests passed.
void RanTests(string name, string output, string[] expected)
{
    var summaries = Regex.Matches(output, @"^\s*(?:Passed|Failed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+), .* - (\S+)\.dll\b", RegexOptions.Multiline)
        .Select(m => (Project: m.Groups[3].Value, Failed: int.Parse(m.Groups[1].Value), Passed: int.Parse(m.Groups[2].Value)))
        .ToList();
    if (!summaries.Select(s => s.Project).Order().SequenceEqual(expected.Order()))
        Fail($"{name}: expected tests of {string.Join(", ", expected)} to run, ran {string.Join(", ", summaries.Select(s => s.Project).Order())}:\n{output}");
    if (summaries.FirstOrDefault(s => s is not { Failed: 0, Passed: 2 }) is { Project: not null } wrong)
        Fail($"{name}: {wrong.Project}: expected 2 tests passed and none failed, got {wrong.Passed} passed, {wrong.Failed} failed:\n{output}");
}

// Compares what blaze-cs wrote with a full MSBuild rebuild of the same sources.
void Verify(string name, HashSet<string> rebuilt)
{
    var ours = Assemblies();
    Run("dotnet", "build Fixture.slnx -nologo -v:q -clp:ErrorsOnly --no-incremental");
    var theirs = Assemblies();
    if (!ours.Keys.ToHashSet().SetEquals(theirs.Keys))
        Fail($"{name}: assembly sets differ: {string.Join(", ", ours.Keys.ToHashSet().Except(theirs.Keys).Union(theirs.Keys.Except(ours.Keys)))}");
    var differing = ours.Keys.Where(path => !ours[path].AsSpan().SequenceEqual(theirs[path])).ToList();
    var inRebuilt = differing.Where(path => CompiledBy(rebuilt, path)).ToList();
    if (inRebuilt.Count > 0)
        Fail($"{name}: blaze-cs compiled these but they differ from dotnet build: {string.Join(", ", inRebuilt)}");
    var beyondIdentity = differing.Where(path => !PeIdentity.SameCode(ours[path], theirs[path])).ToList();
    if (beyondIdentity.Count > 0)
        Fail($"{name}: skipped dependents differ beyond debug identity: {string.Join(", ", beyondIdentity)}");
    Console.WriteLine($"ok  {name}: rebuilt {(rebuilt.Count == 0 ? "nothing" : string.Join(", ", rebuilt.Order()))}; "
        + $"{ours.Count - differing.Count} of {ours.Count} assemblies identical, {differing.Count} same code");
}

// Whether blaze-cs compiled an obj/ assembly (Project/obj/<configuration>/<tfm>/...): its unit is named
// "Project", or "Project (tfm)" when the project multi-targets.
static bool CompiledBy(HashSet<string> rebuilt, string path) => path.Split('/') is [var project, "obj", _, var framework, ..]
    && (rebuilt.Contains(project) || rebuilt.Contains($"{project} ({framework})"));

Dictionary<string, byte[]> Assemblies() =>
    Directory.EnumerateFiles(work, "*.dll", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(work, path))
        .Where(path => path.Split('/') is [_, "obj", ..] parts && !parts.Contains("refint"))
        .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(work, path)));

string Run(string file, string arguments, bool check = true)
{
    var start = new ProcessStartInfo(file, arguments) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd() + stderr.Result;
    process.WaitForExit();
    if (check && process.ExitCode != 0)
        Fail($"{file} {arguments} exited {process.ExitCode}:\n{output}");
    return output;
}

static void CopyFixture(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.EnumerateFiles(from).Where(f => Path.GetFileName(f) != "check.cs"))
        File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (var directory in Directory.EnumerateDirectories(from).Where(d => Path.GetFileName(d) is not ("bin" or "obj" or ".blaze-cs")))
        CopyFixture(directory, Path.Combine(to, Path.GetFileName(directory)));
}

// Thrown rather than exiting, so the finally above still stops the daemon: one left running would
// serve the next check with this run's build of blaze-cs.
[System.Diagnostics.CodeAnalysis.DoesNotReturn]
static void Fail(string message) => throw new CheckFailed(message);

sealed class CheckFailed(string message) : Exception(message);
