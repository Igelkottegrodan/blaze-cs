#!/usr/bin/env dotnet
#:include ../src/BlazeCs/PeIdentity.cs
// Compares assemblies pairwise, ignoring only their debug identity (see PeIdentity).
//
// usage: dotnet run pe-equal.cs -- <list file of "a b" path pairs>
// Prints one line per pair that differs beyond debug identity; exits 1 if any does.
var differing = 0;
foreach (var line in File.ReadAllLines(args[0]))
{
    var pair = line.Split(' ', 2);
    if (!PeIdentity.SameCode(File.ReadAllBytes(pair[0]), File.ReadAllBytes(pair[1])))
    {
        Console.WriteLine($"differs beyond debug identity: {pair[0]}");
        differing++;
    }
}
return differing == 0 ? 0 : 1;
