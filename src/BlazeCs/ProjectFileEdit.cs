using System.Xml;
using System.Xml.Linq;

namespace BlazeCs;

/// <summary>
/// Which project file edits the in-process pass can take. The pass reads csc's arguments and nothing else of
/// the build, so only an edit that changes what csc gets and nothing more can skip dotnet build: one that
/// adds a file the build copies, changes the runtimeconfig or deps.json (InvariantGlobalization, RollForward),
/// the apphost (OutputType), a package, a reference or any target, needs a real build. Decided on the text
/// of the project file before and after the edit, without MSBuild.
/// </summary>
static class ProjectFileEdit
{
    /// <summary>Properties that only become csc arguments, or files the compile pass writes for csc.</summary>
    static readonly HashSet<string> CompilerProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "DefineConstants", "Nullable", "LangVersion", "WarningLevel", "NoWarn", "TreatWarningsAsErrors", "WarningsAsErrors",
        "WarningsNotAsErrors", "AllowUnsafeBlocks", "CheckForOverflowUnderflow", "Features", "AnalysisLevel", "AnalysisMode",
        "EnforceCodeStyleInBuild", "CodeAnalysisTreatWarningsAsErrors", "Optimize", "Deterministic", "ImplicitUsings",
    };

    /// <summary>Items that only become csc arguments, unless they carry <see cref="CopyMetadata"/>.</summary>
    static readonly HashSet<string> CompilerItems = new(StringComparer.OrdinalIgnoreCase) { "Compile", "AdditionalFiles", "Using" };

    static readonly HashSet<string> CopyMetadata = new(StringComparer.OrdinalIgnoreCase) { "CopyToOutputDirectory", "CopyToPublishDirectory" };

    /// <summary>
    /// Null when the edit from <paramref name="before"/> to <paramref name="after"/> changes, besides comments
    /// and whitespace, only compiler properties and compiler items in the project's top-level groups;
    /// otherwise what else it changes, such as <c>&lt;None Update="data.txt"&gt;</c>.
    /// </summary>
    /// <param name="changed">
    /// How the compiler settings the edit changes are read, such as <c>$(DefineConstants</c> or <c>@(Compile</c>:
    /// an import that reads one may do more with it than csc does, which only MSBuild can tell.
    /// </param>
    public static string? MoreThanCompilerInput(string before, string after, out string[] changed)
    {
        changed = [];
        XElement old, now;
        try
        {
            old = XElement.Parse(before);
            now = XElement.Parse(after);
        }
        catch (XmlException e)
        {
            return $"the file doesn't parse: {e.Message}";
        }
        var oldCompiler = StripCompilerInput(old);
        var newCompiler = StripCompilerInput(now);
        if (Difference(old, now) is { } difference)
            return difference;
        // The rest of the file is the same; a compiler setting it reads, in a condition or another property,
        // is more than a compiler setting.
        var rest = now.ToString(SaveOptions.DisableFormatting);
        changed = [.. oldCompiler.Keys.Union(newCompiler.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(name => oldCompiler.GetValueOrDefault(name) != newCompiler.GetValueOrDefault(name))
            .Select(name => (name[0] == '$' ? "$(" : "@(") + name[1..])
            .Order(StringComparer.Ordinal)];
        if (changed.FirstOrDefault(reference => rest.Contains(reference, StringComparison.OrdinalIgnoreCase)) is { } read)
            return $"{read}), which the project file reads elsewhere";
        return null;
    }

    /// <summary>
    /// The first of <paramref name="references"/> (from <see cref="MoreThanCompilerInput"/>) that an imported
    /// file reads for more than csc's input: outside its comments and its top-level compiler properties and
    /// items, so a Directory.Build.props that appends to DefineConstants or NoWarn reads nothing. Null if none.
    /// </summary>
    public static string? ReadBeyondCompilerInput(string import, IReadOnlyList<string> references)
    {
        if (references.Count == 0)
            return null;
        string rest;
        try
        {
            var project = XElement.Parse(import);
            StripCompilerInput(project);
            rest = project.ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException)
        {
            rest = import;
        }
        return references.FirstOrDefault(reference => rest.Contains(reference, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Removes comments, and compiler properties and items from the project's top-level groups (with the
    /// groups this leaves empty). Returns what it removed, as XML by "$property" or "@item type".
    /// </summary>
    static Dictionary<string, string> StripCompilerInput(XElement project)
    {
        foreach (var comment in project.DescendantNodes().OfType<XComment>().ToList())
            comment.Remove();
        var removed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in project.Elements().Where(e => e.Name.LocalName is "PropertyGroup" or "ItemGroup").ToList())
        {
            var properties = group.Name.LocalName == "PropertyGroup";
            foreach (var element in group.Elements().ToList())
            {
                var name = element.Name.LocalName;
                if (properties ? !CompilerProperties.Contains(name)
                        : !CompilerItems.Contains(name) || element.Attributes().Any(a => CopyMetadata.Contains(a.Name.LocalName))
                            || element.Elements().Any(m => CopyMetadata.Contains(m.Name.LocalName)))
                    continue;
                var key = (properties ? "$" : "@") + name;
                removed[key] = removed.GetValueOrDefault(key, "") + group.Attribute("Condition")?.Value + "\n"
                    + element.ToString(SaveOptions.DisableFormatting) + "\n";
                element.Remove();
            }
            if (!group.HasElements)
                group.Remove();
        }
        return removed;
    }

    /// <summary>Null when the two are the same XML; otherwise the innermost element that differs, described.</summary>
    static string? Difference(XElement old, XElement now)
    {
        if (XNode.DeepEquals(old, now))
            return null;
        if (old.Name != now.Name || !old.Attributes().Select(a => a.ToString()).SequenceEqual(now.Attributes().Select(a => a.ToString())))
            return Describe(now);
        var (oldNodes, newNodes) = (old.Nodes().ToList(), now.Nodes().ToList());
        for (var i = 0; i < Math.Min(oldNodes.Count, newNodes.Count); i++)
        {
            if (XNode.DeepEquals(oldNodes[i], newNodes[i]))
                continue;
            return oldNodes[i] is XElement a && newNodes[i] is XElement b ? Difference(a, b) : Describe(now);
        }
        var extra = (oldNodes.Count > newNodes.Count ? oldNodes : newNodes)[Math.Min(oldNodes.Count, newNodes.Count)];
        return Describe(extra as XElement ?? now);
    }

    /// <summary>
    /// An element as its start tag, with the attribute that names it: &lt;PackageReference Include="xunit"&gt;. A
    /// group stands for its first element.
    /// </summary>
    static string Describe(XElement element)
    {
        while (element.Name.LocalName is "PropertyGroup" or "ItemGroup" or "ItemDefinitionGroup" && element.Elements().FirstOrDefault() is { } first)
            element = first;
        return new[] { "Include", "Update", "Remove", "Name", "Project", "Sdk" }.Select(name => element.Attribute(name)).OfType<XAttribute>().FirstOrDefault() is { } attribute
            ? $"<{element.Name.LocalName} {attribute.Name}=\"{attribute.Value}\">"
            : $"<{element.Name.LocalName}>";
    }
}
