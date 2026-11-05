#!/usr/bin/env dotnet
// Makes one benchmark edit to jellyfin's MediaBrowser.Controller; each call makes a new change.
//
// usage: dotnet run edit.cs -- body|signature|file|test [jellyfin-dir]
//   body       change a string literal inside BaseItem's image lookup (no public API change)
//   signature  rename AddGenre's parameter, back and forth (public API change: every dependent recompiles)
//   file       add, then on the next call remove, a small internal class in a new file
//   test       change a comment in a Jellyfin.Controller.Tests test file (only that test project rebuilds)
using System.Text;
using System.Text.RegularExpressions;

var kind = args.Length > 0 ? args[0] : "";
var root = args.Length > 1 ? args[1] : "bench/jellyfin";
var counterFile = Path.Combine(root, ".blaze-bench-counter");
var n = File.Exists(counterFile) ? int.Parse(Read(counterFile)) + 1 : 1;
Write(counterFile, n.ToString());
var baseItem = Path.Combine(root, "MediaBrowser.Controller/Entities/BaseItem.cs");

switch (kind)
{
    case "body":
    {
        var pattern = new Regex("\"No image index found for image path[^\"]*\"");
        var text = Read(baseItem);
        var count = pattern.Matches(text).Count;
        if (count != 1)
            throw new InvalidOperationException($"expected one image lookup message, found {count}");
        Write(baseItem, pattern.Replace(text, $"\"No image index found for image path (edit {n})\""));
        break;
    }
    case "signature":
    {
        var text = Read(baseItem);
        var start = text.IndexOf("        /// Adds a genre to the item.", StringComparison.Ordinal);
        var end = text.IndexOf("        /// Marks the played.", start, StringComparison.Ordinal);
        var block = text[start..end];
        var (old, replacement) = block.Contains("<param name=\"name\">") ? ("name", "genreName") : ("genreName", "name");
        block = Regex.Replace(block, $@"\b{old}\b(?!=)", replacement); // not the name= of <param name="...">
        Write(baseItem, text[..start] + block + text[end..]);
        break;
    }
    case "file":
    {
        var probe = Path.Combine(root, "MediaBrowser.Controller/Entities/BlazeProbe.cs");
        if (File.Exists(probe))
            File.Delete(probe);
        else
            Write(probe,
                "namespace MediaBrowser.Controller.Entities\n{\n"
                + "    /// <summary>\n    /// Added by the blaze-cs benchmark.\n    /// </summary>\n"
                + $"    internal static class BlazeProbe\n    {{\n        internal const int Edit = {n};\n    }}\n}}\n");
        break;
    }
    case "test":
    {
        var testFile = Path.Combine(root, "tests/Jellyfin.Controller.Tests/BaseItemManagerTests.cs");
        var text = Regex.Replace(Read(testFile), @"\n\n// blaze-cs benchmark edit \d+\n$", "\n");
        Write(testFile, text + $"\n// blaze-cs benchmark edit {n}\n"); // StyleCop wants the blank line
        break;
    }
    default:
        Console.Error.WriteLine("usage: dotnet run edit.cs -- body|signature|file|test [jellyfin-dir]");
        return 1;
}
return 0;

// Byte-faithful UTF-8: a byte order mark stays where it was, unlike File.ReadAllText/WriteAllText.
static string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

static void Write(string path, string text) => File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
