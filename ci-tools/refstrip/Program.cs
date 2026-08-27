// refstrip: shrinks a .NET assembly into a compile-time-only "reference
// assembly" by replacing every method body's IL with a minimal
// `ldnull; throw` stub, in place, at the metadata level.
//
// Why this exists: the mod build needs the real Stardew Valley/SMAPI game
// DLLs to compile against (see ../../README.md#ci-builds), but they're
// copyrighted, ~9MB total, and the repo is public, so they can't be
// committed or attached anywhere publicly downloadable (a GitHub Release
// asset, a public repo file, etc). GitHub Actions secrets stay private even
// on a public repo, but are capped at 48KB each with 100 secrets per repo
// (~4.9MB total capacity) - less than the ~5.2MB the raw gzipped+base64'd
// DLLs need. Stripping method bodies (which mod code never needs - only
// the public type/method *signatures* matter for compiling against them)
// shrinks the gzipped payload enough to fit in a few dozen chunked secrets
// with real headroom to spare.
//
// This only touches method body bytes (via their RVA), never any metadata
// table (types, members, signatures, custom attributes, .param default
// values, generic constraints, interface overrides, etc) - so everything
// the C# compiler needs to bind against the assembly's public API survives
// untouched. It does NOT attempt to resolve external type references (unlike
// a disassemble/reassemble round-trip through a tool like ildasm/ilasm),
// which is what makes it robust against assemblies that reference types
// this tool has no way to load (e.g. facade/forwarder assemblies for a
// different runtime).
//
// Usage: dotnet run -- <input.dll> <output.dll>

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: refstrip <input.dll> <output.dll>");
    return 1;
}

string inputPath = args[0];
string outputPath = args[1];

byte[] bytes = File.ReadAllBytes(inputPath);

int totalMethods = 0, withBody = 0, stripped = 0, skippedTooSmall = 0;

using (var ms = new MemoryStream(bytes, writable: false))
using (var pe = new PEReader(ms))
{
    var headers = pe.PEHeaders;
    var md = pe.GetMetadataReader();

    int RvaToFileOffset(int rva)
    {
        int idx = headers.GetContainingSectionIndex(rva);
        if (idx < 0) throw new InvalidOperationException($"RVA {rva:X} not in any section");
        var sh = headers.SectionHeaders[idx];
        return sh.PointerToRawData + (rva - sh.VirtualAddress);
    }

    foreach (var handle in md.MethodDefinitions)
    {
        totalMethods++;
        var mdef = md.GetMethodDefinition(handle);
        int rva = mdef.RelativeVirtualAddress;
        if (rva == 0) continue; // abstract / extern / interface method: no body to strip
        withBody++;

        MethodBodyBlock body = pe.GetMethodBody(rva);
        int size = body.Size; // total bytes occupied by this method body, incl. header + exception sections

        // Replacement body: tiny-format header (1 byte) + ldnull (0x14) + throw (0x7A) = 3 bytes.
        // `ldnull; throw` verifies for ANY method signature (null is assignable to any
        // reference type per ECMA-335), so it needs no external type reference at all.
        const int newSize = 3;
        if (size < newSize)
        {
            skippedTooSmall++;
            continue;
        }

        int fileOffset = RvaToFileOffset(rva);

        byte tinyHeader = (byte)((2 << 2) | 0x2); // tiny format, 2 bytes of code, implicit maxstack 8
        bytes[fileOffset + 0] = tinyHeader;
        bytes[fileOffset + 1] = 0x14; // ldnull
        bytes[fileOffset + 2] = 0x7A; // throw
        for (int i = newSize; i < size; i++)
            bytes[fileOffset + i] = 0; // zero the abandoned tail so gzip crushes it away

        stripped++;
    }
}

File.WriteAllBytes(outputPath, bytes);

Console.WriteLine($"{Path.GetFileName(inputPath)}: methods={totalMethods} withBody={withBody} stripped={stripped} skippedTooSmall={skippedTooSmall}");
return 0;
