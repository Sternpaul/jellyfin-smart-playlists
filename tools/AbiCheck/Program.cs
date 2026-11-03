// Binary compatibility check for a Jellyfin plugin.
//
//   dotnet run --project tools/AbiCheck -- <plugin.dll> <jellyfin-install-dir>
//
// A plugin binds to exact method signatures. When Jellyfin changes one (12.0 added a
// parameter to IPlaylistManager.AddItemToPlaylistAsync) the plugin still loads, then
// fails at runtime with MissingMethodException. This tool reads every type and member
// the plugin references in host assemblies and checks each exists, with the same
// signature, in the given server install. It also checks that host interfaces and
// abstract base classes the plugin implements have gained no new required members.
// Exit code 1 lists every mismatch.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: AbiCheck <plugin.dll> <jellyfin-install-dir>");
    return 2;
}

var pluginPath = Path.GetFullPath(args[0]);
var hostDir = Path.GetFullPath(args[1]);

// Host assemblies come from the server install, the BCL from this runtime.
var assemblyPaths = Directory.GetFiles(hostDir, "*.dll")
    .Concat(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
    .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
    .Select(g => g.First())
    .Where(p => !string.Equals(Path.GetFileName(p), Path.GetFileName(pluginPath), StringComparison.OrdinalIgnoreCase))
    .Append(pluginPath)
    .ToList();

string[] hostPrefixes = { "MediaBrowser.", "Jellyfin.", "Emby.", "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "SkiaSharp" };
bool IsHost(string? assemblyName) => assemblyName != null && hostPrefixes.Any(assemblyName.StartsWith);

using var context = new MetadataLoadContext(new PathAssemblyResolver(assemblyPaths));
using var stream = File.OpenRead(pluginPath);
using var peReader = new PEReader(stream);
var reader = peReader.GetMetadataReader();
var names = new SignatureNames(reader);
var problems = new List<string>();
int checkedMembers = 0;

Type? FindType(string fullName, string assemblyName)
{
    try
    {
        var type = context.LoadFromAssemblyName(assemblyName).GetType(fullName);
        if (type != null) return type;
    }
    catch (FileNotFoundException)
    {
        problems.Add($"missing assembly {assemblyName}");
        return null;
    }
    // Type forwarded to another host assembly.
    foreach (var path in assemblyPaths.Where(p => IsHost(Path.GetFileNameWithoutExtension(p))))
    {
        try
        {
            var type = context.LoadFromAssemblyPath(path).GetType(fullName);
            if (type != null) return type;
        }
        catch (BadImageFormatException) { /* native library */ }
    }
    return null;
}

const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

foreach (var handle in reader.TypeReferences)
{
    var (fullName, assemblyName) = names.Describe(handle);
    if (!IsHost(assemblyName)) continue;
    if (FindType(fullName!, assemblyName!) == null)
        problems.Add($"missing type {fullName} [{assemblyName}]");
}

foreach (var handle in reader.MemberReferences)
{
    var member = reader.GetMemberReference(handle);
    var (typeName, assemblyName) = names.DescribeParent(member.Parent);
    if (!IsHost(assemblyName)) continue;
    var type = FindType(typeName!, assemblyName!);
    if (type == null) continue; // already reported as a missing type
    var name = reader.GetString(member.Name);
    checkedMembers++;

    if (member.GetKind() == MemberReferenceKind.Field)
    {
        if (type.GetField(name, All) == null) problems.Add($"missing field {typeName}::{name}");
        continue;
    }

    var signature = member.DecodeMethodSignature(names, null);
    var wanted = $"{signature.ReturnType} ({string.Join(", ", signature.ParameterTypes)})";
    IEnumerable<MethodBase> candidates = name == ".ctor"
        ? type.GetConstructors(All)
        : type.GetMethods(All).Where(m => m.Name == name);
    var available = candidates.Select(Describe).ToList();
    if (!available.Contains(wanted))
        problems.Add($"missing method {typeName}::{name} {wanted}"
            + (available.Count == 0 ? "\n    host has no member with that name" : "\n    host has: " + string.Join("\n              ", available)));
}

// New abstract members on host interfaces/base classes the plugin implements would
// make Jellyfin fail with TypeLoadException when it loads the plugin type.
var plugin = context.LoadFromAssemblyPath(pluginPath);
foreach (var type in plugin.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
{
    var implemented = new HashSet<string>(
        Hierarchy(type).SelectMany(t => t.GetMethods(All)).Where(m => !m.IsAbstract).SelectMany(m => new[] { m.Name, Unqualified(m.Name) }),
        StringComparer.Ordinal);
    var required = type.GetInterfaces().Where(i => IsHost(i.Assembly.GetName().Name)).SelectMany(i => i.GetMethods())
        .Concat(Hierarchy(type).Skip(1).Where(t => IsHost(t.Assembly.GetName().Name)).SelectMany(t => t.GetMethods(All)))
        .Where(m => m.IsAbstract);
    foreach (var method in required)
        if (!implemented.Contains(method.Name))
            problems.Add($"{type.FullName} does not implement {method.DeclaringType?.FullName}::{method.Name}");
}

if (problems.Count > 0)
{
    Console.Error.WriteLine($"ABI check FAILED against {hostDir}:");
    foreach (var problem in problems.Distinct()) Console.Error.WriteLine("  " + problem);
    return 1;
}

Console.WriteLine($"ABI check passed: {checkedMembers} host member references resolve in {hostDir}.");
return 0;

static IEnumerable<Type> Hierarchy(Type type)
{
    for (var t = type; t != null; t = t.BaseType) yield return t;
}

static string Unqualified(string name) => name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;

static string Describe(MethodBase method)
{
    var definition = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : method;
    var returnType = definition is MethodInfo info ? Normalize(info.ReturnType) : "System.Void";
    return $"{returnType} ({string.Join(", ", definition.GetParameters().Select(p => Normalize(p.ParameterType)))})";
}

static string Normalize(Type type)
{
    if (type.IsGenericParameter) return (type.DeclaringMethod != null ? "!!" : "!") + type.GenericParameterPosition;
    if (type.IsByRef) return Normalize(type.GetElementType()!) + "&";
    if (type.IsPointer) return Normalize(type.GetElementType()!) + "*";
    if (type.IsArray) return Normalize(type.GetElementType()!) + (type.IsSZArray ? "[]" : $"[{new string(',', type.GetArrayRank() - 1)}]");
    if (type.IsGenericType && !type.IsGenericTypeDefinition)
        return $"{type.GetGenericTypeDefinition().FullName}<{string.Join(",", type.GetGenericArguments().Select(Normalize))}>";
    return type.FullName ?? type.Name;
}

/// <summary>Renders signature types in the same text form as <see cref="Normalize"/>.</summary>
sealed class SignatureNames(MetadataReader reader) : ISignatureTypeProvider<string, object?>
{
    public (string? Name, string? Assembly) DescribeParent(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => Describe((TypeReferenceHandle)parent),
        HandleKind.TypeSpecification => DescribeSpecification((TypeSpecificationHandle)parent),
        _ => (null, null),
    };

    public (string? Name, string? Assembly) Describe(TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        switch (type.ResolutionScope.Kind)
        {
            case HandleKind.TypeReference:
                var (outer, assembly) = Describe((TypeReferenceHandle)type.ResolutionScope);
                return ($"{outer}+{name}", assembly);
            case HandleKind.AssemblyReference:
                var ns = reader.GetString(type.Namespace);
                var scope = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
                return (ns.Length > 0 ? $"{ns}.{name}" : name, reader.GetString(scope.Name));
            default:
                return (null, null);
        }
    }

    private (string? Name, string? Assembly) DescribeSpecification(TypeSpecificationHandle handle)
    {
        var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return (null, null);
        blob.ReadCompressedInteger();
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeReference ? Describe((TypeReferenceHandle)generic) : (null, null);
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + (typeCode switch
    {
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        _ => typeCode.ToString(),
    });

    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = r.GetTypeDefinition(handle);
        var ns = r.GetString(type.Namespace);
        var name = r.GetString(type.Name);
        if (type.GetDeclaringType() is { IsNil: false } declaring)
            return GetTypeFromDefinition(r, declaring, rawTypeKind) + "+" + name;
        return ns.Length > 0 ? $"{ns}.{name}" : name;
    }

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind) => Describe(handle).Name ?? "?";
    public string GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle handle, byte rawTypeKind)
        => r.GetTypeSpecification(handle).DecodeSignature(this, context);
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
    public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? context, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
}
