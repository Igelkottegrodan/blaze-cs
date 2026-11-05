using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;

namespace BlazeCs.TestHost;

/// <summary>
/// A long-lived process that runs xUnit v3 test assemblies in-process, one per line read from stdin.
/// Each run gets a fresh collectible load context, so statics start clean and the next build's DLLs
/// load without restarting the runtime. Test output goes to stdout; the result is the last line.
/// </summary>
static class Program
{
    const string ResultPrefix = "\u0001result:";

    static void Main()
    {
        var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        while (Console.ReadLine() is { } assemblyPath)
        {
            var (result, context) = Run(assemblyPath);
            var unloaded = Unload(context);
            Console.Out.Flush();
            protocol.WriteLine(ResultPrefix + JsonSerializer.Serialize(result with { Unloaded = unloaded }));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (Result, WeakReference) Run(string assemblyPath)
    {
        var context = new TestContext(assemblyPath);
        var reportPath = Path.Combine(Path.GetTempPath(), $"blaze-cs-{Environment.ProcessId}.xml");
        try
        {
            // Tests find files relative to their output directory, as under `dotnet test`: through the
            // current directory, or AppContext.BaseDirectory (WebApplicationFactory looks for deps.json there).
            var directory = Path.GetDirectoryName(assemblyPath)!;
            Environment.CurrentDirectory = directory;
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", directory + Path.DirectorySeparatorChar);
            using (context.EnterContextualReflection())
            {
                var assembly = context.LoadFromAssemblyPath(assemblyPath);
                // The assembly's own Main would run whatever Assembly.GetEntryAssembly() is: this host.
                // xUnit's in-process runner also takes the test assembly explicitly.
                var runnerType = context.LoadFromAssemblyName(new AssemblyName("xunit.v3.runner.inproc.console"))
                    .GetType("Xunit.Runner.InProc.SystemConsole.ConsoleRunner", throwOnError: true)!;
                string[] arguments = ["-noLogo", "-noColor", "-xml", reportPath];
                var runner = (IDisposable)Activator.CreateInstance(runnerType, [arguments, assembly])!;
                using (runner)
                {
                    // Not Console.In: this process's stdin is the daemon's channel.
                    var run = (Task<int>)runnerType.GetMethod("EntryPoint")!.Invoke(runner, [TextReader.Null, Console.Out])!;
                    run.GetAwaiter().GetResult();
                }
            }
            return (Report(reportPath), new WeakReference(context));
        }
        catch (Exception e)
        {
            var error = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            return (new Result(0, 0, 0, 0, false, error.ToString()), new WeakReference(context));
        }
        finally
        {
            context.Unload();
            File.Delete(reportPath);
        }
    }

    /// <summary>Totals from xUnit's XML report: assemblies/assembly[@total,@passed,@failed,@skipped].</summary>
    static Result Report(string path)
    {
        var assembly = XDocument.Load(path).Root!.Element("assembly")
            ?? throw new InvalidOperationException("xUnit wrote no results");
        int Count(string name) => int.Parse(assembly.Attribute(name)?.Value ?? "0");
        return new Result(Count("total"), Count("passed"), Count("failed") + Count("errors"), Count("skipped"), false, null);
    }

    static bool Unload(WeakReference context)
    {
        for (var i = 0; i < 10 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return !context.IsAlive;
    }

    sealed record Result(int Total, int Passed, int Failed, int Skipped, bool Unloaded, string? Error);

    /// <summary>
    /// Resolves the test project's own dependencies from its deps.json; the shared frameworks
    /// (.NET, ASP.NET Core) come from this process.
    /// </summary>
    sealed class TestContext(string assemblyPath) : AssemblyLoadContext($"tests:{assemblyPath}", isCollectible: true)
    {
        readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name) =>
            _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;

        protected override IntPtr LoadUnmanagedDll(string name) =>
            _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
