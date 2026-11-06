#!/usr/bin/env dotnet
#:include ../../scripts/PeIdentity.cs
// Regression check: makes each kind of edit to a copy of this fixture, rebuilds it with blaze-cs, and fails
// unless (1) blaze-cs rebuilt exactly the expected projects, or took the expected fallback to dotnet build,
// and (2) every assembly and scoped CSS bundle is what `dotnet build --no-incremental` produces. An assembly
// may differ only in debug identity (PeIdentity), and only in a project blaze-cs skipped: its PDB still
// records the old MVID of the reference that changed. After a fallback, nothing may differ.
//
// usage: task check   (or: dotnet run tests/fixture/check.cs, after `dotnet build BlazeCs.slnx`)
using System.Diagnostics;
using System.Text.RegularExpressions;

var fixture = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "tests/fixture/check.cs"))!;
var root = Path.GetFullPath(Path.Combine(fixture, "../.."));
var work = Path.Combine(Path.GetTempPath(), "blaze-cs-fixture");
var blaze = Path.Combine(root, OperatingSystem.IsWindows() ? "blaze-cs.cmd" : "blaze-cs");

// BLAZE_CS_SDK=<version> checks against another SDK than the repo's pin, such as a .NET 11 one.
var sdk = Environment.GetEnvironmentVariable("BLAZE_CS_SDK") is { Length: > 0 } pinned ? pinned : null;
// Tests11 (net11.0) joins the solution only under a .NET 11 or newer SDK; the pin is 10.
var hasNet11 = sdk is not null && Version.Parse(sdk.Split('-')[0]).Major >= 11;

// (name, edit, units blaze-cs must rebuild, command, fallback); the edits build on each other. Lib and
// Consumer target net8.0 and net10.0, and blaze-cs names each target framework's unit "Project (tfm)".
// A case with a fallback must instead log it ("blaze-cs: <fallback>") and run dotnet build.
string[] lib = ["Lib (net10.0)", "Lib (net8.0)"], consumer = ["Consumer (net10.0)", "Consumer (net8.0)"];
Case[] cases =
[
    new("body", () => Replace("Lib/Greeter.cs", "\"Hello, \"", "\"Hello there, \""), lib, "build"),
    new("signature used by Consumer",
        () => Replace("Lib/Greeter.cs", "Greet(string name) => \"Hello there, \" + name", "Greet(string person) => \"Hello there, \" + person"),
        [.. consumer, .. lib], "build"),
    new("signature unused by Consumer",
        () => Replace("Lib/Greeter.cs", "Twice(int value) => value * 2", "Twice(int amount) => amount * 2"), lib, "build"),
    // Lib (net8.0) recompiles because its source changed, but its API doesn't, so Consumer (net8.0) doesn't.
    new("net10.0-only signature",
        () => Replace("Lib/Greeter.cs", "Shout(string name) => name.ToUpperInvariant()", "Shout(string who) => who.ToUpperInvariant()"),
        ["Consumer (net10.0)", .. lib], "build"),
    new("new file",
        () => File.WriteAllText(Path.Combine(work, "Lib/Probe.cs"), "namespace Lib;\n\ninternal static class Probe\n{\n    internal const int Edit = 1;\n}\n"),
        lib, "build"),
    new("generator output",
        () => Replace("Lib/Greeter.cs", "    public static int Twice",
            "    public static string Farewell(string name) => \"Bye, \" + name;\n\n    public static int Twice"),
        [.. consumer, .. lib], "build"),
    // A generator in the solution goes through dotnet build; the next compile must run its new build.
    new("generator edit", () => Replace("Generator/GreeterMembersGenerator.cs", "string.Join(\",\",", "string.Join(\";\","),
        [], "build", "Generator: it is an analyzer other projects load; running dotnet build"),
    new("generator output after generator edit",
        () => Replace("Lib/Greeter.cs", "    public static int Twice",
            "    public static string Wave(string name) => \"Hi, \" + name;\n\n    public static int Twice"),
        [.. consumer, .. lib], "build"),
    // A test run must also show each rebuilt test project's tests passing (RanTests).
    new("xUnit v3 test edit", () => Replace("Tests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["Tests"], "test"),
    new("NUnit test edit", () => Replace("NUnitTests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["NUnitTests"], "test"),
    new("MSTest test edit", () => Replace("MSTestTests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["MSTestTests"], "test"),
    .. (hasNet11 ? new Case[] { new Case("net11.0 test edit", () => Replace("Tests11/RuntimeTests.cs", "Edit = 0", "Edit = 1"), ["Tests11"], "test") } : []),
    // Razor (Components): the Razor generator compiles .razor files, which csc gets as /additionalfile.
    new("razor markup", () => Replace("Components/Greeting.razor", "<Badge>@Text(Name)</Badge>", "<Badge><em>@Text(Name)</em></Badge>"),
        ["Components"], "build"),
    new("razor @code API used by ComponentTests",
        () => Replace("Components/Greeting.razor", "    [Parameter]\n    public string Name", "    [Parameter, EditorRequired]\n    public string Name"),
        ["ComponentTests", "Components"], "build"),
    new("razor @code API unused",
        () => Replace("Components/Badge.razor", "    public RenderFragment? ChildContent { get; set; }\n",
            "    public RenderFragment? ChildContent { get; set; }\n\n    [Parameter]\n    public string? Tone { get; set; }\n"),
        ["Components"], "build"),
    new("_Imports.razor", () => File.AppendAllText(Path.Combine(work, "Components/_Imports.razor"), "@using Consumer\n"), ["Components"], "build"),
    new("component test edit", () => Replace("ComponentTests/GreetingTests.cs", "\"reader\"", "\"tester\""), ["ComponentTests"], "test"),
    // Only MSBuild lists a new .razor for the generator, with its metadata in the generated .editorconfig.
    new("new razor file",
        () => File.WriteAllText(Path.Combine(work, "Components/Farewell.razor"), "<p>Bye, @Who</p>\n\n@code {\n    [Parameter]\n    public string Who { get; set; } = \"\";\n}\n"),
        [], "build", "Components: Farewell.razor is new; running dotnet build"),
    new("razor file added by the last fallback", () => Replace("Components/Farewell.razor", "<p>Bye, @Who</p>", "<p>Goodbye, @Who</p>"), ["Components"], "build"),
    // MSBuild rewrites and bundles scoped CSS; the compiled component only carries its scope.
    new("scoped css", () => Replace("Components/Badge.razor.css", "font-weight: bold;", "font-weight: bold;\n    color: green;"),
        [], "build", "Components/Badge.razor.css changed, running dotnet build"),
    new("deleted razor file", () => File.Delete(Path.Combine(work, "Components/Farewell.razor")),
        [], "build", "Components: Farewell.razor was deleted; running dotnet build"),
];

if (Directory.Exists(work))
    Directory.Delete(work, recursive: true);
CopyFixture(fixture, work);
if (sdk is not null)
    File.WriteAllText(Path.Combine(work, "global.json"),
        $$"""{ "sdk": { "version": "{{sdk}}", "allowPrerelease": true, "rollForward": "latestFeature" } }""");
else
    File.Copy(Path.Combine(root, "global.json"), Path.Combine(work, "global.json"));
if (hasNet11)
    Replace("Fixture.slnx", "</Solution>", "  <Project Path=\"Tests11/Tests11.csproj\" />\n</Solution>");
else
    Directory.Delete(Path.Combine(work, "Tests11"), recursive: true);
var passed = false;
try
{
    Run(blaze, "build"); // starts the daemon: a real dotnet build, then a warm-up compile
    foreach (var (name, edit, expected, command, fallback) in cases)
    {
        edit();
        var output = Run(blaze, command);
        var rebuilt = fallback is null ? Rebuilt(output) : FellBack(name, output, fallback);
        if (!rebuilt.SetEquals(expected))
            Fail($"{name}: expected blaze-cs to rebuild {string.Join(", ", expected)}, it rebuilt {string.Join(", ", rebuilt.Order())}:\n{output}");
        if (command == "test")
            RanTests(name, output, expected);
        Verify(name, rebuilt, fallback);
    }
    HostCannotLoad();
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
    {
        // Shared for writing: on Windows the daemon's launcher may still hold it.
        var text = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)).ReadToEnd();
        Console.Error.WriteLine($"daemon log ({log}):\n{text[^Math.Min(4000, text.Length)..]}");
    }
}
return 0;

// A test assembly the warm host can't load (here, a missing reference) fails the run within seconds, naming
// the assembly and the cause, and the daemon still serves the next run.
void HostCannotLoad()
{
    var reference = Directory.EnumerateFiles(Path.Combine(work, "Tests", "bin"), "Consumer.dll", SearchOption.AllDirectories).Single();
    var kept = File.ReadAllBytes(reference);
    File.Delete(reference);
    Replace("Tests/WelcomeTests.cs", "\"tester\"", "\"loader\"");
    var stopwatch = Stopwatch.StartNew();
    string output;
    int exitCode;
    try
    {
        (output, exitCode) = RunWithExit(blaze, "test", check: false);
    }
    finally
    {
        File.WriteAllBytes(reference, kept);
        Replace("Tests/WelcomeTests.cs", "\"loader\"", "\"tester\"");
    }
    var failedIn = stopwatch.Elapsed;
    if (exitCode == 0 || failedIn > TimeSpan.FromSeconds(30) || !Regex.IsMatch(output, @"^blaze-cs: Tests\.dll: .*Consumer", RegexOptions.Multiline))
        Fail($"host that can't load Tests.dll: expected a failure naming Tests.dll and Consumer within 30 s, got exit {exitCode} after {failedIn.TotalSeconds:F0} s:\n{output}");
    RanTests("test after a host that can't load", Run(blaze, "test"), ["Tests"]);
    Console.WriteLine($"ok  host that can't load an assembly: failed in {failedIn.TotalSeconds:F1} s; the next run passed");
}

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

// A case that must go through dotnet build: blaze-cs logs why, and then reloads with every assembly
// identical to that build's. The projects MSBuild compiled aren't reported, so the set is empty.
HashSet<string> FellBack(string name, string output, string fallback)
{
    // Its message names paths with this OS's separator.
    if (!output.Contains($"blaze-cs: {fallback.Replace('/', Path.DirectorySeparatorChar)}"))
        Fail($"{name}: expected blaze-cs to log \"{fallback}\":\n{output}");
    // The last warm-up: one that finds stale cached arguments reloads with fresh ones.
    var warmUp = Regex.Matches(output, @"^blaze-cs: warmed up in \d+ ms; (\d+)/(\d+) assemblies identical", RegexOptions.Multiline).LastOrDefault();
    if (warmUp is null || warmUp.Groups[1].Value != warmUp.Groups[2].Value)
        Fail($"{name}: expected blaze-cs to reload with every assembly identical to dotnet build's:\n{output}");
    return [];
}

// The test projects a run reports, from their summary lines ("Passed!  - Failed: 0, Passed: 2, ... - Tests.dll",
// printed alike by the warm host and dotnet test): exactly the expected ones, once each, with all their tests
// passed, and none through dotnet test, since the warm host must run them on the runtime each one targets.
void RanTests(string name, string output, string[] expected)
{
    var summaries = Regex.Matches(output, @"^\s*(?:Passed|Failed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+), .* - (\S+)\.dll\b", RegexOptions.Multiline)
        .Select(m => (Project: m.Groups[3].Value, Failed: int.Parse(m.Groups[1].Value), Passed: int.Parse(m.Groups[2].Value)))
        .ToList();
    if (!summaries.Select(s => s.Project).Order().SequenceEqual(expected.Order()))
        Fail($"{name}: expected tests of {string.Join(", ", expected)} to run, ran {string.Join(", ", summaries.Select(s => s.Project).Order())}:\n{output}");
    if (output.Contains("running dotnet test"))
        Fail($"{name}: expected the test host to run these, not dotnet test:\n{output}");
    if (summaries.FirstOrDefault(s => s.Failed != 0 || s.Passed != TestCount(s.Project)) is { Project: not null } wrong)
        Fail($"{name}: {wrong.Project}: expected {TestCount(wrong.Project)} tests passed and none failed, got {wrong.Passed} passed, {wrong.Failed} failed:\n{output}");
}

// Each test project has its two tests and RunsOnTargetRuntime, except these.
static int TestCount(string project) => project switch { "ComponentTests" => 2, "Tests11" => 1, _ => 3 };

// Compares what blaze-cs wrote with a full MSBuild rebuild of the same sources. After a fallback, MSBuild
// built everything, so every output must be identical.
void Verify(string name, HashSet<string> rebuilt, string? fallback)
{
    var ours = Outputs();
    Run("dotnet", "build Fixture.slnx -nologo -v:q -clp:ErrorsOnly --no-incremental");
    var theirs = Outputs();
    if (!ours.Keys.ToHashSet().SetEquals(theirs.Keys))
        Fail($"{name}: output sets differ: {string.Join(", ", ours.Keys.ToHashSet().Except(theirs.Keys).Union(theirs.Keys.Except(ours.Keys)))}");
    var differing = ours.Keys.Where(path => !ours[path].AsSpan().SequenceEqual(theirs[path])).ToList();
    var inRebuilt = differing.Where(path => fallback is not null || CompiledBy(rebuilt, path) || !path.EndsWith(".dll", StringComparison.Ordinal)).ToList();
    if (inRebuilt.Count > 0)
        Fail($"{name}: blaze-cs or its fallback wrote these, but they differ from dotnet build: {string.Join(", ", inRebuilt)}");
    var beyondIdentity = differing.Where(path => !PeIdentity.SameCode(ours[path], theirs[path])).ToList();
    if (beyondIdentity.Count > 0)
        Fail($"{name}: skipped dependents differ beyond debug identity: {string.Join(", ", beyondIdentity)}");
    Console.WriteLine($"ok  {name}: {(fallback is not null ? "dotnet build" : "rebuilt " + (rebuilt.Count == 0 ? "nothing" : string.Join(", ", rebuilt.Order())))}; "
        + $"{ours.Count - differing.Count} of {ours.Count} outputs identical, {differing.Count} same code");
}

// Whether blaze-cs compiled an obj/ assembly (Project/obj/<configuration>/<tfm>/...): its unit is named
// "Project", or "Project (tfm)" when the project multi-targets.
static bool CompiledBy(HashSet<string> rebuilt, string path) => path.Split('/') is [var project, "obj", _, var framework, ..]
    && (rebuilt.Contains(project) || rebuilt.Contains($"{project} ({framework})"));

// Every obj/ assembly, and the scoped CSS that MSBuild rewrites and bundles (obj/.../scopedcss/), by
// relative path with / separators on every OS.
Dictionary<string, byte[]> Outputs() =>
    Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(work, path).Replace('\\', '/'))
        .Where(path => path.Split('/') is [_, "obj", ..] parts && !parts.Contains("refint")
            && (path.EndsWith(".dll", StringComparison.Ordinal) || parts.Contains("scopedcss") && path.EndsWith(".css", StringComparison.Ordinal)))
        .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(work, path)));

string Run(string file, string arguments, bool check = true) => RunWithExit(file, arguments, check).Output;

(string Output, int ExitCode) RunWithExit(string file, string arguments, bool check = true)
{
    var start = new ProcessStartInfo(file, arguments) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var output = process.StandardOutput.ReadToEnd() + stderr.Result;
    process.WaitForExit();
    if (check && process.ExitCode != 0)
        Fail($"{file} {arguments} exited {process.ExitCode}:\n{output}");
    return (output, process.ExitCode);
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

sealed record Case(string Name, Action Edit, string[] Expected, string Command, string? Fallback = null);
