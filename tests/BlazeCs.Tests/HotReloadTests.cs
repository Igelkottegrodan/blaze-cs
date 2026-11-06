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
    /// fails until <see cref="Fix"/>'s delta makes Answer.Value return 42.
    /// </summary>
    sealed class Subject : IDisposable
    {
        readonly string _framework;
        readonly DirectoryInfo _directory;
        readonly CSharpCompilation _compilation;
        readonly EmitBaseline _baseline;
        readonly Guid _module;

        public Subject(string framework)
        {
            _framework = framework;
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
            _compilation = CSharpCompilation.Create(kit, [Parse(Source(41))],
                [.. runtime.Concat(packages).Select(path => MetadataReference.CreateFromFile(path))],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

            using var image = new MemoryStream();
            using var pdb = new MemoryStream();
            var emitted = _compilation.Emit(image, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            File.WriteAllBytes(AssemblyPath, image.ToArray());
            File.WriteAllBytes(Path.ChangeExtension(AssemblyPath, ".pdb"), pdb.ToArray());

            var reader = new PEReader(ImmutableArray.Create(image.ToArray()));
            var metadata = reader.GetMetadataReader();
            _module = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            _baseline = EmitBaseline.CreateInitialBaseline(_compilation, ModuleMetadata.CreateFromImage(image.ToArray()),
                debugInformationProvider: _ => default, // Answer.Value has no locals or lambdas to map
                localSignatureProvider: method => metadata.GetMethodDefinition(method).RelativeVirtualAddress is var rva and > 0
                    ? reader.GetMethodBody(rva).LocalSignature : default,
                hasPortableDebugInformation: true);
        }

        public string AssemblyPath { get; }

        public TestHostRequest Run => new(_framework, AssemblyPath, null);

        /// <summary>The delta that makes Answer.Value return 42, as the daemon would send it.</summary>
        public TestHostDelta Fix()
        {
            var fixedCompilation = _compilation.ReplaceSyntaxTree(_compilation.SyntaxTrees.Single(), Parse(Source(42)));
            ISymbol Value(Compilation compilation) => compilation.GetTypeByMetadataName("Answer")!.GetMembers("Value").Single();
            using var metadata = new MemoryStream();
            using var il = new MemoryStream();
            using var pdb = new MemoryStream();
            var result = fixedCompilation.EmitDifference(_baseline,
                [new SemanticEdit(SemanticEditKind.Update, Value(_compilation), Value(fixedCompilation), null, null, default)],
                _ => false, metadata, il, pdb, EmitDifferenceOptions.Default, TestContext.Current.CancellationToken);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return new TestHostDelta(_module, metadata.ToArray(), il.ToArray(), pdb.ToArray(),
                [.. result.ChangedTypes.Select(type => MetadataTokens.GetToken(type))]);
        }

        static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(source, path: "Answer.cs", encoding: System.Text.Encoding.UTF8);

        string Source(int answer) => $$"""
            using System;
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
