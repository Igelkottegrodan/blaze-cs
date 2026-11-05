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

// (name, edit, projects blaze-cs must rebuild, command); the edits build on each other.
(string Name, Action Edit, string[] Expected, string Command)[] cases =
[
    ("body", () => Replace("Lib/Greeter.cs", "\"Hello, \"", "\"Hello there, \""), ["Lib"], "build"),
    ("signature used by Consumer",
        () => Replace("Lib/Greeter.cs", "Greet(string name) => \"Hello there, \" + name", "Greet(string person) => \"Hello there, \" + person"),
        ["Consumer", "Lib"], "build"),
    ("signature unused by Consumer",
        () => Replace("Lib/Greeter.cs", "Twice(int value) => value * 2", "Twice(int amount) => amount * 2"), ["Lib"], "build"),
    ("new file",
        () => File.WriteAllText(Path.Combine(work, "Lib/Probe.cs"), "namespace Lib;\n\ninternal static class Probe\n{\n    internal const int Edit = 1;\n}\n"),
        ["Lib"], "build"),
    ("generator output",
        () => Replace("Lib/Greeter.cs", "    public static int Twice",
            "    public static string Farewell(string name) => \"Bye, \" + name;\n\n    public static int Twice"),
        ["Consumer", "Lib"], "build"),
    ("test edit", () => Replace("Tests/WelcomeTests.cs", "\"you\"", "\"tester\""), ["Tests"], "test"),
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
        Verify(name, rebuilt);
    }
    passed = true;
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

// Compares what blaze-cs wrote with a full MSBuild rebuild of the same sources.
void Verify(string name, HashSet<string> rebuilt)
{
    var ours = Assemblies();
    Run("dotnet", "build Fixture.slnx -nologo -v:q -clp:ErrorsOnly --no-incremental");
    var theirs = Assemblies();
    if (!ours.Keys.ToHashSet().SetEquals(theirs.Keys))
        Fail($"{name}: assembly sets differ: {string.Join(", ", ours.Keys.ToHashSet().Except(theirs.Keys).Union(theirs.Keys.Except(ours.Keys)))}");
    var differing = ours.Keys.Where(path => !ours[path].AsSpan().SequenceEqual(theirs[path])).ToList();
    var inRebuilt = differing.Where(path => rebuilt.Contains(path.Split('/')[0])).ToList();
    if (inRebuilt.Count > 0)
        Fail($"{name}: blaze-cs compiled these but they differ from dotnet build: {string.Join(", ", inRebuilt)}");
    var beyondIdentity = differing.Where(path => !PeIdentity.SameCode(ours[path], theirs[path])).ToList();
    if (beyondIdentity.Count > 0)
        Fail($"{name}: skipped dependents differ beyond debug identity: {string.Join(", ", beyondIdentity)}");
    Console.WriteLine($"ok  {name}: rebuilt {(rebuilt.Count == 0 ? "nothing" : string.Join(", ", rebuilt.Order()))}; "
        + $"{ours.Count - differing.Count} of {ours.Count} assemblies identical, {differing.Count} same code");
}

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

[System.Diagnostics.CodeAnalysis.DoesNotReturn]
static void Fail(string message)
{
    Console.Error.WriteLine(message);
    Environment.Exit(1);
}
