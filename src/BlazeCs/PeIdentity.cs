using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

/// <summary>
/// An assembly's bytes with its debug identity zeroed: the COFF timestamp, the debug directory (PDB id
/// and checksum) and the module MVID. Deterministic builds derive all three from a hash that covers the
/// PDB, and a PDB records the MVID of every reference, so a dependent that was not recompiled after its
/// reference's API changed differs from a fresh build there and nowhere else. So does an assembly after an
/// edit to comments or line positions only (unless code reads them, as [CallerLineNumber] does).
/// `blaze-cs test` hashes the masked bytes to tell whether a test project's assemblies changed.
/// </summary>
static class PeIdentity
{
    public static bool SameCode(byte[] a, byte[] b) => Masked(a).AsSpan().SequenceEqual(Masked(b));

    public static byte[] Masked(byte[] original)
    {
        var bytes = (byte[])original.Clone();
        // The reader reads lazily, so it gets its own copy of the bytes being masked.
        using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(original));
        var headers = pe.PEHeaders;
        Zero(bytes, headers.CoffHeaderStartOffset + 4, 4); // TimeDateStamp

        var table = headers.PEHeader!.DebugTableDirectory;
        if (headers.TryGetDirectoryOffset(table, out var tableOffset))
            Zero(bytes, tableOffset, table.Size);
        foreach (var entry in pe.ReadDebugDirectory())
            Zero(bytes, entry.DataPointer, entry.DataSize);

        var metadata = pe.GetMetadataReader();
        var mvid = metadata.GetModuleDefinition().Mvid;
        var guidHeap = headers.MetadataStartOffset + metadata.GetHeapMetadataOffset(HeapIndex.Guid);
        Zero(bytes, guidHeap + (MetadataTokens.GetHeapOffset(mvid) - 1) * 16, 16);
        return bytes;
    }

    static void Zero(byte[] bytes, int offset, int length) => Array.Clear(bytes, offset, length);
}
