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
try
{
    Run(blaze, "build"); // starts the daemon: a real dotnet build, then a warm-up compile
    foreach (var (name, edit, expected, command, fallback, allKept, ran) in cases)
    {
        edit();
        var output = Run(blaze, command);
        var rebuilt = fallback is null ? Rebuilt(output) : FellBack(name, output, fallback, allKept);
        if (!rebuilt.SetEquals(expected))
            Fail($"{name}: expected blaze-cs to rebuild {string.Join(", ", expected)}, it rebuilt {string.Join(", ", rebuilt.Order())}:\n{output}");
        if (command.StartsWith("test", StringComparison.Ordinal))
            RanTests(name, output, ran ?? expected);
        Verify(name, rebuilt, fallback);
    }
    HostCannotLoad();
    RunSettingsTheHostDoesNotApply();
    FailedRunsAgain();
    AllTestsSkipped();
    DaemonLifecycle();
    ParentFails();
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

// A client from another build of blaze-cs stops the daemon the cases used and starts its own, with a notice;
// that daemon, started with a short idle limit, exits on its own and removes its socket; and the next
// command, from this repo's build again, starts a daemon without a word about it.
void DaemonLifecycle()
{
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
    var daemons = Daemons();
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
    daemons = Daemons();
    if (output.Contains("another build") || daemons.Count != 3 || !Running(daemons[2].Pid) || !Regex.IsMatch(output, "^blaze-cs: up to date", RegexOptions.Multiline))
        Fail($"after an idle exit: expected the next command to start a daemon without a notice and build, got {daemons.Count} daemons:\n{output}");
    Console.WriteLine($"ok  daemon lifecycle: another build restarted daemon {first.Pid} as {idle.Pid}, which exited on its own after 5 s idle; the next command started {daemons[2].Pid}");
}

static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.EnumerateFiles(from))
        File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (var directory in Directory.EnumerateDirectories(from))
        CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
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
// test, which applies it; the tests still see the variable and parameter there.
void RunSettingsTheHostDoesNotApply()
{
    Replace("NUnitTests/test.runsettings", "  <RunConfiguration>\n", "  <RunConfiguration>\n    <TreatNoTestsAsError>true</TreatNoTestsAsError>\n");
    Replace("NUnitTests/WelcomeTests.cs", "\"tester\"", "\"settings\"");
    var output = Run(blaze, "test");
    if (!output.Contains("blaze-cs: NUnitTests has a .runsettings, which the test host doesn't apply; running dotnet test")
        || !Regex.IsMatch(output, $@"^\s*Passed!\s+- Failed:\s+0, Passed:\s+{TestCount("NUnitTests")}, .* - NUnitTests\.dll", RegexOptions.Multiline))
        Fail($"runsettings the host doesn't apply: expected NUnitTests to pass under dotnet test:\n{output}");
    Console.WriteLine("ok  runsettings the host doesn't apply: dotnet test ran the project");
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
    // The last warm-up: one that finds stale cached arguments reloads with fresh ones.
    var warmUp = Regex.Matches(output, @"^blaze-cs: warmed up in \d+ ms; (\d+)/(\d+) assemblies identical.*", RegexOptions.Multiline).LastOrDefault();
    if (warmUp is null || warmUp.Groups[1].Value != warmUp.Groups[2].Value)
        Fail($"{name}: expected blaze-cs to reload with every assembly identical to dotnet build's:\n{output}");
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

// The test projects' summary lines ("Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, ... - Tests.dll",
// printed alike by the warm host and dotnet test), by project.
static Dictionary<string, Summary> Summaries(string output) =>
    Regex.Matches(output, @"^\s*(\w+!)\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+), .* - (\S+)\.dll\b", RegexOptions.Multiline)
        .ToDictionary(m => m.Groups[6].Value, m => new Summary(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value)));

// The test projects a run reports: exactly the expected ones, once each, with all their tests passed and
// counted as dotnet test counts them (explicit tests that weren't asked for aren't), and none through
// dotnet test, since the warm host must run them on the runtime each one targets. Every other test
// project is listed as unchanged since its last run.
void RanTests(string name, string output, string[] expected)
{
    var unchanged = Regex.Match(output, @"^blaze-cs: unchanged since last run: (.+)$", RegexOptions.Multiline) is { Success: true } line
        ? line.Groups[1].Value.TrimEnd('\r').Split(", ").Select(project => project.Split(" (")[0]).ToList()
        : [];
    if (!unchanged.Order().SequenceEqual(allTests.Except(expected).Order()))
        Fail($"{name}: expected {string.Join(", ", allTests.Except(expected).Order())} listed as unchanged since last run, got {string.Join(", ", unchanged.Order())}:\n{output}");
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
