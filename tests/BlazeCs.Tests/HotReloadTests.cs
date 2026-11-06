using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// The test host's half of hot reload (Protocol.cs): after a run it stays up, applies the delta the daemon
/// sends to an assembly it has loaded, and runs the tests again in the same process. Each framework's test
/// assembly is compiled here, with one failing test, into a copy of a kit (tests/TestHostKits: that
/// framework's packages, at the fixture's versions), and the delta, made with EmitDifference from a baseline
/// of that image, fixes the method the test fails on.
/// </summary>
public class HotReloadTests
{
    public static TheoryData<string> Frameworks => ["xunit3", "nunit", "mstest"];

    [Theory]
    [MemberData(nameof(Frameworks))]
    public void AnUpdateFixesTheFailingTestInTheSameHost(string framework)
    {
        using var subject = new Subject(framework);
        using var host = new Host(hotReload: true);
        var pid = host.Process.Id;

        var first = host.Send(subject.Run);
        Assert.Equal((2, 1, 1), (first.Total, first.Passed, first.Failed));
        Assert.Null(first.CannotHotReload);

        var second = host.Send(new TestHostUpdate([subject.Fix()]));
        Assert.Equal((2, 2, 0), (second.Total, second.Passed, second.Failed));
        Assert.Null(second.CannotHotReload);
        Assert.False(host.Process.HasExited);
        Assert.Equal(pid, host.Process.Id);

        Assert.True(TestRunner.Send(host.Process, new TestHostExit()));
        Assert.True(host.Process.WaitForExit(TimeSpan.FromSeconds(30)), "the host should exit on the exit message");
    }

    [Fact]
    public void AHostThatCannotApplyUpdatesSaysSoAndRunsOnce()
    {
        using var subject = new Subject("xunit3");
        using var host = new Host(hotReload: false);

        var result = host.Send(subject.Run);
        Assert.Equal((2, 1, 1), (result.Total, result.Passed, result.Failed));
        Assert.Contains("DOTNET_MODIFIABLE_ASSEMBLIES", result.CannotHotReload);
        Assert.True(host.Process.WaitForExit(TimeSpan.FromSeconds(30)), "the host should exit after its one run");
    }

    [Fact]
    public void ADeltaForNoLoadedAssemblyRunsNothingAndEndsTheHost()
    {
        using var subject = new Subject("xunit3");
        using var host = new Host(hotReload: true);
        Assert.Null(host.Send(subject.Run).CannotHotReload);

        var result = host.Send(new TestHostUpdate([subject.Fix() with { Module = Guid.NewGuid() }]));
        Assert.Equal(0, result.Total);
        Assert.Contains("no assembly with module", result.Unsupported);
        Assert.NotNull(result.CannotHotReload);
        Assert.True(host.Process.WaitForExit(TimeSpan.FromSeconds(30)), "the host should exit after a failed update");
    }

    /// <summary>
    /// A host's work ends with the exit message, with stdin closing while it waits for an update, or with its
    /// one run when it can't hot reload, and then the process ends too: a foreground thread a test started
    /// doesn't keep it running.
    /// </summary>
    [Theory]
    [InlineData("exit")]
    [InlineData("end of input")]
    [InlineData("no hot reload")]
    public void AHostEndsDespiteATestsForegroundThread(string ending)
    {
        using var subject = new Subject("xunit3", "[Fact] public void StartsAThread() => new Thread(() => Thread.Sleep(Timeout.Infinite)).Start();");
        using var host = new Host(hotReload: ending != "no hot reload");

        var result = host.Send(subject.Run);
        Assert.Equal((3, 2, 1), (result.Total, result.Passed, result.Failed));
        if (ending == "exit")
            Assert.True(TestRunner.Send(host.Process, new TestHostExit()));
        else if (ending == "end of input")
            host.Process.StandardInput.Close();
        Assert.True(host.Process.WaitForExit(TimeSpan.FromSeconds(30)), $"the host should exit ({ending})");
    }

    /// <summary>
    /// Stdin closing right after a message, as when the daemon dies having sent it, ends the host before it
    /// acts on it, even while it loads the test assembly or applies the delta: nobody would read the result.
    /// The tests sleep 20 s once they run; the host must be gone well before.
    /// </summary>
    [Fact]
    public void StdinClosingRightAfterARunEndsTheHostWithoutRunning()
    {
        using var subject = new Subject("xunit3", "[Fact] public void Sleeps() => Thread.Sleep(20_000);");
        using var host = new Host(hotReload: true);

        Assert.True(TestRunner.Send(host.Process, subject.Run));
        host.Process.StandardInput.Close();
        Assert.True(host.Process.WaitForExit(TimeSpan.FromSeconds(10)), "the host should exit without running the tests");
        Assert.Null(TestRunner.ReadResult(host.Process, []));
    }

    /// <inheritdoc cref="StdinClosingRightAfterARunEndsTheHostWithoutRunning"/>
    [Fact]
    public void StdinClosingRightAfterAnUpdateEndsTheHostWithoutRunning()
    {
        using var subject = new Subject("xunit3", "[Fact] public void SleepsOnceFixed() { if (Answer.Value() == 42) Thread.Sleep(20_000); }");
        using var host = new Host(hotReload: true);
        Assert.Equal((3, 2, 1), host.Send(subject.Run) is var first ? (first.Total, first.Passed, first.Failed) : default);

        Assert.True(TestRunner.Send(host.Process, new TestHostUpdate([subject.Fix()])));
        host.Process.StandardInput.Close();
        Assert.True(host.Process.WaitForExit(TimeSpan.FromSeconds(10)), "the host should exit without running the tests again");
        Assert.Null(TestRunner.ReadResult(host.Process, []));
    }

    /// <summary>A line the host can't read gets an error result, and ends it.</summary>
    [Fact]
    public void AnUnreadableLineIsReportedAndEndsTheHost()
    {
        using var subject = new Subject("xunit3");
        using var host = new Host(hotReload: true);
        Assert.Null(host.Send(subject.Run).CannotHotReload);

        host.Process.StandardInput.WriteLine("{\"kind\":\"upd");
        var result = TestRunner.ReadResult(host.Process, []);
        Assert.Contains("a line it couldn't read", result?.Error);
        Assert.NotNull(result?.CannotHotReload);
        Assert.True(host.Process.WaitForExit(TimeSpan.FromSeconds(30)), "the host should exit after an unreadable line");
    }

    // A test project's tests may not have loaded every assembly they run with when an edit comes (here, a library
    // the tests load by name, only once a gate file exists). The delta names its baseline, which the host loads
    // before it applies the delta: the file the tests would load later is the edited build, which no delta fits.
    [Fact]
    public void ADeltaForAnAssemblyTheTestsHaveNotLoadedLoadsItsBaselineFirst()
    {
        using var subject = new Subject("xunit3", answerInLibrary: true);
        using var host = new Host(hotReload: true);
        var pid = host.Process.Id;

        var first = host.Send(subject.Run);
        Assert.Equal((1, 0, 1), (first.Total, first.Passed, first.Failed));
        Assert.Null(first.CannotHotReload);

        subject.OpenGate();
        var second = host.Send(new TestHostUpdate([subject.Fix()]));
        Assert.Null(second.Unsupported);
        Assert.Equal((1, 1, 0), (second.Total, second.Passed, second.Failed));
        Assert.Null(second.CannotHotReload);
        Assert.False(host.Process.HasExited);
        Assert.Equal(pid, host.Process.Id);
    }

    /// <summary>A test host process, as the daemon starts one, with or without the runtime's switch for updates.</summary>
    sealed class Host : IDisposable
    {
        public Process Process { get; }

        public Host(bool hotReload)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "blaze-cs-testhost.dll"));
            if (hotReload)
                start.Environment[TestHostProtocol.ModifiableAssemblies] = "debug";
            else
                start.Environment.Remove(TestHostProtocol.ModifiableAssemblies);
            Process = Process.Start(start)!;
        }

        /// <summary>Sends a message and returns the result it gets, with the test output in the test's log.</summary>
        public TestHostResult Send(TestHostMessage message)
        {
            Assert.True(TestRunner.Send(Process, message), "the host should be up");
            var output = new List<string>();
            var result = TestRunner.ReadResult(Process, output);
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", output.Append($"=> {result}")));
            return result ?? throw new InvalidOperationException("the host exited without a result:\n" + string.Join("\n", output));
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit();
            }
            Process.Dispose();
        }
    }

    /// <summary>
    /// A test assembly in a framework, written over a copy of its kit's own: a test that passes, and one that
    /// fails until <see cref="Fix"/>'s delta makes Answer.Value return 42. With answerInLibrary (xUnit v3 only),
    /// Answer is in AnswerLib, a library the test assembly doesn't reference: its one test, which fails, loads it
    /// by name once <see cref="OpenGate"/> was called, and reads 0 until then. AnswerLib's build is in a
    /// directory of its own (<see cref="Baseline"/>), where no resolver finds it, as a snapshot of the daemon's.
    /// </summary>
    sealed class Subject : IDisposable
    {
        readonly string _framework, _extraTest;
        readonly DirectoryInfo _directory;
        readonly CSharpCompilation _compilation;
        readonly EmitBaseline _baseline;
        readonly Guid _module;
        readonly Func<int, string> _source;

        /// <param name="extraTest">A third test in the test class, in the framework's attributes.</param>
        public Subject(string framework, string extraTest = "", bool answerInLibrary = false)
        {
            (_framework, _extraTest) = (framework, extraTest);
            var kit = framework switch { "xunit3" => "XunitV3Kit", "nunit" => "NUnitKit", "mstest" => "MSTestKit", _ => throw new ArgumentOutOfRangeException(nameof(framework)) };
            var kitDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "kits", kit));
            _directory = Directory.CreateTempSubdirectory("blaze-cs-hot-reload-");
            Copy(kitDirectory, _directory.FullName);
            AssemblyPath = Path.Combine(_directory.FullName, kit + ".dll");

            var runtime = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetFileName(path) is var name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
                .ToList();
            var frameworkNames = runtime.Select(Path.GetFileName).ToHashSet();
            var packages = Directory.EnumerateFiles(kitDirectory, "*.dll")
                .Where(path => Path.GetFileName(path) is var name && name != kit + ".dll" && !frameworkNames.Contains(name));
            MetadataReference[] references = [.. runtime.Concat(packages).Select(path => MetadataReference.CreateFromFile(path))];
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug);
            if (!answerInLibrary)
            {
                _source = Source;
                _compilation = CSharpCompilation.Create(kit, [Parse(Source(41))], references, options);
                (_module, _baseline) = Emit(_compilation, AssemblyPath);
                return;
            }
            Assert.Equal("xunit3", framework);
            _source = AnswerSource;
            _compilation = CSharpCompilation.Create("AnswerLib", [Parse(AnswerSource(41))], references, options);
            Baseline = Path.Combine(_directory.FullName, "baseline", "AnswerLib.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(Baseline)!);
            (_module, _baseline) = Emit(_compilation, Baseline);
            Emit(CSharpCompilation.Create(kit, [Parse(GatedTestsSource)], references, options), AssemblyPath);
        }

        /// <summary>Writes a compilation's assembly and PDB, and returns its module version id and a baseline for deltas.</summary>
        static (Guid Module, EmitBaseline Baseline) Emit(CSharpCompilation compilation, string path)
        {
            using var image = new MemoryStream();
            using var pdb = new MemoryStream();
            var emitted = compilation.Emit(image, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            File.WriteAllBytes(path, image.ToArray());
            File.WriteAllBytes(Path.ChangeExtension(path, ".pdb"), pdb.ToArray());

            var reader = new PEReader(ImmutableArray.Create(image.ToArray()));
            var metadata = reader.GetMetadataReader();
            return (metadata.GetGuid(metadata.GetModuleDefinition().Mvid), EmitBaseline.CreateInitialBaseline(compilation, ModuleMetadata.CreateFromImage(image.ToArray()),
                debugInformationProvider: _ => default, // Answer.Value has no locals or lambdas to map
                localSignatureProvider: method => metadata.GetMethodDefinition(method).RelativeVirtualAddress is var rva and > 0
                    ? reader.GetMethodBody(rva).LocalSignature : default,
                hasPortableDebugInformation: true));
        }

        public string AssemblyPath { get; }

        /// <summary>With answerInLibrary, the build of AnswerLib the delta is made against; otherwise null.</summary>
        public string? Baseline { get; }

        /// <summary>Lets the test load AnswerLib from now on.</summary>
        public void OpenGate() => File.WriteAllText(Path.Combine(_directory.FullName, "gate"), "");


        public TestHostRequest Run => new(_framework, AssemblyPath, null);

        /// <summary>The delta that makes Answer.Value return 42, as the daemon would send it.</summary>
        public TestHostDelta Fix()
        {
            var fixedCompilation = _compilation.ReplaceSyntaxTree(_compilation.SyntaxTrees.Single(), Parse(_source(42)));
            ISymbol Value(Compilation compilation) => compilation.GetTypeByMetadataName("Answer")!.GetMembers("Value").Single();
            using var metadata = new MemoryStream();
            using var il = new MemoryStream();
            using var pdb = new MemoryStream();
            var result = fixedCompilation.EmitDifference(_baseline,
                [new SemanticEdit(SemanticEditKind.Update, Value(_compilation), Value(fixedCompilation), null, null, default)],
                _ => false, metadata, il, pdb, EmitDifferenceOptions.Default, TestContext.Current.CancellationToken);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return new TestHostDelta(_module, metadata.ToArray(), il.ToArray(), pdb.ToArray(),
                [.. result.ChangedTypes.Select(type => MetadataTokens.GetToken(type))], Baseline);
        }

        static string AnswerSource(int answer) => $$"""
            public static class Answer
            {
                public static int Value() => {{answer}};
            }
            """;

        const string GatedTestsSource = """
            using System;
            using System.IO;
            using Xunit;

            public class AnswerTests
            {
                static int Answer() => File.Exists(Path.Combine(Path.GetDirectoryName(typeof(AnswerTests).Assembly.Location)!, "gate"))
                    ? (int)Type.GetType("Answer, AnswerLib", throwOnError: true)!.GetMethod("Value")!.Invoke(null, null)!
                    : 0;

                [Fact] public void IsFortyTwo() => Assert.Equal(42, Answer());
            }
            """;

        static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(source, path: "Answer.cs", encoding: System.Text.Encoding.UTF8);

        string Source(int answer) => $$"""
            using System;
            using System.Threading;
            {{_framework switch
            {
                "xunit3" => "using Xunit;",
                "nunit" => "using NUnit.Framework;",
                _ => "using Microsoft.VisualStudio.TestTools.UnitTesting;",
            }}}

            public static class Answer
            {
                public static int Value() => {{answer}};
            }

            {{(_framework == "mstest" ? "[TestClass]" : "")}}
            public class AnswerTests
            {
                {{_framework switch
                {
                    "xunit3" => "[Fact] public void IsPositive() => Assert.True(Answer.Value() > 0);\n    [Fact] public void IsFortyTwo() => Assert.Equal(42, Answer.Value());",
                    "nunit" => "[Test] public void IsPositive() => Assert.That(Answer.Value(), Is.Positive);\n    [Test] public void IsFortyTwo() => Assert.That(Answer.Value(), Is.EqualTo(42));",
                    _ => "[TestMethod] public void IsPositive() => Assert.IsTrue(Answer.Value() > 0);\n    [TestMethod] public void IsFortyTwo() => Assert.AreEqual(42, Answer.Value());",
                }}}
                {{_extraTest}}
            }
            """;

        static void Copy(string from, string to)
        {
            foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }

        public void Dispose()
        {
            try { _directory.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
