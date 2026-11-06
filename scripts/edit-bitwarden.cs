#!/usr/bin/env dotnet
// Makes one verification edit to bitwarden/server (src/Core, which nearly every project references, and
// src/Admin, an MVC app with .cshtml views); each call makes a new change. Used by verify.sh bitwarden.
//
// usage: dotnet run edit-bitwarden.cs -- body|signature|file|test|test-v3|view|view-const [bitwarden-dir]
//   body       change a string literal inside CoreHelpers.Base64UrlDecode (no public API change)
//   signature  rename GetEmailDomain's parameter, back and forth (public API change)
//   file       add, then on the next call remove, a small internal class in a new file in Core
//   test       change a comment in a Core.Test file (xUnit v2: only that test project rebuilds)
//   test-v3    change a comment in a Setup.Test file (xUnit v3)
//   view       change the title text of Admin's dashboard view (Views/Home/Index.cshtml)
//   view-const first call: add a public const to CoreHelpers that only that view reads; later calls
//              change its value (Admin must recompile, though none of its .cs files names it)
using System.Text;
using System.Text.RegularExpressions;

var kind = args.Length > 0 ? args[0] : "";
var root = args.Length > 1 ? args[1] : "bench/bitwarden";
var counterFile = Path.Combine(root, ".blaze-bench-counter");
var n = File.Exists(counterFile) ? int.Parse(Read(counterFile)) + 1 : 1;
Write(counterFile, n.ToString());
var coreHelpers = Path.Combine(root, "src/Core/Utilities/CoreHelpers.cs");

switch (kind)
{
    case "body":
        ReplaceOnce(coreHelpers, "\"Illegal base64url string[^\"]*\"", $"\"Illegal base64url string (edit {n})!\"");
        break;
    case "signature":
    {
        var text = Read(coreHelpers);
        var start = text.IndexOf("    public static string? GetEmailDomain(", StringComparison.Ordinal);
        var end = text.IndexOf("    public static string ReplaceWhiteSpace(", start, StringComparison.Ordinal);
        var block = text[start..end];
        var (old, replacement) = block.Contains("(string email)") ? ("email", "emailAddress") : ("emailAddress", "email");
        block = Regex.Replace(block, $@"\b{old}\b", replacement);
        Write(coreHelpers, text[..start] + block + text[end..]);
        break;
    }
    case "file":
    {
        var probe = Path.Combine(root, "src/Core/Utilities/BlazeProbe.cs");
        if (File.Exists(probe))
            File.Delete(probe);
        else
            Write(probe, "namespace Bit.Core.Utilities;\n\n"
                + $"internal static class BlazeProbe\n{{\n    internal const int Edit = {n};\n}}\n");
        break;
    }
    case "test":
        AppendComment(Path.Combine(root, "test/Core.Test/Utilities/CoreHelpersTests.cs"));
        break;
    case "test-v3":
        AppendComment(Path.Combine(root, "test/Setup.Test/ContextTests.cs"));
        break;
    case "view":
        ReplaceOnce(Path.Combine(root, "src/Admin/Views/Home/Index.cshtml"),
            "ViewData\\[\"Title\"\\] = \"Dashboard[^\"]*\"", $"ViewData[\"Title\"] = \"Dashboard (edit {n})\"");
        break;
    case "view-const":
    {
        var text = Read(coreHelpers);
        if (text.Contains("BlazeViewProbe", StringComparison.Ordinal))
        {
            ReplaceOnce(coreHelpers, "BlazeViewProbe = \"[^\"]*\"", $"BlazeViewProbe = \"edit {n}\"");
            break;
        }
        ReplaceOnce(coreHelpers, "public static class CoreHelpers\n\\{\n",
            $"public static class CoreHelpers\n{{\n    public const string BlazeViewProbe = \"edit {n}\";\n");
        var view = Path.Combine(root, "src/Admin/Views/Home/Index.cshtml");
        Write(view, Read(view) + "<p>@Bit.Core.Utilities.CoreHelpers.BlazeViewProbe</p>\n");
        break;
    }
    default:
        Console.Error.WriteLine("usage: dotnet run edit-bitwarden.cs -- body|signature|file|test|test-v3|view|view-const [bitwarden-dir]");
        return 1;
}
return 0;

void AppendComment(string path)
{
    var text = Regex.Replace(Read(path), @"// blaze-cs edit \d+\n$", "");
    Write(path, text + $"// blaze-cs edit {n}\n");
}

static void ReplaceOnce(string path, string pattern, string replacement)
{
    var regex = new Regex(pattern);
    var text = Read(path);
    var count = regex.Matches(text).Count;
    if (count != 1)
        throw new InvalidOperationException($"expected one match of {pattern} in {path}, found {count}");
    Write(path, regex.Replace(text, replacement));
}

// Byte-faithful UTF-8: a byte order mark stays where it was, unlike File.ReadAllText/WriteAllText.
static string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

static void Write(string path, string text) => File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
