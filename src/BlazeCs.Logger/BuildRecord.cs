namespace BlazeCs;

// The file blaze-cs-logger writes and the daemon reads (BuildLog); both compile this one definition.

/// <summary>What one `dotnet build` did that the daemon needs.</summary>
/// <param name="Compiled">Every C# project (or target framework of one) CoreCompile ran for, whether csc ran or
/// the target was up to date.</param>
/// <param name="Copies">Every file copy MSBuild made or confirmed up to date, source to destination.</param>
sealed record BuildRecord(List<CompileRecord> Compiles, List<ProjectRecord> Compiled, List<CopyRecord> Copies);

/// <summary>A project, or one target framework of it.</summary>
/// <param name="TargetFramework">The TargetFramework global property: set for each framework of a multi-targeting project.</param>
sealed record ProjectRecord(string ProjectFile, string? TargetFramework);

/// <summary>One csc run: a project, or one target framework of it.</summary>
/// <param name="TargetFramework">The TargetFramework global property: set for each framework of a multi-targeting project.</param>
/// <param name="CommandLine">The Csc task's command line, compiler executable first.</param>
/// <param name="AfterCompile">Tasks in targets after CoreCompile that were handed the assembly csc wrote, in order.</param>
sealed record CompileRecord(string ProjectFile, string? TargetFramework, string CommandLine, List<AfterCompileTask> AfterCompile);

/// <param name="TargetFile">The file that defines the target, which tells SDK targets from others.</param>
sealed record AfterCompileTask(string Target, string? TargetFile, string Task);

sealed record CopyRecord(string From, string To);
