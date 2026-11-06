using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BlazeCs;

/// <summary>
/// What the daemon last wrote to or verified in each output file, with the file's stamp as of then, so that
/// deciding whether a file already holds the bytes about to be written reads the disk only when something
/// else wrote the file since. It remembers each file's length and hash, never its bytes: on jellyfin the
/// bytes of every assembly, PDB, documentation file and reference assembly were 30 to 40 MB of heap.
/// </summary>
sealed class PublishedOutputs
{
    readonly ConcurrentDictionary<string, Published> _published = new(PathComparer.Instance);

    sealed record Published(OutputContent Content, DateTime Modified);

    /// <summary>Whether a file holds these bytes; if it does, that is remembered with the file's stamp.</summary>
    public bool Holds(string path, ReadOnlySpan<byte> bytes) => Holds(path, OutputContent.Of(bytes));

    /// <inheritdoc cref="Holds(string, ReadOnlySpan{byte})"/>
    public bool Holds(string path, OutputContent content)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return false;
        if (_published.TryGetValue(path, out var last) && last.Content.Length == info.Length && last.Modified == info.LastWriteTimeUtc)
            return last.Content == content;
        if (info.Length != content.Length || OutputContent.Read(path) != content)
            return false;
        _published[path] = new Published(content, info.LastWriteTimeUtc);
        return true;
    }

    /// <summary>Whether a file still holds what the daemon last wrote to or verified in it.</summary>
    public bool HoldsLast(string path) => _published.TryGetValue(path, out var last) && Holds(path, last.Content);

    /// <summary>Remembers that the daemon just wrote this content to a file.</summary>
    public void Wrote(string path, OutputContent content) =>
        _published[path] = new Published(content, File.GetLastWriteTimeUtc(path));

    public void Forget(string path) => _published.TryRemove(path, out _);
}

/// <summary>
/// A file's bytes, as their length and SHA-256. Two contents are equal exactly when their bytes are: a
/// collision is treated as impossible. SHA-256 is cryptographic, so finding two outputs with one hash takes
/// about 2^128 work even on purpose, and the chance that any two of the few thousand files a solution emits
/// collide by accident is below 2^-230. It is in the BCL, so the daemon ships no hashing package next to the
/// SDK's Roslyn it loads, and it runs on the CPU's SHA instructions (SHA-NI, ARMv8 SHA2) where they exist:
/// about 2 GB/s, a few milliseconds for the outputs of one edit. A non-cryptographic 128-bit hash such as
/// XxHash128 would be faster, but lives in the System.IO.Hashing package.
/// </summary>
readonly record struct OutputContent(long Length, UInt128 Hash0, UInt128 Hash1)
{
    public static OutputContent Of(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return From(bytes.Length, hash);
    }

    /// <summary>The content of a file on disk, streamed rather than read whole.</summary>
    public static OutputContent Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0, FileOptions.SequentialScan);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(stream, hash);
        return From(stream.Length, hash);
    }

    static OutputContent From(long length, ReadOnlySpan<byte> hash) =>
        new(length, BinaryPrimitives.ReadUInt128LittleEndian(hash), BinaryPrimitives.ReadUInt128LittleEndian(hash[16..]));
}
