using System.Text.Json.Nodes;

namespace BlazeCs;

/// <summary>A NuGet restore spec (obj/&lt;project&gt;.nuget.dgspec.json): what restore read, for NuGet's own no-op check.</summary>
static class RestoreSpec
{
    /// <summary>
    /// Whether two restore specs of one project say the same, over the projects restore reaches from it. The
    /// one GenerateRestoreGraphFile writes also lists projects only a ReferenceOutputAssembly="false"
    /// reference leads to (an analyzer project, say), which the one restore wrote leaves out: restore follows
    /// each framework's projectReferences, and those aren't among them.
    /// </summary>
    public static bool Same(string restored, string regenerated) =>
        JsonNode.DeepEquals(Closure(JsonNode.Parse(restored)), Closure(JsonNode.Parse(regenerated)));

    /// <summary>The spec without the projects its restore entries don't reach through projectReferences.</summary>
    static JsonNode? Closure(JsonNode? spec)
    {
        if (spec?["projects"] is not JsonObject projects)
            return spec;
        var reached = new HashSet<string>(PathComparer.Instance);
        var pending = new Stack<string>((spec["restore"] as JsonObject ?? []).Select(entry => entry.Key));
        while (pending.TryPop(out var path))
        {
            if (!reached.Add(path) || projects.FirstOrDefault(p => PathComparer.Instance.Equals(p.Key, path)).Value is not { } project)
                continue;
            foreach (var framework in project["restore"]?["frameworks"] as JsonObject ?? [])
            {
                foreach (var reference in framework.Value?["projectReferences"] as JsonObject ?? [])
                    pending.Push(reference.Value?["projectPath"]?.GetValue<string>() ?? reference.Key);
            }
        }
        foreach (var path in projects.Select(p => p.Key).Where(path => !reached.Contains(path)).ToList())
            projects.Remove(path);
        return spec;
    }
}
