#!/usr/bin/env dotnet
// The README's animated header: "blaze-cs" in chrome pixel letters over a striped sunset and a neon grid
// that scrolls toward the viewer, 80s Outrun style.
//
//     dotnet run docs/brand/outrun/gen.cs                   writes header-{dark,light}.png next to this file
//     dotnet run docs/brand/outrun/gen.cs -- --preview DIR  instead, DIR/preview-{dark,light}.png: four frames
//                                                          on GitHub's backgrounds, to look at
//
// Everything is drawn on a coarse grid of cells and scaled up by whole pixels, so it stays crisp. Each moving
// part (the grid's rows, the sun's stripes, the shine across the letters) moves by a whole period over Frames
// frames, so frame Frames is frame 0 again and the animation loops without a seam.
//
// The output is an animated PNG (APNG) with a palette, written below with nothing but the BCL: every browser
// plays it inline in a README, and a dozen flat colours with on-or-off transparency compress well. The dark
// and light files differ only in colours; the README picks one by the reader's theme.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

const int Cell = 5;        // output pixels per cell; the README shows the image at half size, for 2x screens
const int W = 176, H = 76; // canvas, in cells
const int Px = 2;          // cells per pixel of a letter: the letters are chunkier than the scene behind them
const int Horizon = 44;    // row of the horizon line
const int SunRadius = 40;
const int Frames = 32;
const int FrameMs = 70;
const string Word = "blaze-cs";

var glyphs = new Dictionary<char, string[]>
{
    ['b'] = ["XX.....", "XX.....", "XX.....", "XX.....", "XXXXXX.", "XXXXXXX", "XX...XX", "XX...XX", "XX...XX", "XX...XX", "XXXXXXX", "XXXXXX."],
    ['l'] = ["XX", "XX", "XX", "XX", "XX", "XX", "XX", "XX", "XX", "XX", "XX", "XX"],
    ['a'] = [".......", ".......", ".......", ".......", ".XXXXX.", "XXXXXXX", ".....XX", ".XXXXXX", "XXXXXXX", "XX...XX", "XXXXXXX", ".XXXXXX"],
    ['z'] = [".......", ".......", ".......", ".......", "XXXXXXX", "XXXXXXX", "....XX.", "...XX..", "..XX...", ".XX....", "XXXXXXX", "XXXXXXX"],
    ['e'] = [".......", ".......", ".......", ".......", ".XXXXX.", "XXXXXXX", "XX...XX", "XXXXXXX", "XXXXXXX", "XX.....", "XXXXXXX", ".XXXXXX"],
    ['-'] = ["....", "....", "....", "....", "....", "....", "....", "XXXX", "XXXX", "....", "....", "...."],
    ['c'] = [".......", ".......", ".......", ".......", ".XXXXXX", "XXXXXXX", "XX.....", "XX.....", "XX.....", "XX.....", "XXXXXXX", ".XXXXXX"],
    ['s'] = [".......", ".......", ".......", ".......", ".XXXXXX", "XXXXXXX", "XX.....", "XXXXXX.", ".XXXXXX", ".....XX", "XXXXXXX", "XXXXXX."],
};

// One palette per GitHub theme. Letters are banded by row like 80s chrome: sky on top, a dark horizon
// through the middle, sunset underneath.
var themes = new Dictionary<string, Theme>
{
    ["dark"] = new(
        Letters: ["#2de2e6", "#2de2e6", "#6cf0f2", "#6cf0f2", "#b4f8f9", "#ffffff", "#3b1c7a", "#7a2bc4", "#b83cd6", "#f04dcb", "#ff7ad9", "#ffb3e8"],
        Outline: "#1a0b2e", Shadow: "#7b1fa2", Shine: "#ffffff",
        Sun: ["#ffe66d", "#ffc94d", "#ff9e3d", "#ff6b5a", "#ff3c8e", "#d81b9a"],
        Grid: "#ff2ec4", HorizonLine: "#2de2e6", Background: "#0d1117"),
    ["light"] = new(
        Letters: ["#0b5cad", "#0b5cad", "#1288c9", "#1288c9", "#19b3d9", "#5fd6ea", "#3b1c7a", "#5a1e9e", "#8a1fb0", "#c21e9c", "#e0287e", "#f2556b"],
        Outline: "#1a0a3d", Shadow: "#c2188a", Shine: "#ffffff",
        Sun: ["#ffd23f", "#ffb02e", "#ff8a2b", "#ff5a4e", "#f0307e", "#c2188a"],
        Grid: "#c2188a", HorizonLine: "#1288c9", Background: "#ffffff"),
};

var wordWidth = Word.Sum(c => glyphs[c][0].Length) + Word.Length - 1;
var letterLeft = (W - wordWidth * Px) / 2;
var letterTop = Horizon - 10 * Px; // the bottom row of the letters sits just below the horizon

var letterMask = new bool[H, W];
var x0 = letterLeft;
foreach (var c in Word)
{
    var g = glyphs[c];
    for (var r = 0; r < g.Length; r++)
        for (var col = 0; col < g[r].Length; col++)
            if (g[r][col] == 'X')
                for (var y = 0; y < Px; y++)
                    for (var x = 0; x < Px; x++)
                        letterMask[letterTop + r * Px + y, x0 + col * Px + x] = true;
    x0 += (g[0].Length + 1) * Px;
}

var preview = args.Length == 2 && args[0] == "--preview" ? args[1] : null;
var outDir = preview ?? Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "docs/brand/outrun/gen.cs"))!;
Directory.CreateDirectory(outDir);

foreach (var (name, theme) in themes)
{
    var frames = Enumerable.Range(0, Frames).Select(f => Draw(theme, f / (double)Frames)).ToArray();
    var path = Path.Combine(outDir, preview is null ? $"header-{name}.png" : $"preview-{name}.png");
    if (preview is null)
        Apng.Write(path, frames, null, Cell, FrameMs);
    else
        Apng.Write(path, [SideBySide([.. new[] { 0, 8, 16, 24 }.Select(i => frames[i])])], theme.Background, Cell, FrameMs);
    Console.WriteLine($"{path}: {new FileInfo(path).Length / 1024} KiB");
}

// One frame, as a cell grid of "#rrggbb" colours (null is transparent). t runs from 0 to 1 over the loop.
string?[,] Draw(Theme theme, double t)
{
    var cells = new string?[H, W];
    var cx = (W - 1) / 2.0;

    // The sun: a half disc on the horizon, banded from yellow to magenta, its lower half cut by stripes
    // that thicken toward the horizon and slide down.
    const int stripeTop = Horizon - 22, period = 8;
    var stripeShift = (int)Math.Floor(t * period);
    for (var r = Horizon - SunRadius; r < Horizon; r++)
        for (var c = 0; c < W; c++)
        {
            var dx = (c - cx) / SunRadius;
            var dy = (Horizon - r - 0.5) / SunRadius;
            if (dx * dx + dy * dy > 1) continue;
            if (r >= stripeTop)
            {
                var gap = r >= Horizon - 10 ? 4 : 2;
                if (((r - stripeTop - stripeShift) % period + period) % period >= period - gap) continue;
            }
            cells[r, c] = theme.Sun[(r - (Horizon - SunRadius)) * theme.Sun.Length / SunRadius];
        }

    // The floor: a horizon line, rows evenly spaced in depth that come toward the viewer, and rays that
    // fan out from the vanishing point. Near the horizon, both would merge into a solid band, so rows
    // appear only from a few cells down and rays only from where they are four cells apart.
    var floor = H - 1 - Horizon;
    for (var c = 0; c < W; c++) cells[Horizon, c] = theme.HorizonLine;
    for (var k = 0; k < 10; k++)
    {
        var z = 1 + (k - t);
        if (z < 0.9) continue;
        var d = (int)Math.Round(floor / z);
        if (d >= 8 && d <= floor)
            for (var c = 0; c < W; c++) cells[Horizon + d, c] = theme.Grid;
    }
    const double spread = 16; // cells between neighbouring rays at the bottom edge
    var firstRow = (int)Math.Ceiling(4 * floor / spread);
    for (var ray = -9; ray <= 9; ray++)
    {
        int RayX(int d) => (int)Math.Round(cx + ray * spread * d / floor);
        for (var d = firstRow; d <= floor; d++)
        {
            var (from, to) = d == firstRow ? (RayX(d), RayX(d)) : (RayX(d - 1) + Math.Sign(ray), RayX(d));
            for (var c = Math.Min(from, to); c <= Math.Max(from, to); c++)
                if (c >= 0 && c < W) cells[Horizon + d, c] = theme.Grid;
        }
    }

    // The letters: a dark outline one letter pixel wide that keeps them apart from the sun and the grid, a
    // shadow one letter pixel down and right, and a diagonal shine that sweeps across once a loop.
    var outline = new bool[H, W];
    for (var r = 0; r < H; r++)
        for (var c = 0; c < W; c++)
            for (var dy = -Px; dy <= Px && !outline[r, c]; dy++)
                for (var dx = -Px; dx <= Px; dx++)
                    if (r + dy is >= 0 and < H && c + dx is >= 0 and < W && letterMask[r + dy, c + dx]) { outline[r, c] = true; break; }
    for (var r = Px; r < H; r++)
        for (var c = Px; c < W; c++)
            if (outline[r - Px, c - Px] && !outline[r, c])
                cells[r, c] = theme.Shadow;
    var sweep = -12 + t * (W / Px + 40);
    for (var r = 0; r < H; r++)
        for (var c = 0; c < W; c++)
        {
            if (!outline[r, c]) continue;
            if (!letterMask[r, c]) { cells[r, c] = theme.Outline; continue; }
            var row = (r - letterTop) / Px;
            var d = c / Px + row * 0.5 - sweep;
            cells[r, c] = d is >= 0 and < 2.5 ? theme.Shine : theme.Letters[row];
        }
    return cells;
}

static string?[,] SideBySide(string?[][,] frames)
{
    var (h, w) = (frames[0].GetLength(0), frames[0].GetLength(1));
    var all = new string?[h, w * frames.Length];
    for (var i = 0; i < frames.Length; i++)
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
                all[r, i * w + c] = frames[i][r, c];
    return all;
}

record Theme(string[] Letters, string Outline, string Shadow, string Shine, string[] Sun, string Grid, string HorizonLine, string Background);

// A minimal APNG writer: 8-bit palette, transparency only on index 0, every frame full size and replacing the
// last (blend op SOURCE), looping forever. One frame makes a plain PNG.
static class Apng
{
    public static void Write(string path, string?[][,] frames, string? background, int scale, int frameMs)
    {
        var (h, w) = (frames[0].GetLength(0), frames[0].GetLength(1));
        var palette = new List<string?> { background };
        var index = new Dictionary<string, byte>();
        byte IndexOf(string? color)
        {
            color ??= background;
            if (color is null) return 0;
            if (!index.TryGetValue(color, out var i)) { i = (byte)palette.Count; palette.Add(color); index[color] = i; }
            return i;
        }
        var images = frames.Select(f => Pixels(f, w, h, scale, IndexOf)).ToArray();

        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', (byte)'\r', (byte)'\n', 0x1A, (byte)'\n']);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w * scale);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], h * scale);
        ihdr[8] = 8; ihdr[9] = 3; // bit depth 8, palette
        Chunk(file, "IHDR", ihdr);
        if (images.Length > 1)
        {
            Span<byte> actl = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(actl, images.Length); // plays: 0, forever
            Chunk(file, "acTL", actl);
        }
        Chunk(file, "PLTE", palette.SelectMany(c => c is null ? new byte[3] : Convert.FromHexString(c[1..])).ToArray());
        if (background is null) Chunk(file, "tRNS", [0]);

        var sequence = 0;
        for (var f = 0; f < images.Length; f++)
        {
            if (images.Length > 1)
            {
                var fctl = new byte[26];
                BinaryPrimitives.WriteInt32BigEndian(fctl, sequence++);
                BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), w * scale);
                BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), h * scale);
                BinaryPrimitives.WriteInt16BigEndian(fctl.AsSpan(20), (short)frameMs);
                BinaryPrimitives.WriteInt16BigEndian(fctl.AsSpan(22), 1000);
                Chunk(file, "fcTL", fctl); // offsets 0, dispose NONE, blend SOURCE
            }
            if (f == 0)
                Chunk(file, "IDAT", images[f]);
            else
            {
                var fdat = new byte[4 + images[f].Length];
                BinaryPrimitives.WriteInt32BigEndian(fdat, sequence++);
                images[f].CopyTo(fdat, 4);
                Chunk(file, "fdAT", fdat);
            }
        }
        Chunk(file, "IEND", []);
    }

    // The zlib stream of one frame: each scaled row, filter type 0, palette indices.
    static byte[] Pixels(string?[,] cells, int w, int h, int scale, Func<string?, byte> indexOf)
    {
        using var buffer = new MemoryStream();
        using (var z = new ZLibStream(buffer, CompressionLevel.SmallestSize))
        {
            var row = new byte[1 + w * scale];
            for (var r = 0; r < h; r++)
            {
                for (var c = 0; c < w; c++) row.AsSpan(1 + c * scale, scale).Fill(indexOf(cells[r, c]));
                for (var y = 0; y < scale; y++) z.Write(row);
            }
        }
        return buffer.ToArray();
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        s.Write(word);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF);
        s.Write(word);
    }

    static readonly uint[] Table = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    })];

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
