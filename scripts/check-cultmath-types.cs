// Release gate helper for build-unity-package.ps1: reads an assembly's type-definition table and
// reports which of the named CultMath types it does not define as a public top-level type.
// Usage: dotnet run scripts/check-cultmath-types.cs -- <assembly.dll> <TypeName>...
// Exit 0 when every type is defined, 1 when any is missing, 2 on bad usage.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: check-cultmath-types <assembly.dll> <TypeName>...");
    return 2;
}

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
var defined = new HashSet<string>();
foreach (var handle in metadata.TypeDefinitions)
{
    var type = metadata.GetTypeDefinition(handle);
    if (!type.GetDeclaringType().IsNil) continue;
    if ((type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public) continue;
    if (metadata.GetString(type.Namespace) == "CultMath") defined.Add(metadata.GetString(type.Name));
}

var missing = args.Skip(1).Where(name => !defined.Contains(name)).ToArray();
if (missing.Length == 0) return 0;
Console.WriteLine(string.Join(", ", missing));
return 1;
