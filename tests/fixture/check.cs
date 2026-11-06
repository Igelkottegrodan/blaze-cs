#!/usr/bin/env dotnet
#:include ../../src/BlazeCs/PeIdentity.cs
// Regression check: makes each kind of edit to a copy of this fixture, rebuilds it with blaze-cs, and fails
// unless (1) blaze-cs rebuilt exactly the expected projects, or took the expected fallback to dotnet build,
// and (2) every assembly and scoped CSS bundle is what `dotnet build --no-incremental` produces. A `test` must
// also run exactly the test projects whose inputs changed since they last passed, and list the rest as unchanged. An assembly
// may differ only in debug identity (PeIdentity), and only in a project blaze-cs skipped: its PDB still
// records the old MVID of the reference that changed. After a fallback, nothing may differ.
//
// usage: task check   (or: dotnet run tests/fixture/check.cs, after `dotnet build BlazeCs.slnx`;
// or BLAZE_CS=<installed blaze-cs> dotnet run tests/fixture/check.cs, after `task pack` and `dotnet tool install`)
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

var fixture = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "tests/fixture/check.cs"))!;
var root = Path.GetFullPath(Path.Combine(fixture, "../.."));
var work = Path.Combine(Path.GetTempPath(), "blaze-cs-fixture");
var otherBuild = work + "-other-build";
// BLAZE_CS=<path> runs that blaze-cs command, such as an installed tool's, instead of the launcher built here.
var blaze = Environment.GetEnvironmentVariable("BLAZE_CS") is { Length: > 0 } given
    ? given
    : Path.Combine(root, OperatingSystem.IsWindows() ? "blaze-cs.cmd" : "blaze-cs");

// BLAZE_CS_SDK=<exact version> checks against another SDK than the repo's pin, such as a .NET 11 one or the
// oldest supported .NET 10 (rollForward is off: a newer installed SDK must not stand in for it).
var sdk = Environment.GetEnvironmentVariable("BLAZE_CS_SDK") is { Length: > 0 } pinned ? pinned : null;
// Tests11 (net11.0) joins the solution only under a .NET 11 or newer SDK; the pin is 10.
var hasNet11 = sdk is not null && Version.Parse(sdk.Split('-')[0]).Major >= 11;

// (name, edit, units blaze-cs must rebuild, command, fallback); the edits build on each other. Lib and
// Consumer target net8.0 and net10.0, and blaze-cs names each target framework's unit "Project (tfm)".
// A case with a fallback must instead log it ("blaze-cs: <fallback>") and run dotnet build.
string[] lib = ["Lib (net10.0)", "Lib (net8.0)"], consumer = ["Consumer (net10.0)", "Consumer (net8.0)"];
// The test projects, and those of them that run with Lib's net10.0 build copied next to them.
string[] libTests = ["Tests", "XunitV2Tests", "NUnitTests", "MSTestTests", "ComponentTests"], allTests = [.. libTests, .. (hasNet11 ? ["Tests11"] : Array.Empty<string>())];
// Idle test hosts the daemon keeps, one per test project (TestRunner.MaxIdleHosts).
const int HostCap = 4;
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
    new("constant used by App",
        () =>
        {
            Replace("Lib/Greeter.cs", "    public static int Twice", "    public const int Answer = 1;\n\n    public static int Twice");
            File.AppendAllText(Path.Combine(work, "App/Program.cs"), "Console.WriteLine(Lib.Greeter.Answer);\n");
        },
        ["App", .. lib], "build"),
    // One edit adds a member to Lib and uses it in Consumer, whose API doesn't change. Consumer may compile
    // before Lib's new image is out, fail, and block App (which reads Lib's changed constant through it);
    // once Consumer compiles against the new image, App must still run.
    new("new member used by Consumer in the same edit",
        () =>
        {
            Replace("Lib/Greeter.cs", "Answer = 1;", "Answer = 2;\n\n    public const string Probe = \"p\";");
            Replace("Consumer/Welcome.cs", "=> Lib.Greeter.Greet(who);", "=> Lib.Greeter.Greet(who) + Lib.Greeter.Probe;");
        },
        ["App", .. consumer, .. lib], "build"),
    // A test run must also run exactly the test projects in Ran (by default, the rebuilt ones), list the others as
    // unchanged since their last run, and show every test passing (RanTests). Each test project runs the first time.
    new("first test run", () => { }, [], "test", Ran: allTests),
    new("xUnit v3 test edit", () => Replace("Tests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["Tests"], "test"),
    new("xUnit v2 test edit", () => Replace("XunitV2Tests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["XunitV2Tests"], "test"),
    // NUnitTests and MSTestTests have a .runsettings the host applies: a test checks its variable and parameter.
    // NUnitTests' is in its directory and its project file names it; MSTestTests' is set by a Directory.Build.props
    // and is in no project directory. XunitV2Tests has a stray .runsettings that nothing names, which dotnet test
    // ignores: the host must run it, not send it to dotnet test (RanTests).
    new("NUnit test edit", () => Replace("NUnitTests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["NUnitTests"], "test"),
    new("MSTest test edit", () => Replace("MSTestTests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["MSTestTests"], "test"),
    .. (hasNet11 ? new Case[] { new Case("net11.0 test edit", () => Replace("Tests11/RuntimeTests.cs", "Edit = 0", "Edit = 1"), ["Tests11"], "test") } : []),
    new("test without edits", () => { }, [], "test", Ran: []),
    // The host applies NUnitTests' .runsettings, so an edit to it runs the project again.
    new("runsettings edit", () => Replace("NUnitTests/test.runsettings", "<RunSettings>\n", "<RunSettings>\n  <!-- edited -->\n"),
        [], "test", Ran: ["NUnitTests"]),
    new("test --all", () => { }, [], "test --all", Ran: allTests),
    // Lib.dll's PDB changes, and with it only Lib.dll's debug identity, so no test project runs.
    new("comment in Lib", () => Replace("Lib/Greeter.cs", "namespace Lib;\n", "namespace Lib;\n\n// Greetings and small helpers.\n"),
        lib, "test", Ran: []),
    new("body in Lib", () => Replace("Lib/Greeter.cs", "Twice(int amount) => amount * 2", "Twice(int amount) => amount + amount"),
        lib, "test", Ran: libTests),
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
    // A project file edit goes through dotnet build, but csc's arguments don't change: every unit stays
    // warm, and none needs a compile to be trusted, since MSBuild left each assembly as the daemon knew it.
    new("project file edit", () => Replace("Lib/Lib.csproj", "</Project>", "  <!-- edited -->\n</Project>"),
        [], "build", "Lib/Lib.csproj changed, running dotnet build", AllKept: true),
];

if (Directory.Exists(work))
    Directory.Delete(work, recursive: true);
CopyFixture(fixture, work);
if (sdk is not null)
    File.WriteAllText(Path.Combine(work, "global.json"),
        $$"""{ "sdk": { "version": "{{sdk}}", "allowPrerelease": true, "rollForward": "disable" } }""");
else
    File.Copy(Path.Combine(root, "global.json"), Path.Combine(work, "global.json"));
if (hasNet11)
    Replace("Fixture.slnx", "</Solution>", "  <Project Path=\"Tests11/Tests11.csproj\" />\n</Solution>");
else
    Directory.Delete(Path.Combine(work, "Tests11"), recursive: true);
var passed = false;
var loadedProjects = 0; // from the last "loaded N projects" line a command printed
try
{
    Run(blaze, "build"); // starts the daemon: a real dotnet build; the warm-up compile runs after it returns
    // The next command reports the warm-up, which must reproduce every assembly.
    WarmedUpIdentical("start", Run(blaze, "build"));
    var prestarted = IdleHosts("before any test command");
    Console.WriteLine($"ok  {prestarted.Count} idle test hosts before any test command, one each for {string.Join(", ", prestarted.Select(h => h.Project).Order())}, and a spare per runtime");
    foreach (var (name, edit, expected, command, fallback, allKept, ran) in cases)
    {
        edit();
        var output = Run(blaze, command);
        if (Regex.Matches(output, @"^blaze-cs: loaded (\d+) projects", RegexOptions.Multiline).LastOrDefault() is { } load)
            loadedProjects = int.Parse(load.Groups[1].Value);
        var rebuilt = fallback is null ? Rebuilt(output) : FellBack(name, output, fallback, allKept);
        if (!rebuilt.SetEquals(expected))
            Fail($"{name}: expected blaze-cs to rebuild {string.Join(", ", expected)}, it rebuilt {string.Join(", ", rebuilt.Order())}:\n{output}");
        if (command.StartsWith("test", StringComparison.Ordinal))
        {
            RanTests(name, output, ran ?? expected);
            if (name == "first test run")
                TookEveryHost(prestarted);
            else
                IdleHosts(name, ran ?? expected);
        }
        Verify(name, rebuilt, fallback);
    }
    IdleCollected();
    AnalyzerWarnings();
    CompilationEndAnalyzers();
    PreparedHostsSeeNewBytes();
    ProjectRemovedAndRestored();
    HostCannotLoad();
    RunSettingsTheHostDoesNotApply();
    FailedRunsAgain();
    AllTestsSkipped();
    WarmUpAfterFirstCommand();
    DaemonLifecycle();
    ParentFails();
    TouchedOutputLeftAlone();
    StopThenCommandAtOnce();
    HostsStopWithTheDaemon();
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
    if (Directory.Exists(otherBuild))
        Directory.Delete(otherBuild, recursive: true);
    if (!passed)
    {
        foreach (var name in new[] { "daemon.log", "daemon-start.log" })
        {
            var text = ReadLog(name);
            if (text.Length > 0)
                Console.Error.WriteLine($"{name} ({Path.Combine(work, ".blaze-cs", name)}):\n{text[^Math.Min(4000, text.Length)..]}");
        }
    }
}
return 0;

// Shared for writing and deleting: the daemon has its log open as it runs, and on Windows its launcher may
// still hold daemon-start.log.
string ReadLog(string name)
{
    var path = Path.Combine(work, ".blaze-cs", name);
    if (!File.Exists(path))
        return "";
    using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    return reader.ReadToEnd();
}

// The daemons that have started, oldest first, from the log line each writes.
List<(int Pid, string Socket)> Daemons() =>
    [.. Regex.Matches(ReadLog("daemon.log"), @"^\S+ serving .* on (.+) \(pid (\d+), exits after \d+ s idle\)", RegexOptions.Multiline)
        .Select(m => (int.Parse(m.Groups[2].Value), m.Groups[1].Value))];

bool Running(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

// A few seconds after a command the daemon collects, off the command's path, and logs when it ran, what it
// took and the heap it left, in the line IdleCollector.CollectAndLog documents. The cases' verifies
// usually leave it idle long enough already.
void IdleCollected()
{
    MatchCollection Collections() => Regex.Matches(ReadLog("daemon.log"),
        @"^\S+ blaze-cs: idle collection took (\d+) ms; heap (\d+) MB, committed (\d+) MB, working set (\d+) MB \(was (\d+) MB\)\r?$", RegexOptions.Multiline);
    var waiting = Stopwatch.StartNew();
    while (Collections().Count == 0 && waiting.Elapsed < TimeSpan.FromSeconds(30))
        Thread.Sleep(100);
    var collections = Collections();
    if (collections.Count == 0)
        Fail($"idle collection: expected the daemon to log a collection within 30 s of the last command:\n{ReadLog("daemon.log")}");
    var last = collections[^1].Groups;
    Console.WriteLine($"ok  idle collection: {collections.Count} logged, the last took {last[1].Value} ms and left a {last[2].Value} MB heap");
}

// A client from another build of blaze-cs stops the daemon the cases used and starts its own, with a notice;
// that daemon, started with a short idle limit, exits on its own and removes its socket; and the next
// command, from this repo's build again, starts a daemon without a word about it.
void DaemonLifecycle()
{
    // The daemons this step starts, after the ones before it.
    var earlier = Daemons().Count - 1;
    var first = Daemons().Last();
    // A copy of the build whose assembly has another MVID, which is its identity.
    CopyDirectory(Path.Combine(root, "artifacts", "bin"), otherBuild);
    var assembly = Path.Combine(otherBuild, "blaze-cs.dll");
    var bytes = File.ReadAllBytes(assembly);
    using (var pe = new PEReader(new MemoryStream(bytes)))
    {
        var metadata = pe.GetMetadataReader();
        bytes[bytes.AsSpan().IndexOf(metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToByteArray().AsSpan())] ^= 0xFF;
    }
    File.WriteAllBytes(assembly, bytes);

    var output = Run("dotnet", $"\"{assembly}\" build", environment: new() { ["BLAZE_CS_IDLE_SECONDS"] = "5" });
    var daemons = Daemons()[earlier..];
    if (!output.Contains("blaze-cs: the running daemon is from another build") || daemons.Count != 2 || daemons[0] != first)
        Fail($"daemon handshake: expected a notice and a second daemon after the first ({first.Pid}), got {daemons.Count} daemons:\n{output}");
    if (Running(first.Pid))
        Fail($"daemon handshake: expected daemon {first.Pid}, from another build, to have exited before the command ran:\n{output}");

    var idle = daemons[1];
    var waiting = Stopwatch.StartNew();
    while (Running(idle.Pid) && waiting.Elapsed < TimeSpan.FromSeconds(60))
        Thread.Sleep(100);
    if (Running(idle.Pid) || File.Exists(idle.Socket) || !ReadLog("daemon.log").Contains("no command for 5 s; exiting"))
        Fail($"idle exit: expected daemon {idle.Pid} to log that it was idle for 5 s, exit and remove {idle.Socket} within 60 s:\n{ReadLog("daemon.log")}");

    output = Run(blaze, "build");
    daemons = Daemons()[earlier..];
    if (output.Contains("another build") || daemons.Count != 3 || !Running(daemons[2].Pid) || !output.Contains("blaze-cs: warming up in the background"))
        Fail($"after an idle exit: expected the next command to start a daemon without a notice and build, got {daemons.Count} daemons:\n{output}");

    // The same handover through the native client (a build has one unless it was packed as a tool): first as
    // the client of another build, which reads its identity from the blaze-cs.dll beside it, then as this
    // repo's launcher, whose client is the native one unless BLAZE_CS names an installed tool. Each hands
    // the daemon over with a notice and has the CLI start the next one.
    var native = Path.Combine(otherBuild, OperatingSystem.IsWindows() ? "blaze-cs-client.exe" : "blaze-cs-client");
    var nativeNote = "";
    if (File.Exists(native))
    {
        Handover(native, daemons[2].Pid, earlier + 4, new() { ["BLAZE_CS_IDLE_SECONDS"] = "5" });
        Handover(blaze, Daemons()[earlier + 3].Pid, earlier + 5, null);
        nativeNote = "; the same through the native client";
    }
    Console.WriteLine($"ok  daemon lifecycle: another build restarted daemon {first.Pid} as {idle.Pid}, which exited on its own after 5 s idle; the next command started {daemons[2].Pid}{nativeNote}");
}

// A command whose build differs from the running daemon's: a notice, that daemon gone, one more daemon.
void Handover(string client, int old, int expectedDaemons, Dictionary<string, string>? environment)
{
    var output = Run(client, "build", environment: environment);
    var daemons = Daemons();
    if (!output.Contains("blaze-cs: the running daemon is from another build") || daemons.Count != expectedDaemons || Running(old))
        Fail($"daemon handshake through {Path.GetFileName(client)}: expected a notice, daemon {old} gone and {expectedDaemons} daemons, got {daemons.Count}:\n{output}");
}

// A daemon's first command returns once the solution is loaded, and the warm-up compile finishes after it;
// the next command waits for it and reports it. Here a build outside blaze-cs compiled Lib with a new file
// while no daemon ran, so Lib's cached csc arguments are stale: the warm-up finds that, and the next command
// reloads with fresh ones (dotnet build --no-incremental) before it builds.
void WarmUpAfterFirstCommand()
{
    Run(blaze, "stop");
    File.WriteAllText(Path.Combine(work, "Lib/Outside.cs"), "namespace Lib;\n\ninternal static class Outside\n{\n}\n");
    Run("dotnet", "build Fixture.slnx -nologo -v:q -clp:ErrorsOnly");

    var first = Run(blaze, "build");
    var returned = DateTimeOffset.Now;
    if (!first.Contains("blaze-cs: warming up in the background") || first.Contains("warmed up") || first.Contains("stale"))
        Fail($"first command: expected it to return once loaded, before the warm-up reports:\n{first}");

    var next = Run(blaze, "build");
    var finished = Regex.Matches(ReadLog("daemon.log"), @"^(\S+) blaze-cs: warmed up in", RegexOptions.Multiline).LastOrDefault();
    if (finished is null || DateTimeOffset.Parse(finished.Groups[1].Value) <= returned)
        Fail($"first command: expected the warm-up to finish after it returned at {returned:O}:\n{ReadLog("daemon.log")}");
    var warmUps = Regex.Matches(next, @"^blaze-cs: warmed up in \d+ ms; (\d+)/(\d+) assemblies identical", RegexOptions.Multiline);
    var stale = next.IndexOf("its cached compiler arguments are stale; running dotnet build --no-incremental", StringComparison.Ordinal);
    if (warmUps.Count != 2 || warmUps[0].Groups[1].Value == warmUps[0].Groups[2].Value || warmUps[1].Groups[1].Value != warmUps[1].Groups[2].Value
        || !Regex.IsMatch(next, @"^blaze-cs: Lib \(net\d+\.0\): its cached", RegexOptions.Multiline)
        || !(warmUps[0].Index < stale && stale < warmUps[1].Index) || !Regex.IsMatch(next, "^blaze-cs: up to date", RegexOptions.Multiline))
        Fail($"next command: expected the first warm-up's report, Lib's stale arguments, a reload with every assembly identical, then a build:\n{next}");
    Verify("warm-up after the first command", [], "reload");
}

static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.EnumerateFiles(from))
        File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (var directory in Directory.EnumerateDirectories(from))
        CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
}

// A build reports the analyzer warnings dotnet build reports for the files the edit touched, with the same id,
// message and location, and with the severity .editorconfig gives each file: CA1825 is a warning in Lib's files
// but off for Lib/Quiet.cs, and off in all of Consumer (Consumer/.editorconfig). A build without edits, and one
// whose edit fixes the warning, report none.
void AnalyzerWarnings()
{
    const string greeter = "    public static string[] Nobody() => new string[0];\n\n";
    const string welcome = "    public static int[] NoOne() => new int[0];\n\n";
    Replace("Lib/Greeter.cs", "    public static int Twice", greeter + "    public static int Twice");
    Replace("Consumer/Welcome.cs", "    // Generated by Generator", welcome + "    // Generated by Generator");
    File.WriteAllText(Path.Combine(work, "Lib/Quiet.cs"), "namespace Lib;\n\ninternal static class Quiet\n{\n    internal static int[] None() => new int[0];\n}\n");
    var output = Run(blaze, "build");
    var rebuilt = Rebuilt(output);
    if (!rebuilt.SetEquals([.. consumer, .. lib]))
        Fail($"analyzer warnings: expected blaze-cs to rebuild {string.Join(", ", [.. consumer, .. lib])}, it rebuilt {string.Join(", ", rebuilt.Order())}:\n{output}");
    var ours = Warnings(output, "CA1825");
    var theirs = Warnings(Verify("analyzer warnings", rebuilt, null), "CA1825");
    var greeterPath = Path.Combine(work, "Lib", "Greeter.cs");
    // dotnet build logs each warning twice (as its project builds, and again for the solution).
    if (ours.Count != lib.Length || !ours.All(w => w.StartsWith(greeterPath + "(", StringComparison.Ordinal)) || !ours.Distinct().SequenceEqual(theirs.Distinct()))
        Fail($"analyzer warnings: expected blaze-cs to report CA1825 in {greeterPath} once per target framework, with the id, message and location dotnet build reports, "
            + $"got:\n{string.Join("\n", ours)}\ndotnet build reported:\n{string.Join("\n", theirs)}\nblaze-cs output:\n{output}");

    output = Run(blaze, "build");
    if (Warnings(output, "CA1825").Count != 0 || !Rebuilt(output).SetEquals([]))
        Fail($"analyzer warnings, built again: expected nothing rebuilt and no CA1825:\n{output}");

    // An edit inside the body of a member above it analyzes only that member again: the warning is reported
    // again where it moved to, as dotnet build reports it.
    Replace("Lib/Greeter.cs", "=> \"Bye, \" + name;", "=> \"Bye, \"\n        + name;");
    output = Run(blaze, "build");
    rebuilt = Rebuilt(output);
    var moved = Warnings(output, "CA1825");
    theirs = Warnings(Verify("analyzer warnings, body edit above", rebuilt, null), "CA1825");
    if (!rebuilt.SetEquals(lib) || moved.Count != lib.Length || moved.SequenceEqual(ours) || !moved.Distinct().SequenceEqual(theirs.Distinct()))
        Fail($"analyzer warnings, body edit above: expected Lib rebuilt and CA1825 one line further down, as dotnet build reports it, got:\n{string.Join("\n", moved)}\n"
            + $"dotnet build reported:\n{string.Join("\n", theirs)}\nblaze-cs output:\n{output}");
    Replace("Lib/Greeter.cs", greeter, "");
    Replace("Consumer/Welcome.cs", welcome, "");
    File.Delete(Path.Combine(work, "Lib/Quiet.cs"));
    output = Run(blaze, "build");
    if (Warnings(output, "CA1825").Count != 0 || !Rebuilt(output).SetEquals([.. consumer, .. lib]))
        Fail($"analyzer warnings, fixed: expected {string.Join(", ", [.. consumer, .. lib])} rebuilt and no CA1825:\n{output}");
    Console.WriteLine($"ok  analyzer warnings: {ours.Count} CA1825 warnings, as dotnet build reports them, and where they moved after a body edit above; none where .editorconfig turns it off, none once fixed");
}

// Compilation-end rules need the whole compilation: CA1812 (an internal class never instantiated, a warning in Lib,
// see .editorconfig). A build doesn't report it. The background analysis that runs once the daemon is idle does,
// after the next command's build and once only, under a line saying it comes from that run and when it finished;
// `build --all` reports it at once. Each time with the id, message and location dotnet build reports. A result for
// a project the next command changes is dropped, and the project analyzed again. A fallback dotnet build prints
// errors only, so what it compiled, and what was pending, is analyzed after it.
void CompilationEndAnalyzers()
{
    var unused = Path.Combine(work, "Lib", "Unused.cs");
    const string unusedClass = "namespace Lib;\n\ninternal sealed class Unused\n{\n}\n";
    File.WriteAllText(unused, unusedClass);
    var output = Run(blaze, "build");
    var built = DateTimeOffset.Now;
    if (!Rebuilt(output).SetEquals(lib) || Warnings(output, "CA1812").Count != 0)
        Fail($"compilation-end analyzers: expected {string.Join(", ", lib)} rebuilt and no CA1812 from the build itself:\n{output}");
    var theirs = Warnings(Verify("compilation-end analyzers", Rebuilt(output), null), "CA1812").Distinct().ToList();
    if (theirs.Count != 1 || !theirs[0].StartsWith(unused + "(", StringComparison.Ordinal))
        Fail($"compilation-end analyzers: expected dotnet build to report CA1812 in {unused}, it reported:\n{string.Join("\n", theirs)}");
    WaitForBackgroundAnalysis("compilation-end analyzers", built);
    CollectedAfterBackgroundAnalysis("compilation-end analyzers, idle collection", built);

    // Its report comes after the command's own build, its warnings under the line that marks them.
    output = Run(blaze, "build");
    var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    var header = lines.FindIndex(l => Regex.IsMatch(l, @"^blaze-cs: background analysis \(compilation-end rules\) of .*, finished at \d\d:\d\d:\d\d in \d+ ms: 2 warning\(s\)$"));
    var ours = Warnings(output, "CA1812");
    var upToDate = lines.FindIndex(l => l.StartsWith("blaze-cs: up to date", StringComparison.Ordinal));
    if (header < 0 || !lib.All(lines[header].Contains) || ours.Count != lib.Length || !ours.Distinct().SequenceEqual(theirs)
        || !lines.Skip(header + 1).Take(lib.Length).All(l => l.Contains(": warning CA1812: ", StringComparison.Ordinal)) || upToDate < 0 || upToDate > header)
        Fail($"compilation-end analyzers, next command: expected the build, then the background analysis line for {string.Join(", ", lib)}, then CA1812 once per "
            + $"target framework as dotnet build reports it:\n{output}\ndotnet build reported:\n{string.Join("\n", theirs)}");
    output = Run(blaze, "build");
    if (Warnings(output, "CA1812").Count != 0 || output.Contains("background analysis"))
        Fail($"compilation-end analyzers, the command after: expected the background analysis reported once only:\n{output}");

    output = Run(blaze, "build --all");
    var all = Warnings(output, "CA1812");
    if (!Regex.IsMatch(output, @"^blaze-cs: full analysis \(every analyzer, compilation-end rules included\) of \d+ projects in \d+ ms: \d+ warning\(s\)\r?$", RegexOptions.Multiline)
        || all.Count != lib.Length || !all.Distinct().SequenceEqual(theirs))
        Fail($"compilation-end analyzers, build --all: expected the full analysis to report CA1812 once per target framework as dotnet build reports it:\n{output}");

    // A finished result for a file the next command deletes: dropped, and Lib analyzed again.
    File.WriteAllText(unused, "// Moved down a line.\n" + unusedClass);
    Run(blaze, "build");
    WaitForBackgroundAnalysis("compilation-end analyzers, stale result", DateTimeOffset.Now);
    File.Delete(unused);
    built = DateTimeOffset.Now;
    output = Run(blaze, "build");
    if (!Rebuilt(output).SetEquals(lib) || Warnings(output, "CA1812").Count != 0 || output.Contains("background analysis"))
        Fail($"compilation-end analyzers, stale result: expected {string.Join(", ", lib)} rebuilt and the result for the deleted file dropped:\n{output}");
    WaitForBackgroundAnalysis("compilation-end analyzers, stale result", built);
    output = Run(blaze, "build");
    if (!Regex.IsMatch(output, @"^blaze-cs: background analysis \(compilation-end rules\) of .*Lib \(net10\.0\).*: no warnings\r?$", RegexOptions.Multiline))
        Fail($"compilation-end analyzers, stale result: expected Lib analyzed again, with no warnings:\n{output}");

    // The class and a project file edit in one step: the fallback compiles Lib, and prints errors only.
    File.WriteAllText(unused, unusedClass);
    Replace("Lib/Lib.csproj", "</Project>", "  <!-- edited with Unused.cs -->\n</Project>");
    built = DateTimeOffset.Now;
    output = Run(blaze, "build");
    if (!output.Contains("blaze-cs: Lib/Lib.csproj changed, running dotnet build") || Warnings(output, "CA1812").Count != 0)
        Fail($"compilation-end analyzers, fallback: expected a dotnet build that shows no CA1812:\n{output}");
    WaitForBackgroundAnalysis("compilation-end analyzers, fallback", built);
    output = Run(blaze, "build");
    if (!Warnings(output, "CA1812").Distinct().SequenceEqual(theirs))
        Fail($"compilation-end analyzers, fallback: expected the background analysis to report CA1812 after the dotnet build:\n{output}");

    // A build, then at once a fallback for another project's file: what was pending still reports.
    File.Delete(unused);
    Run(blaze, "build");
    WaitForBackgroundAnalysis("compilation-end analyzers, pending across a fallback", DateTimeOffset.Now);
    Run(blaze, "build");
    File.WriteAllText(unused, unusedClass);
    Run(blaze, "build");
    Replace("App/App.csproj", "</Project>", "  <!-- edited -->\n</Project>");
    built = DateTimeOffset.Now;
    output = Run(blaze, "build");
    if (!output.Contains("blaze-cs: App/App.csproj changed, running dotnet build"))
        Fail($"compilation-end analyzers, pending across a fallback: expected a dotnet build:\n{output}");
    // The run may have finished before the fallback, which then reports it.
    if (Warnings(output, "CA1812").Count == 0)
    {
        WaitForBackgroundAnalysis("compilation-end analyzers, pending across a fallback", built);
        output = Run(blaze, "build");
    }
    if (!Warnings(output, "CA1812").Distinct().SequenceEqual(theirs))
        Fail($"compilation-end analyzers, pending across a fallback: expected CA1812 from the background analysis:\n{output}");

    // Undone through dotnet build, so the cached compiler arguments no longer name Unused.cs.
    File.Delete(unused);
    Replace("Lib/Lib.csproj", "  <!-- edited with Unused.cs -->\n", "");
    output = Run(blaze, "build");
    if (!output.Contains("blaze-cs: Lib/Lib.csproj changed, running dotnet build") || Warnings(output, "CA1812").Count != 0)
        Fail($"compilation-end analyzers, fixed: expected a dotnet build and no CA1812:\n{output}");
    Console.WriteLine("ok  compilation-end analyzers: CA1812 not from the build, then from the background analysis after the next command's build and from build --all, "
        + "as dotnet build reports it; a result for a deleted file dropped; reported after a fallback dotnet build");
}

// A background analysis of both of Lib's units that finished after this moment, within 60 s. The run a build
// leaves starts a second after it returns, and the build cancelled any earlier one.
// The idle collection waits for the background analysis, which allocates heavily, so that it gives back what
// the analysis grew the heap to: the first one after the command is logged after the analysis finished.
void CollectedAfterBackgroundAnalysis(string name, DateTimeOffset after)
{
    DateTimeOffset[] Logged(string line) => [.. Regex.Matches(ReadLog("daemon.log"), $@"^(\S+) blaze-cs: {line}", RegexOptions.Multiline)
        .Select(m => DateTimeOffset.Parse(m.Groups[1].Value)).Where(t => t > after)];
    var waited = Stopwatch.StartNew();
    while (Logged("idle collection took ").Length == 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
        Thread.Sleep(100);
    var collected = Logged("idle collection took ");
    var analyzed = Logged("background analysis finished in ");
    if (collected.Length == 0 || analyzed.Length == 0 || collected[0] < analyzed[0])
        Fail($"{name}: expected the first idle collection after {after:O} to follow the background analysis' end:\n{ReadLog("daemon.log")}");
    Console.WriteLine($"ok  {name}: ran {(collected[0] - analyzed[0]).TotalMilliseconds:F0} ms after the background analysis finished");
}

void WaitForBackgroundAnalysis(string name, DateTimeOffset after)
{
    var waited = Stopwatch.StartNew();
    bool Finished() => Regex.Matches(ReadLog("daemon.log"), @"^(\S+) blaze-cs: background analysis finished in \d+ ms: (.*)$", RegexOptions.Multiline)
        .Any(m => DateTimeOffset.Parse(m.Groups[1].Value) > after && lib.All(m.Groups[2].Value.Contains));
    while (!Finished() && waited.Elapsed < TimeSpan.FromSeconds(60))
        Thread.Sleep(100);
    if (!Finished())
        Fail($"{name}: expected a background analysis of {string.Join(", ", lib)} to finish within 60 s after {after:O}:\n{ReadLog("daemon.log")}");
}

// A build's warnings with this id, without the project MSBuild appends ("[path/Lib.csproj::TargetFramework=net8.0]"), sorted.
static List<string> Warnings(string output, string id) =>
    [.. Regex.Matches(output, $@"^(.+: warning {id}: .*?)(?: \[[^\]]*\])?\r?$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Order(StringComparer.Ordinal)];

// Hosts wait idle for the test projects run most recently (before any run, the solution's first ones), one
// each and HostCap at most: after a run of HostCap projects or fewer, each of them has one. Each host is
// started for its project, whose framework and assembly it names on its command line. One spare, started
// for no project, waits on each runtime the test projects target (one runtimeconfig.json each). Returns
// the projects' hosts.
List<(int Pid, string Project)> IdleHosts(string when, string[]? ran = null)
{
    var expected = Math.Min(HostCap, allTests.Length);
    var runtimes = hasNet11 ? 2 : 1;
    var mustHave = ran is { Length: <= HostCap } ? ran : [];
    var deadline = Stopwatch.StartNew();
    var hosts = Hosts();
    bool Settled()
    {
        var prepared = hosts.Where(h => h.Project != "").ToList();
        var spares = hosts.Where(h => h.Project == "").ToList();
        return prepared.Count == expected && prepared.Select(h => h.Project).Distinct().Count() == expected
            && prepared.All(h => allTests.Contains(h.Project)) && mustHave.All(project => prepared.Any(h => h.Project == project))
            && spares.Count == runtimes && spares.Select(h => h.Runtime).Distinct().Count() == runtimes;
    }
    while (!Settled() && deadline.Elapsed < TimeSpan.FromSeconds(30))
    {
        Thread.Sleep(100);
        hosts = Hosts();
    }
    if (!Settled())
        Fail($"{when}: expected {expected} idle test hosts, one per test project{(mustHave.Length > 0 ? ", " + string.Join(", ", mustHave) + " among them" : "")}, "
            + $"and {runtimes} spare(s), one per runtime, found:\n" + string.Join("\n", TestHostProcesses().Select(h => h.Command)));
    return [.. hosts.Where(h => h.Project != "").Select(h => (h.Pid, h.Project))];

    List<(int Pid, string Project, string Runtime)> Hosts() =>
        [.. TestHostProcesses().Select(h => (h.Pid,
            Path.GetFileNameWithoutExtension(Regex.Match(h.Command, @"blaze-cs-testhost\.dll""? \w+ ""?(.+?\.dll)""?(?: |$)").Groups[1].Value),
            Regex.Match(h.Command, @"blaze-cs-testhost-\d+-\d+\.runtimeconfig\.json").Value))];
}

// A run of every test project takes the waiting host of each project that has one, and the hosts are back after.
void TookEveryHost(List<(int Pid, string Project)> prestarted)
{
    var taken = prestarted.Where(host => !Running(host.Pid)).Count();
    if (taken != prestarted.Count)
        Fail($"expected the first test run to take all {prestarted.Count} pre-started hosts, {taken} are gone");
    IdleHosts("after the first test run");
    Console.WriteLine($"ok  the first test run took the {taken} hosts started for {string.Join(", ", prestarted.Select(h => h.Project).Order())}; "
        + $"each test project's host is one of at most {HostCap}, with a spare per runtime, and they are back after each run");
}

// A host waiting for its project has loaded the project's packages and run its warm-up assembly, and loaded
// none of the solution's outputs (on Linux, where its mapped files show it), so a project reference rebuilt
// after the host started is the new bytes when the run loads it: an edit to Consumer fails each test
// project's greeting test with the new text, whether its host was waiting or started for the run; undone,
// the tests pass again.
void PreparedHostsSeeNewBytes()
{
    var hosts = IdleHosts("before the Consumer edit");
    if (OperatingSystem.IsLinux())
        hosts.ForEach(NoProjectOutputLoaded);
    Replace("Consumer/Welcome.cs", "=> Lib.Greeter.Greet(who)", "=> Lib.Greeter.Greet(who).Replace(\"Hello\", \"Howdy\")");
    var (output, exitCode) = RunWithExit(blaze, "test", check: false);
    var summaries = Summaries(output);
    if (exitCode == 0 || !Rebuilt(output).SetEquals(consumer) || !output.Contains("Howdy") || !summaries.Keys.Order().SequenceEqual(libTests.Order())
        || libTests.Any(project => summaries[project] != new Summary("Failed!", 1, TestCount(project) - 1, 0, TestCount(project))))
        Fail($"Consumer edit: expected {string.Join(", ", libTests)} to run with Consumer's new bytes, each failing its greeting test on \"Howdy\", got exit {exitCode}:\n{output}");
    Replace("Consumer/Welcome.cs", ".Replace(\"Hello\", \"Howdy\")", "");
    output = Run(blaze, "test");
    if (!Rebuilt(output).SetEquals(consumer))
        Fail($"Consumer edit undone: expected blaze-cs to rebuild {string.Join(", ", consumer)}:\n{output}");
    RanTests("Consumer edit undone", output, libTests);
    Verify("Consumer edit undone", [.. consumer], null);
    Console.WriteLine($"ok  Consumer edit: every test project saw its new bytes, among them in the hosts waiting for {string.Join(", ", hosts.Select(h => h.Project).Order())}"
        + (OperatingSystem.IsLinux() ? ", which had loaded no project output" : ""));
}

// Linux: a host's mapped assemblies, once it has its warm-up assembly, include none of the solution's projects.
void NoProjectOutputLoaded((int Pid, string Project) host)
{
    string[] projects = ["Lib", "Consumer", "App", "Generator", "Components", .. allTests];
    var deadline = Stopwatch.StartNew();
    List<string> mapped;
    while (!(mapped = Mapped(host.Pid)).Any(file => file.StartsWith("blaze-cs-warmup-", StringComparison.Ordinal)) && deadline.Elapsed < TimeSpan.FromSeconds(30))
        Thread.Sleep(100);
    var outputs = mapped.Where(file => projects.Contains(Path.GetFileNameWithoutExtension(file))).ToList();
    if (!mapped.Any(file => file.StartsWith("blaze-cs-warmup-", StringComparison.Ordinal)) || outputs.Count > 0)
        Fail($"host {host.Pid} for {host.Project}: expected its warm-up assembly loaded and no project output, it has mapped {string.Join(", ", mapped)}");
}

static List<string> Mapped(int pid)
{
    try
    {
        return [.. File.ReadLines($"/proc/{pid}/maps").Select(line => line.IndexOf('/') is var start and >= 0 ? line[start..] : "")
            .Where(path => path.EndsWith(".dll", StringComparison.Ordinal)).Select(Path.GetFileName).OfType<string>().Distinct()];
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        return [];
    }
}

// Stopping the daemon takes its test hosts with it, and so does killing it: a host exits when the daemon's
// end of its stdin closes.
// A command right after `stop` starts a new daemon while the stopped one may still be exiting (stopping its
// test hosts); once it is gone, the next command reaches the new daemon: the stopped one must not take the
// new one's socket file with it, which left that daemon running unreachable, with its hosts, beside a third.
void StopThenCommandAtOnce()
{
    var stopped = Daemons().Last().Pid;
    Run(blaze, "stop");
    Run(blaze, "build");
    var started = Daemons().Last().Pid;
    var deadline = Stopwatch.StartNew();
    while (Running(stopped) && deadline.Elapsed < TimeSpan.FromSeconds(30))
        Thread.Sleep(100);
    var output = Run(blaze, "build");
    if (Running(stopped) || started == stopped || Daemons().Last().Pid != started || !Running(started))
        Fail($"stop, then build at once: expected daemon {stopped} gone and the next build served by daemon {started}, the one the first build started; "
            + $"daemons: {string.Join(", ", Daemons().Select(d => $"{d.Pid}{(Running(d.Pid) ? " (running)" : "")}"))}:\n{output}");
    Console.WriteLine("ok  stop, then a command at once: the new daemon kept its socket once the stopped one had exited");
}

void HostsStopWithTheDaemon()
{
    IdleHosts("before stop");
    Run(blaze, "stop");
    NoHostsLeft("blaze-cs stop");
    Run(blaze, "build"); // a new daemon, with new hosts
    IdleHosts("after a new daemon started");
    var daemon = Process.GetProcessById(Daemons().Last().Pid);
    daemon.Kill();
    daemon.WaitForExit();
    NoHostsLeft("the daemon being killed");
}

void NoHostsLeft(string cause)
{
    var deadline = Stopwatch.StartNew();
    List<(int Pid, string Command)> hosts;
    while ((hosts = TestHostProcesses()).Count > 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        Thread.Sleep(100);
    if (hosts.Count > 0)
        Fail($"expected no test host processes after {cause}, found:\n{string.Join("\n", hosts.Select(h => h.Command))}");
    Console.WriteLine($"ok  no test hosts left after {cause}");
}

// This fixture's hosts, wherever blaze-cs runs from (the clone or an installed tool): each runs on a
// runtimeconfig.json named after the daemon that started it.
List<(int Pid, string Command)> TestHostProcesses()
{
    var daemons = Daemons().Select(d => $"blaze-cs-testhost-{d.Pid}-").ToList();
    return [.. Processes().Where(p => p.Command.Contains("blaze-cs-testhost") && daemons.Any(p.Command.Contains))];
}

IEnumerable<(int Pid, string Command)> Processes()
{
    var (output, _) = OperatingSystem.IsWindows()
        ? RunWithExit("powershell", "-NoProfile -Command \"Get-CimInstance Win32_Process | ForEach-Object { $_.ProcessId.ToString() + ' ' + $_.CommandLine }\"", check: false)
        : RunWithExit("ps", "-eo pid=,args=", check: false);
    return output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split(' ', 2))
        .Where(parts => parts.Length == 2 && int.TryParse(parts[0], out _))
        .Select(parts => (int.Parse(parts[0]), parts[1]));
}

// A project removed from the solution is no unit any more, though an incremental build logs nothing about
// it: the reload drops it (here with its obj/ deleted too, which must not break the load), later builds
// don't compile or write it, and `test` doesn't run it. Put back, it is a unit again and builds as usual.
void ProjectRemovedAndRestored()
{
    const string removed = "XunitV2Tests";
    var solution = Path.Combine(work, "Fixture.slnx");
    var original = File.ReadAllText(solution);
    var before = loadedProjects;
    string[] tests = ["Tests", "NUnitTests", "MSTestTests", "ComponentTests", .. (hasNet11 ? new[] { "Tests11" } : [])];

    Replace("Fixture.slnx", $"  <Project Path=\"{removed}/{removed}.csproj\" />\n", "");
    Directory.Delete(Path.Combine(work, removed, "obj"), recursive: true);
    var output = Run(blaze, "build");
    var none = FellBack("project removed", output, "Fixture.slnx changed, running dotnet build", allKept: false);
    var loaded = Regex.Matches(output, @"^blaze-cs: loaded (\d+) projects.*", RegexOptions.Multiline).Last();
    if (int.Parse(loaded.Groups[1].Value) != before - 1 || Regex.Match(loaded.Value, @"(\d+) kept warm").Groups[1].Value != (before - 1).ToString())
        Fail($"project removed: expected {before - 1} projects, all kept warm (one fewer than the {before} before):\n{output}");
    Verify("project removed", none, "fallback");

    // The removed project's sources are no input: nothing compiles, and nothing is written for it.
    Replace($"{removed}/WelcomeTests.cs", "\"tester\"", "\"removed\"");
    output = Run(blaze, "build");
    if (!Rebuilt(output).SetEquals([]) || Directory.Exists(Path.Combine(work, removed, "obj")))
        Fail($"edit in the removed project: expected blaze-cs to ignore it and write no {removed}/obj:\n{output}");
    // The others' inputs didn't change, so none runs again (#35), and the removed one isn't listed at all.
    RanTests("test after a project was removed", Run(blaze, "test"), [], among: tests);

    File.WriteAllText(solution, original);
    output = Run(blaze, "build");
    FellBack("project restored", output, "Fixture.slnx changed", allKept: false);
    Verify("project restored", [], "fallback");
    Replace($"{removed}/WelcomeTests.cs", "\"removed\"", "\"tester\"");
    output = Run(blaze, "test");
    if (!Rebuilt(output).SetEquals([removed]))
        Fail($"project restored: expected blaze-cs to compile {removed} again:\n{output}");
    // Back as a test project: its bytes are what it last ran with (the edit is undone), so it is listed
    // as unchanged since that run (#35) rather than run again.
    RanTests("test after the project was restored", output, []);
    Console.WriteLine($"ok  project removed and restored: {removed} was dropped with the rest kept warm, never compiled or tested, and came back as a unit");
}

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

// A test project whose tests failed runs again on the next `test`, though nothing changed since.
void FailedRunsAgain()
{
    Replace("Tests/WelcomeTests.cs", "\"Hello\"", "\"Goodbye\"");
    try
    {
        for (var run = 1; run <= 2; run++)
        {
            var (output, exitCode) = RunWithExit(blaze, "test", check: false);
            if (exitCode == 0 || !Regex.IsMatch(output, @"^\s*Failed!\s+- Failed:\s+1, .* - Tests\.dll\b", RegexOptions.Multiline)
                || run == 2 && Rebuilt(output).Count != 0)
                Fail($"failing test, run {run}: expected Tests to run again with one test failed{(run == 2 ? " and nothing rebuilt" : "")}, got exit {exitCode}:\n{output}");
        }
    }
    finally
    {
        Replace("Tests/WelcomeTests.cs", "\"Goodbye\"", "\"Hello\"");
    }
    RanTests("test after a failed run", Run(blaze, "test"), ["Tests"]);
    Console.WriteLine("ok  failed test project: ran again with unchanged inputs, and passed once fixed");
}

// A .runsettings with anything beyond environment variables and test parameters sends its project to dotnet
// test, which applies it; the tests still see the variable and parameter there. That holds for the one a
// project file names (NUnitTests) and for the one a Directory.Build.props sets (MSTestTests). A stray
// .runsettings in the project directory that nothing names, which dotnet test ignores, changes nothing:
// XunitV2Tests keeps running in the host, and an edit to it doesn't run the project again.
void RunSettingsTheHostDoesNotApply()
{
    Replace("NUnitTests/test.runsettings", "  <RunConfiguration>\n", "  <RunConfiguration>\n    <TreatNoTestsAsError>true</TreatNoTestsAsError>\n");
    Replace("NUnitTests/WelcomeTests.cs", "\"tester\"", "\"settings\"");
    Replace("settings/shared.runsettings", "  <RunConfiguration>\n", "  <RunConfiguration>\n    <TreatNoTestsAsError>true</TreatNoTestsAsError>\n");
    Replace("MSTestTests/WelcomeTests.cs", "\"tester\"", "\"settings\"");
    var output = Run(blaze, "test");
    foreach (var project in new[] { "NUnitTests", "MSTestTests" })
    {
        if (!output.Contains($"blaze-cs: {project} has a .runsettings, which the test host doesn't apply; running dotnet test")
            || !Regex.IsMatch(output, $@"^\s*Passed!\s+- Failed:\s+0, Passed:\s+{TestCount(project)}, .* - {project}\.dll", RegexOptions.Multiline))
            Fail($"runsettings the host doesn't apply: expected {project} to pass under dotnet test:\n{output}");
    }
    Console.WriteLine("ok  runsettings the host doesn't apply: dotnet test ran the projects");

    Replace("XunitV2Tests/stray.runsettings", "<RunConfiguration>\n", "<RunConfiguration>\n    <!-- edited -->\n");
    output = Run(blaze, "test");
    if (output.Contains("running dotnet test") || Summaries(output).Count != 0)
        Fail($"stray runsettings: expected an edit to a .runsettings nothing names to run no tests:\n{output}");
    Console.WriteLine("ok  stray runsettings: not applied, not a reason to leave the host or run the project again");

    // Back to what the host applies: MSTestTests returns to the host (later steps count the hosts that start).
    Replace("settings/shared.runsettings", "    <TreatNoTestsAsError>true</TreatNoTestsAsError>\n", "");
    RanTests("runsettings the host applies again", Run(blaze, "test"), ["MSTestTests"]);
    Console.WriteLine("ok  runsettings from a Directory.Build.props: MSTestTests is back in the host once its .runsettings is host-applicable");
}

// An API change in Lib whose compile, or whose writes, fail: the build fails naming the cause, and Consumer,
// though it may compile against Lib's new image while Lib's files are written, writes nothing. Until the
// cause is gone, every build fails again; once it is, the next build, edit or not, brings every output back
// to what dotnet build makes.
void ParentFails()
{
    const string greet = "Greet(string person) => \"Hello there, \" + person";
    Dictionary<string, byte[]> ConsumerOutputs(string framework) =>
        Outputs().Where(o => o.Key.StartsWith($"Consumer/obj/Debug/{framework}/", StringComparison.Ordinal)).ToDictionary();
    void Unchanged(string name, Dictionary<string, byte[]> before, string framework)
    {
        var after = ConsumerOutputs(framework);
        if (before.Count == 0 || before.Any(o => !after.TryGetValue(o.Key, out var bytes) || !bytes.AsSpan().SequenceEqual(o.Value)))
            Fail($"{name}: expected Consumer ({framework}) to write nothing");
    }

    var consumer10 = ConsumerOutputs("net10.0");
    var consumer8 = ConsumerOutputs("net8.0");
    Replace("Lib/Greeter.cs", greet, "Greet(string name) => \"Hello there, \" + person");
    var (output, exitCode) = RunWithExit(blaze, "build", check: false);
    if (exitCode == 0 || !output.Contains("error CS0103") || !output.Contains("blaze-cs: build failed"))
        Fail($"failing compile in Lib: expected the build to fail with CS0103, got exit {exitCode}:\n{output}");
    // Like dotnet build, the next build fails again until the error is fixed.
    (output, exitCode) = RunWithExit(blaze, "build", check: false);
    if (exitCode == 0 || !output.Contains("error CS0103") || !output.Contains("blaze-cs: build failed"))
        Fail($"failing compile in Lib, built again: expected the build to fail with CS0103 again, got exit {exitCode}:\n{output}");
    Unchanged("failing compile in Lib", consumer10, "net10.0");
    Unchanged("failing compile in Lib", consumer8, "net8.0");
    Replace("Lib/Greeter.cs", "Greet(string name) => \"Hello there, \" + person", greet);
    var rebuilt = Rebuilt(Run(blaze, "build"));
    if (!rebuilt.SetEquals(lib))
        Fail($"failing compile in Lib, fixed: expected blaze-cs to rebuild {string.Join(", ", lib)}, it rebuilt {string.Join(", ", rebuilt.Order())}");
    Verify("failing compile in Lib, fixed", rebuilt, null);

    // A directory where Lib's net10.0 PDB is first written makes that write fail; its assembly is already written.
    var pdb = Directory.EnumerateFiles(Path.Combine(work, "Lib", "obj"), "Lib.pdb", SearchOption.AllDirectories)
        .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "net10.0");
    var obstacle = Directory.CreateDirectory(pdb + ".blaze-cs.tmp");
    try
    {
        Replace("Lib/Greeter.cs", greet, "Greet(string name) => \"Hello there, \" + name");
        (output, exitCode) = RunWithExit(blaze, "build", check: false);
    }
    finally
    {
        obstacle.Delete();
    }
    if (exitCode == 0 || !output.Contains("Lib.pdb.blaze-cs.tmp") || !output.Contains("blaze-cs: build failed")
        || !Regex.IsMatch(output, @"^blaze-cs: blocked by a failed reference: .*Consumer \(net10\.0\)", RegexOptions.Multiline))
        Fail($"failing write in Lib: expected the build to fail naming Lib.pdb.blaze-cs.tmp and blocked Consumer (net10.0), got exit {exitCode}:\n{output}");
    Unchanged("failing write in Lib", consumer10, "net10.0");
    // With the obstacle gone, the next build writes Lib again and compiles the dependent it blocked.
    string[] retried = ["Consumer (net10.0)", "Lib (net10.0)"];
    rebuilt = Rebuilt(Run(blaze, "build"));
    if (!rebuilt.SetEquals(retried))
        Fail($"failing write in Lib, built again: expected blaze-cs to rebuild {string.Join(", ", retried)}, it rebuilt {string.Join(", ", rebuilt.Order())}");
    Verify("failing write in Lib, built again", rebuilt, null);
    Replace("Lib/Greeter.cs", "Greet(string name) => \"Hello there, \" + name", greet);
    string[] both = [.. consumer, .. lib];
    rebuilt = Rebuilt(Run(blaze, "build"));
    if (!rebuilt.SetEquals(both))
        Fail($"failing write in Lib, fixed: expected blaze-cs to rebuild {string.Join(", ", both)}, it rebuilt {string.Join(", ", rebuilt.Order())}");
    Verify("failing write in Lib, fixed", rebuilt, null);
}

// An output something else touched without changing its bytes, here the reference assembly csc writes for Lib
// (/refout:, obj/.../refint/), is compared on disk with what the next build emits for it and, still holding
// those bytes, is not written again: its stamp stays the one it was touched to.
void TouchedOutputLeftAlone()
{
    var reference = Directory.EnumerateFiles(Path.Combine(work, "Lib", "obj"), "Lib.dll", SearchOption.AllDirectories)
        .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "refint" && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p))) == "net10.0");
    var bytes = File.ReadAllBytes(reference);
    var touched = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(reference, touched);
    Replace("Lib/Greeter.cs", "Twice(int amount) => amount + amount", "Twice(int amount) => amount * 2");
    var rebuilt = Rebuilt(Run(blaze, "build"));
    if (!rebuilt.SetEquals(lib))
        Fail($"touched reference assembly: expected blaze-cs to rebuild {string.Join(", ", lib)}, it rebuilt {string.Join(", ", rebuilt.Order())}");
    if (File.GetLastWriteTimeUtc(reference) != touched || !File.ReadAllBytes(reference).AsSpan().SequenceEqual(bytes))
        Fail($"touched reference assembly: expected blaze-cs to leave {Path.GetRelativePath(work, reference)} as it was, stamp {touched:O}; it is stamped {File.GetLastWriteTimeUtc(reference):O}");
    Verify("touched reference assembly, body in Lib", rebuilt, null);
}

// With every test skipped, the warm host's summary reads like dotnet test's for each framework: "Skipped!"
// and the same counts. The xUnit v3 project keeps its explicit test, which neither counts.
void AllTestsSkipped()
{
    string[] projects = ["Tests", "XunitV2Tests", "NUnitTests", "MSTestTests"];
    SkipTests("Tests/WelcomeTests.cs", "[Fact]", "[Fact(Skip = \"skipped\")]");
    SkipTests("XunitV2Tests/WelcomeTests.cs", "[Fact]", "[Fact(Skip = \"skipped\")]");
    SkipTests("NUnitTests/WelcomeTests.cs", "[Test]", "[Test, Ignore(\"skipped\")]");
    SkipTests("MSTestTests/WelcomeTests.cs", "[TestMethod]", "[TestMethod, Ignore]");
    var ours = Summaries(Run(blaze, "test"));
    foreach (var project in projects)
    {
        var theirs = Summaries(Run("dotnet", $"test --no-build {project} -nologo"));
        var expected = new Summary("Skipped!", 0, 0, TestCount(project), TestCount(project));
        if (ours.GetValueOrDefault(project) != expected || theirs.GetValueOrDefault(project) != expected)
            Fail($"all tests skipped: {project}: expected {expected} from the host and from dotnet test, got {ours.GetValueOrDefault(project)} and {theirs.GetValueOrDefault(project)}");
    }
    Console.WriteLine("ok  all tests skipped: the host's summary lines match dotnet test's");
}

void SkipTests(string path, string attribute, string skipped) => File.WriteAllText(Path.Combine(work, path), File.ReadAllText(Path.Combine(work, path)).Replace(attribute, skipped));

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
// identical to that build's. The projects MSBuild compiled aren't reported, so the set is empty. With
// allKept, the reload must also keep every unit warm and compile none to verify it.
HashSet<string> FellBack(string name, string output, string fallback, bool allKept)
{
    // Its message names paths with this OS's separator.
    if (!output.Contains($"blaze-cs: {fallback.Replace('/', Path.DirectorySeparatorChar)}"))
        Fail($"{name}: expected blaze-cs to log \"{fallback}\":\n{output}");
    var warmUp = WarmedUpIdentical(name, output);
    if (allKept)
    {
        var loaded = Regex.Matches(output, @"^blaze-cs: loaded (\d+) projects .*", RegexOptions.Multiline).LastOrDefault();
        var kept = Regex.Match(loaded?.Value ?? "", @"(\d+) kept warm").Groups[1].Value;
        var compiled = Regex.Match(warmUp.Value, @"\((\d+) compiled\)").Groups[1].Value;
        if (loaded is null || kept != loaded.Groups[1].Value || compiled != "0")
            Fail($"{name}: expected blaze-cs to keep every project warm and compile none of them again:\n{output}");
    }
    return [];
}

// The last warm-up must find every assembly identical to dotnet build's: one that finds stale cached
// arguments reloads with fresh ones.
Match WarmedUpIdentical(string name, string output)
{
    var warmUp = Regex.Matches(output, @"^blaze-cs: warmed up in \d+ ms; (\d+)/(\d+) assemblies identical.*", RegexOptions.Multiline).LastOrDefault();
    if (warmUp is null || warmUp.Groups[1].Value != warmUp.Groups[2].Value)
        Fail($"{name}: expected blaze-cs to reload with every assembly identical to dotnet build's:\n{output}");
    return warmUp;
}

// The test projects' summary lines ("Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, ... - Tests.dll",
// printed alike by the warm host and dotnet test), by project.
static Dictionary<string, Summary> Summaries(string output) =>
    Regex.Matches(output, @"^\s*(\w+!)\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+), .* - (\S+)\.dll\b", RegexOptions.Multiline)
        .ToDictionary(m => m.Groups[6].Value, m => new Summary(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value)));

// The test projects a run reports: exactly the expected ones, once each, with all their tests passed and
// counted as dotnet test counts them (explicit tests that weren't asked for aren't), and none through
// dotnet test, since the warm host must run them on the runtime each one targets. Every other test
// project in the solution (`among`, by default all) is listed as unchanged since its last run.
void RanTests(string name, string output, string[] expected, string[]? among = null)
{
    among ??= allTests;
    var unchanged = Regex.Match(output, @"^blaze-cs: unchanged since last run: (.+)$", RegexOptions.Multiline) is { Success: true } line
        ? line.Groups[1].Value.TrimEnd('\r').Split(", ").Select(project => project.Split(" (")[0]).ToList()
        : [];
    if (!unchanged.Order().SequenceEqual(among.Except(expected).Order()))
        Fail($"{name}: expected {string.Join(", ", among.Except(expected).Order())} listed as unchanged since last run, got {string.Join(", ", unchanged.Order())}:\n{output}");
    var summaries = Summaries(output);
    if (!summaries.Keys.Order().SequenceEqual(expected.Order()))
        Fail($"{name}: expected tests of {string.Join(", ", expected)} to run, ran {string.Join(", ", summaries.Keys.Order())}:\n{output}");
    if (output.Contains("running dotnet test"))
        Fail($"{name}: expected the test host to run these, not dotnet test:\n{output}");
    foreach (var (project, summary) in summaries)
        if (summary != new Summary("Passed!", 0, TestCount(project), 0, TestCount(project)))
            Fail($"{name}: {project}: expected {TestCount(project)} tests passed and no others, got {summary}:\n{output}");
}

// Each test project has its two tests and RunsOnTargetRuntime, plus AppliesRunSettings where it has a .runsettings.
static int TestCount(string project) => project switch { "ComponentTests" => 2, "Tests11" => 1, "NUnitTests" or "MSTestTests" => 4, _ => 3 };

// Compares what blaze-cs wrote with a full MSBuild rebuild of the same sources. After a fallback, MSBuild
// built everything, so every output must be identical. Returns that build's output, with its warnings.
string Verify(string name, HashSet<string> rebuilt, string? fallback)
{
    var ours = Outputs();
    var build = Run("dotnet", "build Fixture.slnx -nologo -v:q -clp:NoSummary --no-incremental");
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
    return build;
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

string Run(string file, string arguments, bool check = true, Dictionary<string, string>? environment = null) =>
    RunWithExit(file, arguments, check, environment).Output;

(string Output, int ExitCode) RunWithExit(string file, string arguments, bool check = true, Dictionary<string, string>? environment = null)
{
    var start = new ProcessStartInfo(file, arguments) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var (name, value) in environment ?? [])
        start.Environment[name] = value;
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

sealed record Summary(string Label, int Failed, int Passed, int Skipped, int Total);

sealed record Case(string Name, Action Edit, string[] Expected, string Command, string? Fallback = null, bool AllKept = false, string[]? Ran = null);
