using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Flarial.Runtime.Linux.Injection;

if (args.Length == 0) throw new Exception("Pass the current release and beta DLL paths.");
foreach (var path in args)
{
    var original = File.ReadAllBytes(path);
    var directory = Path.Combine(Path.GetTempPath(), "flarial-patch-test-" + Guid.NewGuid());
    try
    {
        var cached = LinuxPresencePatch.Prepare(path, directory);
        var patched = File.ReadAllBytes(cached);
        if (!File.ReadAllBytes(path).SequenceEqual(original) || patched.Length != original.Length) throw new Exception("Original changed");
        if (LinuxPresencePatch.Prepare(path, directory) != cached) throw new Exception("Cache was not reused");
        using var reader = new PEReader(new MemoryStream(patched));
        if (!reader.PEHeaders.SectionHeaders.Any(s => s.Name == ".rdata" && patched.AsSpan(s.PointerToRawData + s.VirtualSize - 6, 6).SequenceEqual("linux\0"u8))) throw new Exception("Linux string is not mapped");
        var changes = original.Zip(patched).Count(p => p.First != p.Second);
        if (changes > 14 || changes < 7) throw new Exception("Unexpected patch extent");
        File.WriteAllBytes(cached, new byte[10]);
        if (!File.ReadAllBytes(LinuxPresencePatch.Prepare(path, directory)).SequenceEqual(patched)) throw new Exception("Damaged cache was trusted");
        using var originalReader = new PEReader(new MemoryStream(original));
        var text = originalReader.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
        // Model beta personalization in writable data: it must survive byte-for-byte.
        var data = originalReader.PEHeaders.SectionHeaders.Single(s => s.Name == ".data");
        var personalized = (byte[])original.Clone(); personalized[data.PointerToRawData] ^= 1;
        var personalizedPatch = LinuxPresencePatch.Patch(personalized);
        if (personalizedPatch[data.PointerToRawData] != personalized[data.PointerToRawData]) throw new Exception("Personalization changed");
        var unsupported = (byte[])original.Clone(); unsupported[text.PointerToRawData] ^= 1;
        try { LinuxPresencePatch.Patch(unsupported); throw new Exception("Unknown build accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine($"PASS {Path.GetFileName(path)}: original preserved, {changes} bytes changed, cache repaired, unknown build rejected");
    }
    finally { Directory.Delete(directory, true); }
}
