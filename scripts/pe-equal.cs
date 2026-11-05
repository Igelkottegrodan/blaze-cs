// Compares assemblies pairwise, ignoring only their debug identity: the COFF timestamp, the debug
// directory (PDB id and checksum) and the module MVID. Deterministic builds derive all three from a
// hash that covers the PDB, and a PDB records the MVID of every reference, so a dependent that was not
// recompiled after its reference's API changed differs from a fresh build there and nowhere else.
//
// usage: dotnet run pe-equal.cs -- <list file of "a b" path pairs>
// Prints one line per pair that differs beyond debug identity; exits 1 if any does.
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var differing = 0;
foreach (var line in File.ReadAllLines(args[0]))
{
    var pair = line.Split(' ', 2);
    if (!Masked(pair[0]).AsSpan().SequenceEqual(Masked(pair[1])))
    {
        Console.WriteLine($"differs beyond debug identity: {pair[0]}");
        differing++;
    }
}
return differing == 0 ? 0 : 1;

static byte[] Masked(string path)
{
    var bytes = File.ReadAllBytes(path);
    // The reader reads lazily, so it gets its own copy of the bytes being masked.
    using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(bytes));
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
