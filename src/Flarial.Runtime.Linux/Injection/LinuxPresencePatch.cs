using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Flarial.Runtime.Linux.Injection;

// Profiles cover the shared Windows builds. Beta personalization lives outside .text.
static class LinuxPresencePatch
{
    sealed record Profile(string TextHash, int WindowsRva, int[] References);
    static readonly Profile[] Profiles =
    [
        new("8169a25605d5bf68b7da69729487322adca19d4d6058c7e9616df51fda093480", 0x90a018, [0x5e4015, 0x5e80ba]),
        new("59ddf095ff62b23ea98a4f5adf520d782cccffe589cfa630e5fa1b694e85108b", 0xd912f8, [0x997655, 0x99ba6a]),
    ];

    internal static string Prepare(string source, string cacheDirectory)
    {
        var original = File.ReadAllBytes(source);
        var patched = Patch(original);
        var hash = Convert.ToHexStringLower(SHA256.HashData(original));
        Directory.CreateDirectory(cacheDirectory);
        var destination = Path.Combine(cacheDirectory, $"Flarial.Client.Linux.{hash}.dll");
        if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(patched)) return destination;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, patched);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }

    internal static byte[] Patch(byte[] original)
    {
        using var reader = new PEReader(new MemoryStream(original, false));
        var headers = reader.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus ||
            !headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll))
            throw new InvalidDataException("Linux presence requires a supported 64-bit Flarial DLL.");
        var text = headers.SectionHeaders.Single(s => s.Name == ".text");
        var data = headers.SectionHeaders.Single(s => s.Name == ".rdata");
        var hash = Convert.ToHexStringLower(SHA256.HashData(original.AsSpan(text.PointerToRawData, text.SizeOfRawData)));
        var profile = Profiles.SingleOrDefault(p => p.TextHash == hash)
            ?? throw new InvalidDataException("This client build is not supported by the Linux presence patch. Update the launcher.");
        int Offset(int rva)
        {
            var section = headers.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData);
            return checked(section.PointerToRawData + rva - section.VirtualAddress);
        }
        if (!original.AsSpan(Offset(profile.WindowsRva), 8).SequenceEqual("windows\0"u8))
            throw new InvalidDataException("The client platform string differs from the supported build.");
        var linuxRva = checked(data.VirtualAddress + data.VirtualSize);
        var linuxOffset = checked(data.PointerToRawData + data.VirtualSize);
        if (data.SizeOfRawData - data.VirtualSize < 6 ||
            original.AsSpan(linuxOffset, 6).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("The client has no verified space for the Linux platform string.");
        var patched = (byte[])original.Clone();
        "linux\0"u8.CopyTo(patched.AsSpan(linuxOffset, 6));
        foreach (var rva in profile.References)
        {
            var offset = Offset(rva);
            if (!original.AsSpan(offset, 3).SequenceEqual(new byte[] { 0x48, 0x8d, 0x15 }) ||
                rva + 7 + BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(offset + 3, 4)) != profile.WindowsRva)
                throw new InvalidDataException("The client API platform reference differs from the supported build.");
            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(offset + 3, 4), checked(linuxRva - rva - 7));
        }
        // Extend .rdata into its existing zero-filled file alignment padding.
        var sectionIndex = headers.SectionHeaders.IndexOf(data);
        var sectionTable = headers.PEHeaderStartOffset + headers.CoffHeader.SizeOfOptionalHeader;
        BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(sectionTable + sectionIndex * 40 + 8, 4), data.VirtualSize + 6);
        return patched;
    }
}
