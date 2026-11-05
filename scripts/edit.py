"""Makes one benchmark edit to jellyfin's MediaBrowser.Controller; each call makes a new change.

usage: edit.py body|signature|file [jellyfin-dir]
  body       change a string literal inside BaseItem's image lookup (no public API change)
  signature  rename AddGenre's parameter, back and forth (public API change: every dependent recompiles)
  file       add, then on the next call remove, a small internal class in a new file
  test       change a comment in a Jellyfin.Controller.Tests test file (only that test project rebuilds)
"""
import pathlib, re, sys

kind = sys.argv[1]
root = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else "bench/jellyfin")
counter_file = root / ".blaze-bench-counter"
n = int(counter_file.read_text()) + 1 if counter_file.exists() else 1
counter_file.write_text(str(n))
base_item = root / "MediaBrowser.Controller/Entities/BaseItem.cs"

if kind == "body":
    text = base_item.read_text()
    text, count = re.subn(r'"No image index found for image path[^"]*"', f'"No image index found for image path (edit {n})"', text)
    assert count == 1, count
    base_item.write_text(text)
elif kind == "signature":
    text = base_item.read_text()
    start = text.index("        /// Adds a genre to the item.")
    end = text.index("        /// Marks the played.", start)
    old, new = ("name", "genreName") if '<param name="name">' in text[start:end] else ("genreName", "name")
    block = re.sub(rf"\b{old}\b(?!=)", new, text[start:end])  # not the name= of <param name="...">
    base_item.write_text(text[:start] + block + text[end:])
elif kind == "file":
    probe = root / "MediaBrowser.Controller/Entities/BlazeProbe.cs"
    if probe.exists():
        probe.unlink()
    else:
        probe.write_text(
            "namespace MediaBrowser.Controller.Entities\n{\n"
            "    /// <summary>\n    /// Added by the blaze-cs benchmark.\n    /// </summary>\n"
            f"    internal static class BlazeProbe\n    {{\n        internal const int Edit = {n};\n    }}\n}}\n")
elif kind == "test":
    test_file = root / "tests/Jellyfin.Controller.Tests/BaseItemManagerTests.cs"
    text = re.sub(r"\n\n// blaze-cs benchmark edit \d+\n$", "\n", test_file.read_text())
    test_file.write_text(text + f"\n// blaze-cs benchmark edit {n}\n")  # StyleCop wants the blank line
else:
    sys.exit(__doc__)
