using System.Reflection;
using Microsoft.CodeAnalysis;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// The daemon's shim over the SDK's hot reload engine (HotReload.cs): it says why the engine is unavailable
/// when an SDK lacks its assemblies, or has them from another Roslyn build than the compiler's, so `blaze-cs
/// test` runs as without hot reload.
/// </summary>
public sealed class HotReloadEngineTests : IDisposable
{
    readonly DirectoryInfo _sdk = Directory.CreateTempSubdirectory("blaze-cs-sdk-");

    [Fact]
    public void AnSdkWithoutDotnetWatchHasNoEngine()
    {
        Assert.Null(HotReloadEngine.Load(_sdk.FullName, out var unavailable));
        Assert.Equal($"the SDK has no dotnet-watch in {Path.Combine(_sdk.FullName, "DotnetTools")}", unavailable);
    }

    // As in SDK 10.0.100: a dotnet-watch, but without the engine's ExternalAccess.HotReload.
    [Fact]
    public void ADotnetWatchWithoutTheHotReloadServiceHasNoEngine()
    {
        var watch = Directory.CreateDirectory(Path.Combine(_sdk.FullName, "DotnetTools", "dotnet-watch")).FullName;
        Directory.CreateDirectory(Path.Combine(watch, "10.0.100", "tools", "net10.0", "any"));

        Assert.Null(HotReloadEngine.Load(_sdk.FullName, out var unavailable));
        Assert.Equal($"the SDK's dotnet-watch has no Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll in {watch}", unavailable);
    }

    [Fact]
    public void ADotnetWatchWithoutTheEngineAssembliesHasNoEngine()
    {
        var watch = Directory.CreateDirectory(Path.Combine(_sdk.FullName, "DotnetTools", "dotnet-watch", "10.0.100", "tools", "net10.0", "any")).FullName;
        File.WriteAllBytes(Path.Combine(watch, "Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll"), []);

        Assert.Null(HotReloadEngine.Load(_sdk.FullName, out var unavailable));
        Assert.Equal($"the SDK has no Microsoft.CodeAnalysis.Workspaces.dll in {watch}", unavailable);
    }

    [Fact]
    public void AnEngineAssemblyOfTheCompilersBuildMatches()
    {
        var compiler = typeof(Compilation).Assembly;
        Assert.Null(HotReloadEngine.Unmatched(compiler, compiler.GetName()));
    }

    [Fact]
    public void AnEngineAssemblyOfAnotherRoslynBuildDoesNot()
    {
        var compiler = new AssemblyName("Microsoft.CodeAnalysis") { Version = new Version(1, 2, 3, 4) };
        var engine = typeof(Compilation).Assembly;
        Assert.Equal($"Microsoft.CodeAnalysis {engine.GetName().Version} is not of the compiler's Roslyn build (Microsoft.CodeAnalysis 1.2.3.4)",
            HotReloadEngine.Unmatched(engine, compiler));
    }

    public void Dispose() => _sdk.Delete(recursive: true);
}
