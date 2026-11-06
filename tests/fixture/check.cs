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
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

var fixture = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "tests/fixture/check.cs"))!;
var root = Path.GetFullPath(Path.Combine(fixture, "../.."));
var work = Path.Combine(Path.GetTempPath(), "blaze-cs-fixture");
var otherBuild = work + "-other-build";
var otherSolution = work + "-other-solution";
var roslynCopy = work + "-roslyn";
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
// A case with a fallback must instead log it ("blaze-cs: <fallback>") and run dotnet build. A case with Logged must
// log that line ("blaze-cs: <logged>").
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
    // A project file edit is evaluated in the daemon, with MSBuild hosted in its process, and runs no dotnet
    // build. A comment changes no compiler argument: every unit stays as it was, and nothing compiles.
    new("project file edit", () => Replace("Lib/Lib.csproj", "</Project>", "  <!-- edited -->\n</Project>"),
        [], "build", Logged: "evaluated Lib.csproj in process in"),
    // A define changes both of Lib's units' arguments and, through an #if, a public constant only App reads.
    new("project file define",
        () => Replace("Lib/Lib.csproj", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>",
            "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>\n    <DefineConstants>$(DefineConstants);FIXTURE_FLAG</DefineConstants>"),
        ["App", .. lib], "build", Logged: "evaluated Lib.csproj in process in"),
    // A package needs a restore, which only dotnet build runs. (xunit.abstractions: XunitV2Tests restores it already.)
    new("project file package reference",
        () => Replace("Lib/Lib.csproj", "</Project>", "  <ItemGroup>\n    <PackageReference Include=\"xunit.abstractions\" Version=\"2.0.3\" />\n  </ItemGroup>\n</Project>"),
        [], "build", "Lib: the edit to Lib.csproj changes more than csc's input (<PackageReference Include=\"xunit.abstractions\">); running dotnet build"),
    // An import goes through dotnet build. The in-process MSBuild has parsed Directory.Build.props before, and
    // must not evaluate the next project file edit with the copy it cached: Lib's arguments would lose the define.
    new("Directory.Build.props define",
        () => Replace("Directory.Build.props", "<ImplicitUsings>enable</ImplicitUsings>",
            "<ImplicitUsings>enable</ImplicitUsings>\n    <DefineConstants>$(DefineConstants);FIXTURE_DBP</DefineConstants>"),
        [], "build", "Directory.Build.props changed, running dotnet build"),
    new("project file edit after a fallback", () => Replace("Lib/Lib.csproj", "<!-- edited -->", "<!-- edited again -->"),
        [], "build", Logged: "evaluated Lib.csproj in process in"),
    // Consumer has a ReferenceOutputAssembly="false" reference (Generator), which restore's spec leaves out.
    new("Consumer project file edit", () => Replace("Consumer/Consumer.csproj", "</Project>", "  <!-- edited -->\n</Project>"),
        [], "build", Logged: "evaluated Consumer.csproj in process in"),
    // The pass gives csc's arguments, not the files the build copies.
    new("project file copy item",
        () =>
        {
            File.WriteAllText(Path.Combine(work, "Lib/data.txt"), "copied next to Lib and its dependents\n");
            Replace("Lib/Lib.csproj", "</Project>", "  <ItemGroup>\n    <None Update=\"data.txt\" CopyToOutputDirectory=\"PreserveNewest\" />\n  </ItemGroup>\n</Project>");
        },
        [], "build", "Lib: the edit to Lib.csproj changes more than csc's input (<None Update=\"data.txt\">); running dotnet build"),
    new("pre-build step added",
        () => Replace("Consumer/Consumer.csproj", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>",
            "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>\n    <PreBuildEvent>echo pre-build</PreBuildEvent>"),
        [], "build", "Consumer: the edit to Consumer.csproj changes more than csc's input (<PreBuildEvent>); running dotnet build"),
    // The pass doesn't run a pre-build step, which a real build runs even when nothing else changed.
    new("project file edit with a pre-build step", () => Replace("Consumer/Consumer.csproj", "<!-- edited -->", "<!-- edited again -->"),
        [], "build", "Consumer: it has a pre-build step; running dotnet build"),
    // A compiler setting, so the pass takes it; the pass must also run AddPrependedSource, which
    // Directory.Build.targets puts in front of BuildDependsOn, or Lib loses Prepended.cs.
    new("project file LangVersion",
        () => Replace("Lib/Lib.csproj", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>",
            "<TargetFrameworks>net8.0;net10.0</TargetFrameworks>\n    <LangVersion>latest</LangVersion>"),
        lib, "build", Logged: "evaluated Lib.csproj in process in"),
    // An import that reads a compiler setting may do more with it than csc: here, copy a file.
    new("Directory.Build.targets reads a compiler setting",
        () =>
        {
            File.WriteAllText(Path.Combine(work, "Lib/copied.txt"), "copied when overflow checks are on\n");
            Replace("Directory.Build.targets", "</Project>",
                "  <ItemGroup Condition=\"'$(CheckForOverflowUnderflow)' == 'true'\">\n"
                + "    <None Include=\"$(MSBuildProjectDirectory)/copied.txt\" CopyToOutputDirectory=\"PreserveNewest\" Condition=\"Exists('$(MSBuildProjectDirectory)/copied.txt')\" />\n"
                + "  </ItemGroup>\n</Project>");
        },
        [], "build", "Directory.Build.targets changed, running dotnet build"),
    new("project file setting an import reads",
        () => Replace("Lib/Lib.csproj", "<LangVersion>latest</LangVersion>", "<LangVersion>latest</LangVersion>\n    <CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>"),
        [], "build", "Lib: the edit to Lib.csproj changes more than csc's input ($(CheckForOverflowUnderflow), which Directory.Build.targets reads); running dotnet build"),
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
// BLAZE_CS_COMPILER_SERVER=0 turns the daemon's compiler-server pipe off; the checks of builds through it are skipped.
var serverOff = Environment.GetEnvironmentVariable("BLAZE_CS_COMPILER_SERVER") == "0";
try
{
    // Starts the daemon: a real dotnet build; the warm-up compile runs after it returns. It names the pipe that
    // builds compile through it with.
    var pipe = PipeName(Run(blaze, "build"));
    // The next command reports the warm-up, which must reproduce every assembly.
    WarmedUpIdentical("start", Run(blaze, "build"));
    var prestarted = IdleHosts("before any test command");
    Console.WriteLine($"ok  {prestarted.Count} idle test hosts before any test command, one each for {string.Join(", ", prestarted.Select(h => h.Project).Order())}, and a spare per runtime");
    foreach (var (name, edit, expected, command, fallback, ran, logged) in cases)
    {
        edit();
        var output = Run(blaze, command);
        if (logged is not null && !output.Contains($"blaze-cs: {logged}"))
            Fail($"{name}: expected blaze-cs to log \"{logged}\":\n{output}");
        if (Regex.Matches(output, @"^blaze-cs: loaded (\d+) projects", RegexOptions.Multiline).LastOrDefault() is { } load)
            loadedProjects = int.Parse(load.Groups[1].Value);
        var rebuilt = fallback is null ? Rebuilt(output) : FellBack(name, output, fallback);
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
    OutsideBuildOfOneProject(pipe: null);
    if (!serverOff)
        OutsideBuildOfOneProject(pipe);
    AnalyzerWarnings();
    CompilationEndAnalyzers();
    if (!serverOff)
    {
        BuildThroughTheDaemon(pipe!);
        ArgumentsChangedThroughTheDaemon(pipe!);
        RejectedRequests(pipe!);
        AnalyzersThroughTheDaemon(pipe!);
        CompilationEndThroughTheDaemon(pipe!);
    }
    PreparedHostsSeeNewBytes();
    HotReloadBodyEdit();
    HotReloadFallsBack();
    ProjectRemovedAndRestored();
    HostCannotLoad();
    RunSettingsTheHostDoesNotApply();
    FailedRunsAgain();
    AllTestsSkipped();
    HotReloadUnavailable();
    WarmUpAfterFirstCommand();
    if (!serverOff)
    {
        // Both restart the daemon, so they come after the checks of a daemon's test hosts.
        PipeTakenOver(pipe!);
        ServerTurnedOff(pipe!);
    }
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
    foreach (var scratch in new[] { otherBuild, otherSolution, roslynCopy })
    {
        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a process may still hold a file on Windows
    }
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

// A `dotnet build` of one project writes its own obj/ and bin/ but not the copies in the projects that
// reference it (#133). Without the pipe, the next `test` compiles Lib to the bytes its obj/ already holds;
// through it, the daemon compiled Lib for the build, and the next `test` has nothing to compile. Either way
// every copy must be brought up to date, and the test projects whose copies changed must run rather than be
// reported unchanged. Every copy in a bin/ is then what `dotnet build --no-incremental` makes. The edit is
// undone after.
void OutsideBuildOfOneProject(string? pipe)
{
    var name = pipe is null ? "body in Lib, built by dotnet build Lib/Lib.csproj" : "body in Lib, built through the daemon's pipe";
    const string before = "Twice(int amount) => amount + amount", after = "Twice(int amount) => 2 * amount";
    Run(blaze, "test"); // the inputs each test project last passed with
    Replace("Lib/Greeter.cs", before, after);
    if (pipe is null)
        Run("dotnet", "build Lib/Lib.csproj -nologo -v:q -clp:NoSummary");
    else
        BuildThrough(pipe, "Lib/Lib.csproj");
    var output = Run(blaze, "test");
    var rebuilt = Rebuilt(output);
    string[] expected = pipe is null ? lib : [];
    if (!rebuilt.SetEquals(expected))
        Fail($"{name}: expected blaze-cs to rebuild {(expected.Length == 0 ? "nothing" : string.Join(", ", expected))}, it rebuilt {string.Join(", ", rebuilt.Order())}:\n{output}");
    RanTests(name, output, libTests);
    var ours = Copies();
    Verify(name, rebuilt, pipe is null ? null : "the daemon's pipe");
    var theirs = Copies();
    if (!ours.Keys.ToHashSet().SetEquals(theirs.Keys))
        Fail($"{name}: bin/ copies differ in which exist: {string.Join(", ", ours.Keys.ToHashSet().Except(theirs.Keys).Union(theirs.Keys.Except(ours.Keys)))}");
    var stale = ours.Keys.Where(path => !ours[path].AsSpan().SequenceEqual(theirs[path])).Order().ToList();
    if (stale.Count > 0)
        Fail($"{name}: these copies differ from dotnet build --no-incremental's: {string.Join(", ", stale)}");
    Console.WriteLine($"ok  {name}: all {ours.Count} assemblies, PDBs and documentation files in bin/ identical to dotnet build's");

    Replace("Lib/Greeter.cs", after, before);
    rebuilt = Rebuilt(Run(blaze, "build"));
    if (!rebuilt.SetEquals(lib))
        Fail($"{name}, undone: expected blaze-cs to rebuild {string.Join(", ", lib)}, it rebuilt {string.Join(", ", rebuilt.Order())}");
}

// Every assembly, PDB and documentation file in a project's bin/, by relative path with / separators.
Dictionary<string, byte[]> Copies() =>
    Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(work, path).Replace('\\', '/'))
        .Where(path => path.Split('/') is [_, "bin", ..] && Path.GetExtension(path) is ".dll" or ".pdb" or ".xml")
        .ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(work, path)));

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

// The pipe the daemon's first command names: "dotnet build -p:SharedCompilationId=<pipe> compiles through this daemon".
// None when the pipe is off.
string? PipeName(string output)
{
    var name = Regex.Match(output, @"^blaze-cs: dotnet build -p:SharedCompilationId=(\S+) compiles through this daemon", RegexOptions.Multiline);
    if (serverOff == name.Success)
        Fail($"first command: expected {(serverOff ? "no pipe, which BLAZE_CS_COMPILER_SERVER=0 turns off" : "the pipe to compile through the daemon with")}:\n{output}");
    return name.Success ? name.Groups[1].Value : null;
}

// What the Csc task logged of each compile at -v:d ("CompilerServer: server - server processed compilation - Lib (net10.0)"),
// by request (project and target framework): "server" for a compile the daemon answered; for one it rejected,
// "server failed" and the reason; "tool" for csc by design.
Dictionary<string, string> CompilerServerLog(string output) =>
    Regex.Matches(output, @"CompilerServer: (.+?) - (.+) - (\S+(?: \([^)]+\))?)\r?$", RegexOptions.Multiline)
        .DistinctBy(m => m.Groups[3].Value) // MSBuild may print a message more than once
        .ToDictionary(m => m.Groups[3].Value, m => $"{m.Groups[1].Value}: {m.Groups[2].Value}");

// Builds through the pipe, in this process's own MSBuild nodes: a reused node or MSBuild server could hold a task
// assembly that another build here replaces (RejectedRequests), and keep it loaded past the check.
string BuildThrough(string pipe, string arguments) => BuildThroughWithExit(pipe, arguments, check: true).Output;

(string Output, int ExitCode) BuildThroughWithExit(string pipe, string arguments, bool check) =>
    RunWithExit("dotnet", $"build {arguments} -nologo -v:d -nodeReuse:false -p:SharedCompilationId={pipe}", check,
        new() { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" });

// The distinct analyzer diagnostics of one id a build printed: file, position and message, without MSBuild's
// [project] suffix and indent.
static HashSet<string> Printed(string output, string id) =>
    Regex.Matches(output, $@"(\S+\(\d+,\d+\)): (warning|error|info) ({id}): ([^\[\r\n]*)")
        .Select(m => $"{Path.GetFileName(m.Groups[1].Value)} {m.Groups[2].Value} {m.Groups[3].Value} {m.Groups[4].Value.Trim()}").ToHashSet();

// With the daemon's pipe as SharedCompilationId, dotnet build compiles every project through the daemon: each
// compile is one the daemon answered (none falls back to csc), the outputs are what dotnet build --no-incremental
// without it writes, and the next blaze-cs build has nothing to do.
void BuildThroughTheDaemon(string pipe)
{
    var output = BuildThrough(pipe, "Fixture.slnx --no-incremental");
    var compiles = CompilerServerLog(output);
    if (compiles.Count != loadedProjects || compiles.Values.Any(result => result != "server: server processed compilation"))
        Fail($"dotnet build through the daemon: expected all {loadedProjects} compiles answered by it, got:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
    if (!Regex.IsMatch(ReadLog("daemon.log"), @"compiler server: Lib \(net10\.0\): compiled in \d+ ms, exit code 0"))
        Fail($"dotnet build through the daemon: expected its log to show Lib (net10.0) compiled:\n{ReadLog("daemon.log")}");
    Console.WriteLine($"ok  dotnet build -p:SharedCompilationId={pipe}: the daemon answered all {compiles.Count} compiles");
    Verify("dotnet build through the daemon", [], "the daemon's pipe");
    var rebuilt = Rebuilt(Run(blaze, "build"));
    if (rebuilt.Count > 0)
        Fail($"blaze-cs build after dotnet build through the daemon: expected nothing to rebuild, it rebuilt {string.Join(", ", rebuilt.Order())}");
}

// A project file edit changes Lib's compiler arguments, and dotnet build compiles Lib through the daemon with the
// new ones: the daemon takes them from the request. The next blaze-cs build evaluates the project file in process,
// and the pass must give the arguments the request did: every unit stays, and nothing compiles.
void ArgumentsChangedThroughTheDaemon(string pipe)
{
    const string property = "  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);SERVED</DefineConstants>\n  </PropertyGroup>\n";
    const string member = "#if SERVED\n    public const string Served = \"served\";\n\n#endif\n";
    Replace("Lib/Lib.csproj", "</Project>", property + "</Project>");
    Replace("Lib/Greeter.cs", "    public static int Twice", member + "    public static int Twice");
    var compiles = CompilerServerLog(BuildThrough(pipe, "Fixture.slnx"));
    if (lib.Any(unit => compiles.GetValueOrDefault(unit) != "server: server processed compilation") || compiles.Values.Any(result => !result.StartsWith("server: ", StringComparison.Ordinal)))
        Fail($"changed arguments through the daemon: expected it to answer every compile, Lib's among them, got:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
    var output = Run(blaze, "build");
    if (output.Contains("stale") || output.Contains("--no-incremental") || output.Contains("running dotnet build")
        || !Regex.IsMatch(output, @"^blaze-cs: evaluated Lib\.csproj in process in \d+ ms; compiler arguments unchanged\r?$", RegexOptions.Multiline)
        || Rebuilt(output).Count != 0)
        Fail($"changed arguments through the daemon: expected the in-process pass to find Lib's arguments as the request gave them, and nothing to compile:\n{output}");
    Verify("changed arguments through the daemon", [], "the daemon's pipe");

    // Undone in process: Lib takes the arguments the pass gives, and compiles.
    Replace("Lib/Lib.csproj", property, "");
    Replace("Lib/Greeter.cs", member, "");
    output = Run(blaze, "build");
    if (!output.Contains("blaze-cs: evaluated Lib.csproj in process in"))
        Fail($"changed arguments undone: expected blaze-cs to evaluate Lib.csproj in process:\n{output}");
    var undone = Rebuilt(output);
    if (!undone.SetEquals(lib))
        Fail($"changed arguments undone: expected blaze-cs to rebuild {string.Join(", ", lib)}, it rebuilt {string.Join(", ", undone.Order())}:\n{output}");
    Verify("changed arguments undone", undone, null);
}

// The daemon answers only its own solution's compiles, and only for the Roslyn it loaded: a project of another
// solution, and a Csc task from a Roslyn with another commit hash (here the SDK's, with the hash in its task
// assembly changed), are rejected, and those builds succeed through csc.
void RejectedRequests(string pipe)
{
    Directory.CreateDirectory(otherSolution);
    File.Copy(Path.Combine(work, "global.json"), Path.Combine(otherSolution, "global.json"), overwrite: true);
    File.WriteAllText(Path.Combine(otherSolution, "Other.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
    File.WriteAllText(Path.Combine(otherSolution, "Other.cs"), "namespace Other;\n\npublic static class Thing\n{\n}\n");
    var compiles = CompilerServerLog(BuildThrough(pipe, $"\"{Path.Combine(otherSolution, "Other.csproj")}\""));
    if (compiles.GetValueOrDefault("Other (net10.0)") is not { } other || !other.StartsWith("server failed: server rejected the request", StringComparison.Ordinal)
        || !other.Contains("is not the output of a project of") || !File.Exists(Path.Combine(otherSolution, "obj", "Debug", "net10.0", "Other.dll")))
        Fail($"another solution: expected its compile rejected and built by csc, got:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
    Console.WriteLine($"ok  another solution's compile: {other}; built by csc");

    // The SDK's Roslyn directory with a task assembly whose CommitHashAttribute (prolog, length 40, the hash) differs.
    // The SDK loads the Csc task from RoslynTasksAssembly, which it sets from its own directory, not RoslynTargetsPath.
    var version = Run("dotnet", "--version").Trim();
    var sdkRoot = Run("dotnet", "--list-sdks").Split('\n', StringSplitOptions.TrimEntries).Select(line => line.Split(' ', 2))
        .Where(parts => parts.Length == 2 && parts[0] == version).Select(parts => parts[1].Trim('[', ']')).Single();
    CopyDirectory(Path.Combine(sdkRoot, version, "Roslyn"), roslynCopy);
    var tasks = Path.Combine(roslynCopy, "Microsoft.Build.Tasks.CodeAnalysis.dll");
    var bytes = File.ReadAllBytes(tasks);
    var at = Enumerable.Range(0, bytes.Length - 45).Single(i => bytes[i] == 1 && bytes[i + 1] == 0 && bytes[i + 2] == 40
        && bytes.AsSpan(i + 3, 40).ToArray().All(b => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f') && bytes[i + 43] == 0 && bytes[i + 44] == 0);
    for (var i = at + 3; i < at + 43; i++)
        bytes[i] = bytes[i] == (byte)'0' ? (byte)'1' : (byte)'0';
    File.SetAttributes(tasks, FileAttributes.Normal); // a copy of a read-only SDK (a nix store's) is read-only too
    File.WriteAllBytes(tasks, bytes);
    var output = BuildThrough(pipe, $"Lib/Lib.csproj --no-incremental \"-p:RoslynTasksAssembly={tasks}\"");
    compiles = CompilerServerLog(output);
    if (!lib.All(compiles.ContainsKey) || compiles.Values.Any(result => !result.StartsWith("server failed: server rejected the request 'compiler hash", StringComparison.Ordinal)))
        Fail($"another compiler hash: expected every compile rejected for its hash and built by csc, got:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
    Console.WriteLine($"ok  a compile from a task with another compiler hash: {compiles[lib[0]]}; built by csc");
    Verify("compiles the daemon rejected", [], "csc");
}

// csc runs a project's analyzers beside its compile, and a build through the pipe gets what they report as csc
// reports it: a CA2200 warning (Lib/.editorconfig, which is new, so Lib's compiler arguments change with it) is
// in the output of the daemon's answer, as in a build without the pipe. With the rule at error the daemon
// rejects Lib's compile, csc fails it, and the build fails. Once the .editorconfig is gone and a build outside
// the daemon compiled Lib without it, the next blaze-cs build reloads fresh instead of failing on the cached
// arguments that name it.
void AnalyzersThroughTheDaemon(string pipe)
{
    const string rethrow = "    public static void Rethrow()\n    {\n        try { System.Console.WriteLine(); }\n        catch (System.Exception e) { throw e; }\n    }\n\n";
    var editorConfig = Path.Combine(work, "Lib", ".editorconfig");
    Replace("Lib/Greeter.cs", "    public static int Twice", rethrow + "    public static int Twice");
    File.WriteAllText(editorConfig, "[*.cs]\ndotnet_diagnostic.CA2200.severity = warning\n");
    var output = BuildThrough(pipe, "Fixture.slnx");
    var compiles = CompilerServerLog(output);
    Verify("analyzer warning through the daemon", [], "the daemon's pipe");
    // What csc itself prints, with no server: last, as it writes Lib's outputs again.
    var csc = Run("dotnet", "build Lib/Lib.csproj -nologo -v:q --no-incremental -p:UseSharedCompilation=false");
    if (lib.Any(unit => compiles.GetValueOrDefault(unit) != "server: server processed compilation")
        || Printed(output, "CA2200") is not { Count: > 0 } warnings || !warnings.SetEquals(Printed(csc, "CA2200")))
        Fail($"analyzer warning through the daemon: expected it to answer Lib's compiles with csc's CA2200 warnings ({string.Join("; ", Printed(csc, "CA2200"))}), got {string.Join("; ", Printed(output, "CA2200"))}:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
    Console.WriteLine($"ok  analyzer warning through the daemon: {string.Join("; ", warnings)}, as csc prints it");

    File.WriteAllText(editorConfig, "[*.cs]\ndotnet_diagnostic.CA2200.severity = error\n");
    var (failed, exitCode) = BuildThroughWithExit(pipe, "Fixture.slnx", check: false);
    compiles = CompilerServerLog(failed);
    if (exitCode == 0 || !Printed(failed, "CA2200").Any(line => line.Contains(" error ", StringComparison.Ordinal))
        || lib.Any(unit => compiles.GetValueOrDefault(unit) is not { } result || !result.StartsWith("server failed: server rejected the request", StringComparison.Ordinal) || !result.Contains("CA2200")))
        Fail($"analyzer error through the daemon: expected Lib's compiles rejected for CA2200, csc's error CA2200 and a failed build, got exit code {exitCode}:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}\n{string.Join("\n", Printed(failed, "CA2200"))}");
    Console.WriteLine($"ok  analyzer error through the daemon: {compiles[lib[0]]}; csc failed the build");

    // At suggestion, csc prints nothing for it (info goes only to an /errorlog), and runs no analyzer that has
    // nothing above info to report.
    File.WriteAllText(editorConfig, "[*.cs]\ndotnet_diagnostic.CA2200.severity = suggestion\n");
    output = BuildThrough(pipe, "Fixture.slnx");
    compiles = CompilerServerLog(output);
    if (lib.Any(unit => compiles.GetValueOrDefault(unit) != "server: server processed compilation") || Printed(output, "CA2200").Count > 0)
        Fail($"analyzer suggestion through the daemon: expected it to answer Lib's compiles and print nothing for CA2200, got:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}\n{string.Join("\n", Printed(output, "CA2200"))}");
    Console.WriteLine("ok  analyzer suggestion through the daemon: nothing printed for it, as csc prints nothing");

    // A bulk severity raises analyzer rules to error before they reach the daemon, which keeps them errors: the
    // compile fails as csc's would, with csc's errors, and writes nothing, so the next build fails again.
    foreach (var (setting, scope) in new[] { ("dotnet_analyzer_diagnostic.category-Usage.severity = error", "category"), ("dotnet_analyzer_diagnostic.severity = error", "all-rules") })
    {
        File.WriteAllText(editorConfig, $"[*.cs]\n{setting}\n");
        var assemblies = lib.Select(unit => Path.Combine(work, "Lib", "obj", "Debug", unit[(unit.IndexOf('(') + 1)..^1], "Lib.dll")).ToList();
        var before = assemblies.Select(path => (File.GetLastWriteTimeUtc(path), File.ReadAllBytes(path))).ToList();
        var (bulk, bulkExit) = BuildThroughWithExit(pipe, "Lib/Lib.csproj", check: false);
        var (again, againExit) = BuildThroughWithExit(pipe, "Lib/Lib.csproj", check: false);
        var (cscBulk, cscExit) = RunWithExit("dotnet", "build Lib/Lib.csproj -nologo -v:q -p:UseSharedCompilation=false", check: false);
        compiles = CompilerServerLog(bulk);
        var errors = Printed(bulk, @"CA\d+");
        if (bulkExit == 0 || againExit == 0 || cscExit == 0 || lib.Any(unit => compiles.GetValueOrDefault(unit) != "server: server processed compilation")
            || !errors.Any(line => line.Contains(" error CA2200 ", StringComparison.Ordinal)) || !errors.SetEquals(Printed(cscBulk, @"CA\d+"))
            || assemblies.Select(path => (File.GetLastWriteTimeUtc(path), File.ReadAllBytes(path))).Zip(before).Any(pair => pair.First.Item1 != pair.Second.Item1 || !pair.First.Item2.AsSpan().SequenceEqual(pair.Second.Item2)))
            Fail($"{scope} analyzer severity through the daemon: expected it to fail Lib's compiles with csc's errors ({string.Join("; ", Printed(cscBulk, @"CA\d+"))}) and leave Lib's assemblies as they were, twice; "
                + $"got exit codes {bulkExit} and {againExit} (csc {cscExit}), {string.Join("; ", errors)}:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
        Console.WriteLine($"ok  {scope} analyzer severity through the daemon: {string.Join("; ", errors)}, as csc reports it; nothing written, and the next build failed too");
    }

    File.Delete(editorConfig);
    Replace("Lib/Greeter.cs", rethrow, "");
    Run("dotnet", "build Fixture.slnx -nologo -v:q -clp:ErrorsOnly");
    output = Run(blaze, "build");
    if (!output.Contains("cached compiler arguments name a file that is gone"))
        Fail($"deleted .editorconfig: expected blaze-cs to load fresh for the cached arguments that name it:\n{output}");
    Console.WriteLine("ok  deleted .editorconfig the cached arguments named: blaze-cs loaded fresh");
    Verify("analyzers through the daemon undone", [], "fallback");
}

// A compile through the pipe runs csc's analysis, compilation-end rules included, and the build prints what it
// found (CA1812, see CompilationEndAnalyzers): as after a build outside the daemon, the background analysis then
// has nothing to report on the project. Here the background analysis finished on Lib as it is before the compile,
// which changes nothing in it (--no-incremental, same arguments), so only that the compile counts drops it.
void CompilationEndThroughTheDaemon(string pipe)
{
    var unused = Path.Combine(work, "Lib", "Unused.cs");
    const string unusedClass = "namespace Lib;\n\ninternal sealed class Unused\n{\n}\n";
    List<string> Answered(string name, string output)
    {
        var compiles = CompilerServerLog(output);
        var printed = Printed(output, "CA1812").ToList();
        if (lib.Any(unit => compiles.GetValueOrDefault(unit) != "server: server processed compilation") || printed.Count == 0
            || !printed.All(line => line.StartsWith("Unused.cs(", StringComparison.Ordinal) && line.Contains(" warning CA1812 ", StringComparison.Ordinal)))
            Fail($"{name}: expected the daemon to answer Lib's compiles with CA1812 in {unused}, got {string.Join("; ", printed)}:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
        return printed;
    }

    // Through the pipe first, so Lib's arguments name the new file.
    File.WriteAllText(unused, unusedClass);
    Answered("compilation-end rules through the daemon, new file", BuildThrough(pipe, "Fixture.slnx"));
    File.WriteAllText(unused, "// Moved down a line.\n" + unusedClass);
    var output = Run(blaze, "build");
    var built = DateTimeOffset.Now;
    if (!Rebuilt(output).SetEquals(lib) || Warnings(output, "CA1812").Count != 0)
        Fail($"compilation-end rules through the daemon: expected {string.Join(", ", lib)} rebuilt and no CA1812 from the build itself:\n{output}");
    WaitForBackgroundAnalysis("compilation-end rules through the daemon", built);
    var printed = Answered("compilation-end rules through the daemon", BuildThrough(pipe, "Fixture.slnx --no-incremental"));
    output = Run(blaze, "build");
    if (Warnings(output, "CA1812").Count != 0 || Regex.IsMatch(output, @"background analysis \(compilation-end rules\) of .*Lib \("))
        Fail($"compilation-end rules through the daemon: expected the background analysis of Lib, which the compile through the pipe reported, dropped:\n{output}");
    Console.WriteLine($"ok  compilation-end rules through the daemon: {string.Join("; ", printed)}, as csc prints it; the background analysis' report on Lib dropped");

    File.Delete(unused);
    BuildThrough(pipe, "Fixture.slnx");
    Verify("compilation-end rules through the daemon undone", [], "the daemon's pipe");
}

// A build through the pipe while no daemon runs starts the SDK's VBCSCompiler on it. The next daemon shuts that
// server down before it listens, so the pipe has one server, and the next build through it reaches the daemon.
void PipeTakenOver(string pipe)
{
    Run(blaze, "stop");
    var compiles = CompilerServerLog(BuildThrough(pipe, "Lib/Lib.csproj --no-incremental"));
    if (lib.Any(unit => compiles.GetValueOrDefault(unit) != "server: server processed compilation"))
        Fail($"pipe without a daemon: expected VBCSCompiler to answer Lib's compiles, got:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}");
    var logged = ReadLog("daemon.log").Length;
    Run(blaze, "build");
    var log = ReadLog("daemon.log")[logged..];
    if (Regex.Match(log, @"compiler server: shut down the server that held the pipe \(pid (\d+)\)") is not { Success: true } shutDown || Running(int.Parse(shutDown.Groups[1].Value)))
        Fail($"pipe taken over: expected the daemon to shut down the VBCSCompiler on its pipe:\n{log}");
    // The next command waits for the new daemon's warm-up, which compares Lib's assembly on disk: a build with
    // --no-incremental cleans it first.
    Run(blaze, "build");
    compiles = CompilerServerLog(BuildThrough(pipe, "Lib/Lib.csproj --no-incremental"));
    log = ReadLog("daemon.log")[logged..];
    if (lib.Any(unit => compiles.GetValueOrDefault(unit) != "server: server processed compilation" || !log.Contains($"compiler server: {unit}: compiled in")))
        Fail($"pipe taken over: expected the daemon to answer Lib's compiles, got:\n{string.Join("\n", compiles.Select(c => $"{c.Key}: {c.Value}"))}\n{log}");
    Console.WriteLine($"ok  pipe taken over: the daemon shut down VBCSCompiler (pid {shutDown.Groups[1].Value}), which a build through the pipe had started, and answered the next build's compiles");
    Verify("pipe taken over", [], "the daemon's pipe");
}

// With BLAZE_CS_COMPILER_SERVER=0 the daemon has no pipe and names none; without it, the next daemon has it again.
void ServerTurnedOff(string pipe)
{
    Run(blaze, "stop");
    var output = Run(blaze, "build", environment: new() { ["BLAZE_CS_COMPILER_SERVER"] = "0" });
    if (output.Contains("SharedCompilationId") || PipeAnswers(pipe) || !ReadLog("daemon.log").Contains("compiler server off"))
        Fail($"pipe turned off: expected no pipe named or listening:\n{output}");
    Run(blaze, "stop");
    output = Run(blaze, "build");
    if (PipeName(output) != pipe || !PipeAnswers(pipe))
        Fail($"pipe back on: expected the daemon to name and listen on {pipe} again:\n{output}");
    Console.WriteLine("ok  BLAZE_CS_COMPILER_SERVER=0: the daemon had no pipe; the next one listened again");
}

// Whether something listens on the pipe, where the Csc task would connect to it.
static bool PipeAnswers(string pipe)
{
    using var client = new NamedPipeClientStream(".", OperatingSystem.IsWindows() ? pipe : Path.Combine("/tmp", pipe), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
    try
    {
        client.Connect(1000);
        return true;
    }
    catch (Exception e) when (e is TimeoutException or IOException)
    {
        return false;
    }
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

    // The class and a project file edit in one step: the fallback compiles Lib, and prints errors only. A comment
    // would be evaluated in process; a property the pass doesn't take (FixtureFallback) goes through dotnet build.
    File.WriteAllText(unused, unusedClass);
    Replace("Lib/Lib.csproj", "</Project>", "  <PropertyGroup><FixtureFallback>Unused.cs</FixtureFallback></PropertyGroup>\n</Project>");
    built = DateTimeOffset.Now;
    output = Run(blaze, "build");
    if (!output.Contains(FallbackFor("Lib")) || Warnings(output, "CA1812").Count != 0)
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
    Replace("App/App.csproj", "</Project>", "  <PropertyGroup><FixtureFallback>App</FixtureFallback></PropertyGroup>\n</Project>");
    built = DateTimeOffset.Now;
    output = Run(blaze, "build");
    if (!output.Contains(FallbackFor("App")))
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
    Replace("Lib/Lib.csproj", "  <PropertyGroup><FixtureFallback>Unused.cs</FixtureFallback></PropertyGroup>\n", "");
    output = Run(blaze, "build");
    if (!output.Contains(FallbackFor("Lib")) || Warnings(output, "CA1812").Count != 0)
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
// A taken host is gone, or stays up for its project (a Debug project's, for hot reload).
void TookEveryHost(List<(int Pid, string Project)> prestarted)
{
    var live = LiveHosts();
    var taken = prestarted.Where(host => !Running(host.Pid) || live.GetValueOrDefault(host.Pid) == host.Project).Count();
    if (taken != prestarted.Count)
        Fail($"expected the first test run to take all {prestarted.Count} pre-started hosts, {taken} are gone or stay up for their project");
    IdleHosts("after the first test run");
    Console.WriteLine($"ok  the first test run took the {taken} hosts started for {string.Join(", ", prestarted.Select(h => h.Project).Order())}; "
        + $"each test project's host is one of at most {HostCap}, with a spare per runtime, and they are back after each run");
}

// A host waiting for its project has loaded the project's packages and run its warm-up assembly, and loaded
// none of the solution's outputs (on Linux, where its mapped files show it), so a project reference rebuilt
// after the host started is the new bytes when the run loads it: an edit to Consumer fails each test
// project's greeting test with the new text, whether its host was waiting or started for the run; undone,
// the tests pass again. The hosts that ran before now stay up for their projects, so a new daemon starts
// hosts that have run nothing; its first test run runs every test project.
void PreparedHostsSeeNewBytes()
{
    Run(blaze, "stop");
    Run(blaze, "build");
    var hosts = IdleHosts("before the Consumer edit");
    if (OperatingSystem.IsLinux())
        hosts.ForEach(NoProjectOutputLoaded);
    Replace("Consumer/Welcome.cs", "=> Lib.Greeter.Greet(who)", "=> Lib.Greeter.Greet(who).Replace(\"Hello\", \"Howdy\")");
    var (output, exitCode) = RunWithExit(blaze, "test", check: false);
    var summaries = Summaries(output);
    if (exitCode == 0 || !Rebuilt(output).SetEquals(consumer) || !output.Contains("Howdy") || !summaries.Keys.Order().SequenceEqual(allTests.Order())
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

// Why the running daemon has no hot reload engine (on Windows, or an SDK without the one blaze-cs knows), or null if it has.
string? HotReloadUnavailableBecause()
{
    var log = ReadLog("daemon.log");
    var since = log.LastIndexOf($"(pid {Daemons().Last().Pid}, exits after", StringComparison.Ordinal);
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(30))
    {
        var current = ReadLog("daemon.log")[Math.Max(0, since)..];
        if (current.Contains(" hot reload engine loaded"))
            return null;
        if (Regex.Match(current, @"^\S+ hot reload is unavailable: (.+?)\r?$", RegexOptions.Multiline) is { Success: true } unavailable)
            return unavailable.Groups[1].Value;
        Thread.Sleep(100);
    }
    Fail($"expected the daemon to log whether hot reload is available:\n{log}");
    return null;
}

// Hosts that stay up after their run (TestRunner.Keep), by pid, with their project, as the daemon logs them; only
// those still running.
Dictionary<int, string> LiveHosts() =>
    Regex.Matches(ReadLog("daemon.log"), @"^\S+ hot reload: test host (\d+) \((\w+)\.dll\) stays up for ", RegexOptions.Multiline)
        .Select(m => (Pid: int.Parse(m.Groups[1].Value), Project: m.Groups[2].Value))
        .Where(host => Running(host.Pid))
        .ToDictionary(host => host.Pid, host => host.Project);

// A method body edit in Lib reaches the hosts that stay up for the Debug test projects as a delta: each runs its
// tests again in the same process (the output and the daemon log name the host and the module), with the same
// passes and failures as `dotnet test` of the same build, and Lib's files on disk are what dotnet build makes.
// Undone, the tests pass again, in the hosts that stay up then. (Five test projects run with Lib, and hosts
// stay up for the four run most recently.)
void HotReloadBodyEdit()
{
    if (HotReloadUnavailableBecause() is { } reason)
    {
        Console.WriteLine($"ok  hot reload: unavailable here ({reason}); the test runs above took the path without it");
        return;
    }
    var reloadedInto = new HashSet<string>();
    foreach (var undo in new[] { false, true })
    {
        var name = undo ? "hot reload undone" : "hot reload";
        IdleHosts($"before {name}"); // and the hosts of projects no longer among the most recent are gone
        var live = LiveHosts().Where(host => libTests.Contains(host.Value)).ToDictionary();
        if (live.Count == 0)
            Fail($"{name}: expected hosts staying up for some of {string.Join(", ", libTests)}:\n{ReadLog("daemon.log")}");
        if (undo)
            Replace("Lib/Greeter.cs", "\"Howdy there, \"", "\"Hello there, \"");
        else
            Replace("Lib/Greeter.cs", "\"Hello there, \"", "\"Howdy there, \"");
        var (output, exitCode) = RunWithExit(blaze, "test", check: false);
        if (!Rebuilt(output).SetEquals(lib))
            Fail($"{name}: expected blaze-cs to rebuild {string.Join(", ", lib)}:\n{output}");
        var reloaded = Regex.Matches(output, @"^blaze-cs: (\w+)\.dll: hot reloaded Lib \(net10\.0\) \(1 type\) into its test host \(pid (\d+)\)", RegexOptions.Multiline)
            .ToDictionary(m => int.Parse(m.Groups[2].Value), m => m.Groups[1].Value);
        if (!reloaded.OrderBy(h => h.Key).SequenceEqual(live.OrderBy(h => h.Key)))
            Fail($"{name}: expected Lib hot reloaded into the hosts of {string.Join(", ", live.Select(h => $"{h.Value} (pid {h.Key})"))}, which stayed up:\n{output}");
        reloadedInto.UnionWith(live.Values);
        var log = ReadLog("daemon.log");
        if (live.FirstOrDefault(host => !Regex.IsMatch(log, $@"hot reload: test host {host.Key} \({host.Value}\.dll\) applied a delta to module Lib \(net10\.0\) \([0-9a-f-]+\): 1 type updated")) is { Key: > 0 } unlogged)
            Fail($"{name}: expected the daemon log to name host {unlogged.Key}, Lib (net10.0) and 1 type updated:\n{log}");
        if (undo)
            RanTests(name, output, libTests);
        else
        {
            if (exitCode == 0)
                Fail($"{name}: expected each greeting test to fail on \"Howdy\":\n{output}");
            SameAsDotnetTest(name, output, libTests);
        }
        Verify(name, [.. lib], null);
    }
    Console.WriteLine($"ok  hot reload: a Lib body edit and its undo reached the hosts that stayed up ({string.Join(", ", reloadedInto.Order())}) as deltas, in the same processes; the failures matched dotnet test's");
}

// Edits a delta can't carry don't hot reload: each host that stayed up for a Debug test project ends, the
// project runs in a new host, and the failures match dotnet test's (a host that took the edit as a delta would
// still pass, on the old value).
// - A static field's initializer: the engine has a delta (the type initializer's body), but the initializer
//   already ran in the host, so the engine asks for a restart (RestartWhenChangesHaveNoEffect).
// - An embedded resource, with a body edit in the same assembly: the engine has a delta for the body, which
//   doesn't carry the resource.
void HotReloadFallsBack()
{
    if (HotReloadUnavailableBecause() is not null)
        return;
    const string staticInit = "Mark = \"!\"", body = "Twice(int amount) => amount + amount";
    var shouted = false; // four steps: each toggles it, and it ends as it was
    FallsBack("static initializer edit", () => Replace("Lib/Salutation.cs", staticInit, "Mark = \"?\""), fails: true, reason: null);
    FallsBack("static initializer edit undone", () => Replace("Lib/Salutation.cs", "Mark = \"?\"", staticInit), fails: false, reason: null);
    const string resourced = "Lib (net10.0)'s resources changed, which a delta doesn't carry";
    FallsBack("resource and body edit", () =>
    {
        File.WriteAllText(Path.Combine(work, "Lib/greeting.txt"), "Howdy\n");
        Replace("Lib/Greeter.cs", body, "Twice(int amount) => amount << 1");
    }, fails: true, reason: resourced);
    FallsBack("resource and body edit undone", () =>
    {
        File.WriteAllText(Path.Combine(work, "Lib/greeting.txt"), "Hello\n");
        Replace("Lib/Greeter.cs", "Twice(int amount) => amount << 1", body);
    }, fails: false, reason: resourced);
    Console.WriteLine("ok  hot reload fallback: a static initializer edit, and a resource edit with a body edit, ran in new hosts; the failures matched dotnet test's");

    void FallsBack(string name, Action edit, bool fails, string? reason)
    {
        // The step before ran its projects in new hosts, mostly spares, which don't stay up. A body edit in Lib
        // runs them in the hosts prepared for them meanwhile, which stay up, and hot reloads into the ones that
        // did stay up. (Not `test --all`: with nothing changed, it ends the hosts that stayed up. Before an
        // undo, Tests still fails.)
        IdleHosts($"before {name}, before a body edit");
        shouted = !shouted;
        if (shouted)
            Replace("Lib/Greeter.cs", "who.ToUpperInvariant()", "who.ToUpperInvariant().ToString()");
        else
            Replace("Lib/Greeter.cs", "who.ToUpperInvariant().ToString()", "who.ToUpperInvariant()");
        Run(blaze, "test", check: false);
        IdleHosts($"before {name}");
        var live = LiveHosts().Where(host => libTests.Contains(host.Value)).ToDictionary();
        if (live.Count == 0)
            Fail($"{name}: expected hosts staying up for some of {string.Join(", ", libTests)}:\n{ReadLog("daemon.log")}");
        var logged = ReadLog("daemon.log").Length;
        edit();
        var (output, exitCode) = RunWithExit(blaze, "test", check: false);
        if (!Rebuilt(output).SetEquals(lib) || output.Contains("hot reloaded"))
            Fail($"{name}: expected blaze-cs to rebuild {string.Join(", ", lib)} and hot reload nothing:\n{output}");
        var log = ReadLog("daemon.log")[logged..];
        foreach (var (pid, project) in live)
        {
            var ended = Regex.Match(log, $@"hot reload: test host {pid} \({project}\.dll\) ends, and {project}\.dll runs in a new host: (.+?)\r?$", RegexOptions.Multiline);
            if (!ended.Success || (reason is null ? ended.Groups[1].Value.StartsWith("nothing", StringComparison.Ordinal) : ended.Groups[1].Value != reason))
                Fail($"{name}: expected host {pid} ({project}) to end{(reason is null ? "" : $" because {reason}")}:\n{log}");
        }
        if (fails)
        {
            if (exitCode == 0)
                Fail($"{name}: expected Tests to fail:\n{output}");
            SameAsDotnetTest(name, output, libTests);
        }
        else
            RanTests(name, output, libTests);
        Verify(name, [.. lib], null);
    }
}

// Each project's summary and failed tests in a blaze-cs run, as `dotnet test --no-build` reports them.
void SameAsDotnetTest(string name, string output, string[] projects)
{
    var ours = Summaries(output);
    var ourFailures = FailedTests(output);
    foreach (var project in projects)
    {
        var theirOutput = RunWithExit("dotnet", $"test --no-build {project} -nologo", check: false).Output;
        var theirs = Summaries(theirOutput).GetValueOrDefault(project);
        var theirFailures = FailedTests(theirOutput).Values.SelectMany(names => names).Select(Method).ToHashSet();
        if (ours.GetValueOrDefault(project) is not { } summary || summary != theirs
            || !ourFailures.GetValueOrDefault(project, []).Select(Method).ToHashSet().SetEquals(theirFailures))
            Fail($"{name}: {project}: expected the run to match dotnet test's: {ours.GetValueOrDefault(project)} failing {string.Join(", ", ourFailures.GetValueOrDefault(project, []))}, "
                + $"dotnet test {theirs} failing {string.Join(", ", theirFailures)}:\n{theirOutput}");
    }
}

// A test's name without its namespace and class, which NUnit's adapter leaves out under dotnet test and the host doesn't.
static string Method(string test) => test[(test[..(test.IndexOf('(') is >= 0 and var arguments ? arguments : test.Length)].LastIndexOf('.') + 1)..];

// The tests each project's run failed, by project: "  Failed <name>" (with " [<duration>]" from dotnet test), or
// xUnit's "<name> [FAIL]". A project's lines come before its summary line; those after the last one are under "".
// (dotnet test prints xUnit's lines after its summary: of its output, take every name.)
static Dictionary<string, HashSet<string>> FailedTests(string output)
{
    var failed = new Dictionary<string, HashSet<string>>();
    var pending = new HashSet<string>();
    foreach (var line in output.Split('\n').Select(line => line.TrimEnd('\r')))
    {
        if (Regex.Match(line, @"^  Failed (\S+)(?: \[[^\]]*\])?$|^(?:\[xUnit\.net [^\]]*\])?\s+(\S+) \[FAIL\]$") is { Success: true } test)
            pending.Add(test.Groups[1].Success ? test.Groups[1].Value : test.Groups[2].Value);
        else if (Regex.Match(line, @"^\s*\w+!\s+- Failed:.* - (\S+)\.dll\b") is { Success: true } summary)
        {
            failed[summary.Groups[1].Value] = pending;
            pending = [];
        }
    }
    failed[""] = pending;
    return failed;
}

// Without the SDK's hot reload engine (here, BLAZE_CS_DOTNET_WATCH names an empty directory in its place), a
// daemon's first test run says so once, with why, and runs as before: no host stays up after its run, none is
// started so it could, and the next run says nothing more about it.
void HotReloadUnavailable()
{
    var empty = Directory.CreateDirectory(Path.Combine(work, ".no-dotnet-watch")).FullName;
    Run(blaze, "stop");
    try
    {
        Run(blaze, "build", environment: new() { ["BLAZE_CS_DOTNET_WATCH"] = empty });
        var daemon = Daemons().Last().Pid;
        var unavailable = OperatingSystem.IsWindows() ? "blaze-cs: hot reload is unavailable: on Windows"
            : $"blaze-cs: hot reload is unavailable: the SDK has no Microsoft.CodeAnalysis.Workspaces.dll in {empty}";
        var first = Run(blaze, "test");
        if (Regex.Matches(first, "^blaze-cs: hot reload", RegexOptions.Multiline).Count != 1 || !first.Contains(unavailable))
            Fail($"hot reload unavailable: expected one line \"{unavailable}\":\n{first}");
        Replace("Lib/Greeter.cs", "Twice(int amount) => amount + amount", "Twice(int amount) => amount * 2");
        var next = Run(blaze, "test");
        if (next.Contains("hot reload") || !Summaries(next).Keys.Order().SequenceEqual(libTests.Order()))
            Fail($"hot reload unavailable: expected the next run to run {string.Join(", ", libTests)} as before, without a word about hot reload:\n{next}");
        var log = ReadLog("daemon.log");
        var since = log.LastIndexOf($"(pid {daemon}, exits after", StringComparison.Ordinal);
        if (since < 0 || log[since..].Contains("stays up for"))
            Fail($"hot reload unavailable: expected no host to stay up after its run:\n{log}");
        if (OperatingSystem.IsLinux() && TestHostProcesses().FirstOrDefault(host => Modifiable(host.Pid)) is { Pid: > 0 } modifiable)
            Fail($"hot reload unavailable: expected no host started with DOTNET_MODIFIABLE_ASSEMBLIES, found {modifiable.Command}");
        Console.WriteLine("ok  hot reload unavailable: said once why, and the test runs went on as before, with no host kept or started for it");
    }
    finally
    {
        Run(blaze, "stop", check: false);
        Directory.Delete(empty);
        if (File.ReadAllText(Path.Combine(work, "Lib/Greeter.cs")).Contains("Twice(int amount) => amount * 2"))
            Replace("Lib/Greeter.cs", "Twice(int amount) => amount * 2", "Twice(int amount) => amount + amount");
    }
}

// Linux: whether a process runs with DOTNET_MODIFIABLE_ASSEMBLIES set.
static bool Modifiable(int pid)
{
    try
    {
        return File.ReadAllText($"/proc/{pid}/environ").Split('\0').Any(variable => variable.StartsWith("DOTNET_MODIFIABLE_ASSEMBLIES=", StringComparison.Ordinal));
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        return false;
    }
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
    var none = FellBack("project removed", output, "Fixture.slnx changed, running dotnet build");
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
    FellBack("project restored", output, "Fixture.slnx changed");
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

// A test project whose tests failed runs again on the next `test`, though nothing changed since: in a new host,
// not the one that stayed up for it, whose statics the first run left (TestRunner.Plan).
void FailedRunsAgain()
{
    Replace("Tests/WelcomeTests.cs", "StartsWith(\"Hello\"", "StartsWith(\"Goodbye\"");
    try
    {
        for (var run = 1; run <= 2; run++)
        {
            var logged = ReadLog("daemon.log").Length;
            var (output, exitCode) = RunWithExit(blaze, "test", check: false);
            if (exitCode == 0 || !Regex.IsMatch(output, @"^\s*Failed!\s+- Failed:\s+1, .* - Tests\.dll\b", RegexOptions.Multiline)
                || run == 2 && (Rebuilt(output).Count != 0
                    || output.Contains("ran again") || output.Contains("hot reloaded")
                    || HotReloadUnavailableBecause() is null && !ReadLog("daemon.log")[logged..].Contains("(Tests.dll) ends, and Tests.dll runs in a new host: nothing its tests run with changed")))
                Fail($"failing test, run {run}: expected Tests to run again with one test failed{(run == 2 ? ", in a new host, and nothing rebuilt" : "")}, got exit {exitCode}:\n{output}");
        }
    }
    finally
    {
        Replace("Tests/WelcomeTests.cs", "StartsWith(\"Goodbye\"", "StartsWith(\"Hello\"");
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

// What blaze-cs logs when an edit to a project's file adds FixtureFallback, which the in-process pass doesn't take.
static string FallbackFor(string project) =>
    $"blaze-cs: {project}: the edit to {project}.csproj changes more than csc's input (<FixtureFallback>); running dotnet build";

// A case that must go through dotnet build: blaze-cs logs why, and then reloads with every assembly
// identical to that build's. The projects MSBuild compiled aren't reported, so the set is empty.
HashSet<string> FellBack(string name, string output, string fallback)
{
    // Its message names paths with this OS's separator.
    if (!output.Contains($"blaze-cs: {fallback.Replace('/', Path.DirectorySeparatorChar)}"))
        Fail($"{name}: expected blaze-cs to log \"{fallback}\":\n{output}");
    WarmedUpIdentical(name, output);
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

// Each test project has its two tests and RunsOnTargetRuntime, plus AppliesRunSettings where it has a .runsettings;
// Tests also has Exclaims and ReadsGreeting (HotReloadFallsBack).
static int TestCount(string project) => project switch { "Tests" => 5, "ComponentTests" => 2, "Tests11" => 1, "NUnitTests" or "MSTestTests" => 4, _ => 3 };

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

sealed record Case(string Name, Action Edit, string[] Expected, string Command, string? Fallback = null, string[]? Ran = null, string? Logged = null);
