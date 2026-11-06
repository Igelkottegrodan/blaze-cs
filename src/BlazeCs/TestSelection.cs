using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace BlazeCs;

/// <summary>
/// Which test classes of a test assembly can run code that a hot reload delta changed, read from the metadata
/// of the solution's assemblies the tests run with (the hot reload session's modules, as built now).
///
/// Every type there names the types it uses: its base type and interfaces, its members' signatures and
/// attributes (with the types an attribute takes as arguments, as `typeof(X)`), and every type, method and
/// field its methods' IL names. A type's nested types count as part of it (lambdas, async and iterator state
/// machines, nested classes). A test class can reach an updated type if a chain of such uses leads from it
/// to the type; only those test classes run.
///
/// That chain is what runs, unless code reaches a type without naming it. So every test runs (the whole
/// assembly, with the reason in the daemon log) when the delta updates a type that:
/// - code can reach through another type: an interface or abstract class, a class with a virtual method, a
///   type that implements an interface, derives from anything but object, ValueType or Enum, or that another
///   type derives from. A caller may hold it as that other type, and frameworks find such types by scanning
///   (controllers, handlers, components);
/// - is generic;
/// - carries an attribute other than the compiler's (frameworks find such types by scanning too);
/// - is the module's own (`&lt;Module&gt;`: module initializers);
/// - is reachable from code that runs around every test: a module initializer, a type an assembly attribute
///   names (an assembly fixture, a test framework), MSTest's assembly and global test initialize and cleanup,
///   an NUnit SetUpFixture.
/// A compiler-generated type (a lambda's, an async method's) is judged as the type that declares it, since
/// only that type's code creates it. A test class (as the framework reports it) and its base classes are
/// exempt from these rules: the framework finds them, and the selection is of them.
///
/// Every test also runs when the delta doesn't say which types it updated, or when the framework names a test
/// class no type of the test assembly has the name of (a generic or parameterized class). A type that looks
/// up types by name or scans assemblies (Type.GetType, Assembly.GetTypes, Activator.CreateInstance, ...)
/// may reach any type: each test class that reaches one runs whatever the delta changed.
/// </summary>
static class TestSelection
{
    /// <summary>A type, by its assembly's simple name and its full metadata name (a nested type's after its declaring type's and a '+').</summary>
    internal readonly record struct TypeKey(string Assembly, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>The test classes to run, or why every test runs.</summary>
    public sealed record Selection(IReadOnlySet<string>? Classes, string? Everything);

    /// <param name="testAssembly">The test assembly, one of <paramref name="assemblies"/>.</param>
    /// <param name="assemblies">The solution's assemblies the tests run with, as built now.</param>
    /// <param name="updates">Each delta's baseline (the module its type tokens are of) and its updated types' TypeDef tokens, null if it doesn't say.</param>
    /// <param name="testClasses">The test assembly's test classes, as the framework names them in results.</param>
    public static Selection Select(string testAssembly, IReadOnlyCollection<string> assemblies, IEnumerable<(string Baseline, int[]? Types)> updates,
        IReadOnlyCollection<string> testClasses)
    {
        var modules = assemblies.Select(Read).ToList();
        var test = Read(testAssembly);
        var byAssembly = modules.GroupBy(module => module.Assembly, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // The test classes, and their base classes in the test assembly, which the rules don't apply to.
        var exempt = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in testClasses)
        {
            if (!test.Types.ContainsKey(name))
                return Everything($"the framework names a test class {name}, and {test.Assembly} has no type of that name");
            for (string? type = name; type is not null && exempt.Add(type);)
                type = test.Types.TryGetValue(type, out var facts) && facts.Base is { } next && next.Assembly == test.Assembly ? next.Name : null;
        }

        var targets = new HashSet<TypeKey>();
        foreach (var (baseline, tokens) in updates)
        {
            var before = Read(baseline);
            if (tokens is null)
                return Everything($"the delta to {before.Assembly} doesn't say which types it updated");
            if (!byAssembly.TryGetValue(before.Assembly, out var module))
                return Everything($"{before.Assembly} isn't among the assemblies the tests run with");
            foreach (var token in tokens)
            {
                var row = MetadataTokens.GetRowNumber(MetadataTokens.EntityHandle(token));
                if (MetadataTokens.EntityHandle(token).Kind != HandleKind.TypeDefinition || row < 1 || row > before.ByRow.Count)
                    return Everything($"the delta to {before.Assembly} updates a type its baseline doesn't have (token {token:x8})");
                var name = before.ByRow[row - 1];
                if (!module.Types.TryGetValue(name, out var updated))
                    return Everything($"{module.Assembly} no longer has {name}, which the delta updates");
                if (Untraceable(module, name, modules, module.Assembly == test.Assembly ? exempt : []) is { } reason)
                    return Everything($"the delta updates {Display(name)} ({module.Assembly}): {reason}");
                targets.Add(new TypeKey(module.Assembly, updated.Group));
            }
        }

        // Every type that can reach a target: an updated type, or one that looks up types by name.
        foreach (var module in modules)
            targets.UnionWith(module.ByName.Select(group => new TypeKey(module.Assembly, group)));
        var users = new Dictionary<TypeKey, List<TypeKey>>();
        foreach (var module in modules)
        {
            foreach (var (group, uses) in module.Uses)
            {
                var user = new TypeKey(module.Assembly, group);
                foreach (var used in uses)
                {
                    if (!users.TryGetValue(used, out var list))
                        users[used] = list = [];
                    list.Add(user);
                }
            }
        }
        var reaching = new HashSet<TypeKey>(targets);
        var queue = new Queue<TypeKey>(targets);
        while (queue.TryDequeue(out var type))
        {
            foreach (var user in users.GetValueOrDefault(type, []))
            {
                if (reaching.Add(user))
                    queue.Enqueue(user);
            }
        }

        foreach (var module in modules)
        {
            var roots = module.Roots.Select(group => new TypeKey(module.Assembly, group)).Concat(module.NamedRoots);
            if (roots.FirstOrDefault(reaching.Contains) is { Name: not null } root)
                return Everything($"{Display(root.Name)} ({root.Assembly}) runs around every test and can reach what the delta updates");
        }
        return new Selection(testClasses.Where(name => reaching.Contains(new TypeKey(test.Assembly, test.Types[name].Group))).ToHashSet(StringComparer.Ordinal), null);
    }

    static Selection Everything(string reason) => new(null, reason);

    /// <summary>
    /// Why code may reach an updated type without naming it, or null: judged on the type that declares it if
    /// the compiler generated it, and never on an exempt type.
    /// </summary>
    static string? Untraceable(ModuleTypes module, string name, List<ModuleTypes> modules, HashSet<string> exempt)
    {
        if (name == "<Module>")
            return "it is the module's own type, whose initializer runs before any test";
        // A nested type the compiler generated (its name starts with '<') is created only by its declaring type's code.
        var user = name;
        while (user.LastIndexOf('+') is var plus and > 0 && user[(plus + 1)..].StartsWith('<'))
            user = user[..plus];
        if (exempt.Contains(user))
            return null;
        var declared = user == name ? "" : $"it is declared by {Display(user)}, and ";
        if (module.Types[user].Untraceable is { } reason)
            return declared + reason;
        var key = new TypeKey(module.Assembly, user);
        foreach (var other in modules)
        {
            if (other.Types.FirstOrDefault(type => type.Value.Base == key) is { Key: { } derived })
                return $"{declared}{Display(derived)} ({other.Assembly}) derives from it";
        }
        return null;
    }

    /// <summary>A type's name without its namespace (a nested type's with its declaring type's).</summary>
    internal static string Display(string name) => name.Length == 0 ? name : name.LastIndexOf('.', name.IndexOf('+') is var plus and >= 0 ? plus : name.Length - 1) is var dot and >= 0 ? name[(dot + 1)..] : name;

    /// <summary>One assembly's types as the selection sees them.</summary>
    sealed class ModuleTypes
    {
        public required string Assembly { get; init; }
        /// <summary>Its types by full name.</summary>
        public Dictionary<string, TypeFacts> Types { get; } = new(StringComparer.Ordinal);
        /// <summary>Its types' full names by TypeDef row (from 1).</summary>
        public List<string> ByRow { get; } = [];
        /// <summary>The types each of its top-level types (with its nested ones) names, each as its top-level type.</summary>
        public Dictionary<string, HashSet<TypeKey>> Uses { get; } = new(StringComparer.Ordinal);
        /// <summary>Its top-level types that look up types by name, or scan assemblies for them.</summary>
        public HashSet<string> ByName { get; } = new(StringComparer.Ordinal);
        /// <summary>Its top-level types that run around every test.</summary>
        public HashSet<string> Roots { get; } = new(StringComparer.Ordinal);
        /// <summary>The top-level types its assembly and module attributes name, which run around every test.</summary>
        public HashSet<TypeKey> NamedRoots { get; } = [];
    }

    /// <summary>A type: its top-level type (itself, if it isn't nested), its base type, and why code may reach it without naming it, if it may.</summary>
    sealed record TypeFacts(string Group, TypeKey? Base, string? Untraceable);

    static readonly ConcurrentDictionary<string, (FileStamp Stamp, ModuleTypes Types)> Cache = new(PathComparer.Instance);

    /// <summary>An assembly's types, read again only when its length or write time changed.</summary>
    static ModuleTypes Read(string path)
    {
        var stamp = FileStamp.Of(path);
        if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Types;
        using var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(path)));
        var types = new ModuleReader(pe).Read();
        Cache[path] = (stamp, types);
        return types;
    }

    /// <summary>
    /// Members of System's reflection types that find or load types by name, or enumerate an assembly's or the
    /// process's: what they return can be any type.
    /// </summary>
    static readonly Dictionary<string, HashSet<string>> ByNameMembers = new(StringComparer.Ordinal)
    {
        ["System.Type"] = ["GetType", "GetTypeFromProgID", "GetTypeFromCLSID", "ReflectionOnlyGetType"],
        ["System.Activator"] = ["CreateInstance", "CreateInstanceFrom"],
        ["System.AppDomain"] = ["GetAssemblies", "CreateInstance", "CreateInstanceAndUnwrap", "CreateInstanceFrom", "CreateInstanceFromAndUnwrap", "Load", "ExecuteAssembly", "ExecuteAssemblyByName"],
        ["System.Reflection.Assembly"] = ["GetType", "GetTypes", "GetExportedTypes", "get_DefinedTypes", "get_ExportedTypes", "GetForwardedTypes", "CreateInstance",
            "Load", "LoadFrom", "LoadFile", "LoadWithPartialName", "UnsafeLoadFrom", "GetModules", "get_Modules", "GetLoadedModules", "GetModule"],
        ["System.Reflection.Module"] = ["GetType", "GetTypes", "FindTypes"],
        ["System.Runtime.Loader.AssemblyLoadContext"] = ["LoadFromAssemblyPath", "LoadFromAssemblyName", "LoadFromStream", "LoadFromNativeImagePath", "get_Assemblies"],
    };

    /// <summary>Attributes on methods (MSTest) or types (NUnit) that make the framework run them around every test, by type name.</summary>
    static readonly HashSet<string> RootAttributes = ["AssemblyInitializeAttribute", "AssemblyCleanupAttribute", "GlobalTestInitializeAttribute",
        "GlobalTestCleanupAttribute", "SetUpFixtureAttribute"];

    /// <summary>Attributes the compiler emits, and others no framework finds types by, by namespace or full name.</summary>
    static readonly HashSet<string> InertAttributes = ["System.Runtime.CompilerServices", "System.Diagnostics", "System.Diagnostics.CodeAnalysis",
        "Microsoft.CodeAnalysis", "System.CodeDom.Compiler", "System.Reflection.DefaultMemberAttribute", "System.ObsoleteAttribute", "System.FlagsAttribute",
        "System.CLSCompliantAttribute"];

    /// <summary>Reads one assembly's types, and what each top-level type names.</summary>
    sealed class ModuleReader : ISignatureTypeProvider<object?, object?>, ICustomAttributeTypeProvider<string?>
    {
        readonly PEReader _pe;
        readonly MetadataReader _md;
        readonly string _assembly;
        // What the type being read names.
        HashSet<TypeKey> _uses = [];

        public ModuleReader(PEReader pe)
        {
            _pe = pe;
            _md = pe.GetMetadataReader();
            _assembly = _md.IsAssembly ? _md.GetString(_md.GetAssemblyDefinition().Name) : _md.GetString(_md.GetModuleDefinition().Name);
        }

        public ModuleTypes Read()
        {
            var module = new ModuleTypes { Assembly = _assembly };
            foreach (var handle in _md.TypeDefinitions)
            {
                var name = FullName(handle);
                var type = _md.GetTypeDefinition(handle);
                var group = Outermost(name);
                module.ByRow.Add(name);
                module.Types[name] = new TypeFacts(group, !type.BaseType.IsNil && type.BaseType.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference
                    ? Named(type.BaseType) is { } baseType ? baseType with { Name = FullName(type.BaseType) } : null
                    : null, Untraceable(type));
                if (!module.Uses.TryGetValue(group, out var uses))
                    module.Uses[group] = uses = [];
                _uses = uses;
                if (!ReadType(type))
                    module.ByName.Add(group);
                if (name == "<Module>" || type.GetCustomAttributes().Any(a => RootAttributes.Contains(AttributeType(a).Name))
                    || type.GetMethods().Any(m => _md.GetMethodDefinition(m).GetCustomAttributes().Any(a => RootAttributes.Contains(AttributeType(a).Name))))
                    module.Roots.Add(group);
            }
            _uses = module.NamedRoots;
            var attributes = _md.GetModuleDefinition().GetCustomAttributes().AsEnumerable();
            if (_md.IsAssembly)
                attributes = attributes.Concat(_md.GetAssemblyDefinition().GetCustomAttributes());
            foreach (var attribute in attributes)
                AttributeArguments(attribute);
            return module;
        }

        /// <summary>Adds what a type names to the current set; false if it looks up types by name, or names what can't be read.</summary>
        bool ReadType(TypeDefinition type)
        {
            var known = true;
            if (!type.BaseType.IsNil)
                Use(type.BaseType);
            foreach (var implementation in type.GetInterfaceImplementations())
                Use(_md.GetInterfaceImplementation(implementation).Interface);
            known &= Attributes(type.GetCustomAttributes());
            known &= GenericParameters(type.GetGenericParameters());
            foreach (var handle in type.GetFields())
            {
                var field = _md.GetFieldDefinition(handle);
                field.DecodeSignature(this, null);
                known &= Attributes(field.GetCustomAttributes());
            }
            foreach (var handle in type.GetProperties())
            {
                var property = _md.GetPropertyDefinition(handle);
                property.DecodeSignature(this, null);
                known &= Attributes(property.GetCustomAttributes());
            }
            foreach (var handle in type.GetEvents())
            {
                var e = _md.GetEventDefinition(handle);
                Use(e.Type);
                known &= Attributes(e.GetCustomAttributes());
            }
            foreach (var handle in type.GetMethodImplementations())
            {
                var implementation = _md.GetMethodImplementation(handle);
                Use(implementation.MethodBody);
                known &= Use(implementation.MethodDeclaration);
            }
            foreach (var handle in type.GetMethods())
            {
                var method = _md.GetMethodDefinition(handle);
                method.DecodeSignature(this, null);
                known &= Attributes(method.GetCustomAttributes());
                known &= GenericParameters(method.GetGenericParameters());
                foreach (var parameter in method.GetParameters())
                    known &= Attributes(_md.GetParameter(parameter).GetCustomAttributes());
                if (method.RelativeVirtualAddress != 0)
                    known &= Body(_pe.GetMethodBody(method.RelativeVirtualAddress));
            }
            return known;
        }

        bool GenericParameters(GenericParameterHandleCollection parameters)
        {
            var known = true;
            foreach (var handle in parameters)
            {
                var parameter = _md.GetGenericParameter(handle);
                foreach (var constraint in parameter.GetConstraints())
                    Use(_md.GetGenericParameterConstraint(constraint).Type);
                known &= Attributes(parameter.GetCustomAttributes());
            }
            return known;
        }

        /// <summary>The types a method's IL names, its locals' and the types it catches; false if it looks up types by name.</summary>
        bool Body(MethodBodyBlock body)
        {
            var known = true;
            if (!body.LocalSignature.IsNil)
                _md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(this, null);
            foreach (var region in body.ExceptionRegions)
            {
                if (!region.CatchType.IsNil)
                    Use(region.CatchType);
            }
            var il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                int code = il.ReadByte();
                if (code == 0xFE)
                    code = 0xFE00 | il.ReadByte();
                switch (OperandSize(code))
                {
                    case Token:
                        var token = il.ReadInt32();
                        if (token >>> 24 != 0x70) // a string's
                            known &= Use(MetadataTokens.EntityHandle(token));
                        break;
                    case Switch:
                        var targets = il.ReadInt32(); // read first: `Offset +=` would take the offset from before it
                        il.Offset += 4 * targets;
                        break;
                    case var size:
                        il.Offset += size;
                        break;
                }
            }
            return known;
        }

        const int Token = -1, Switch = -2;

        /// <summary>The size of an IL instruction's operand (ECMA-335 III), or Token or Switch.</summary>
        static int OperandSize(int code) => code switch
        {
            0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F or (>= 0x2B and <= 0x37) or 0xDE or 0xFE12 or 0xFE19 => 1,
            (>= 0xFE09 and <= 0xFE0E) => 2,
            0x20 or 0x22 or (>= 0x38 and <= 0x44) or 0xDD => 4,
            0x21 or 0x23 => 8,
            0x45 => Switch,
            0x27 or 0x28 or 0x29 or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or (>= 0x7B and <= 0x81) or 0x8C or 0x8D or 0x8F
                or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C => Token,
            _ => 0,
        };

        /// <summary>Adds the types a metadata entity names; false if it is a member that looks up types by name.</summary>
        bool Use(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                case HandleKind.TypeReference:
                    if (Named(handle) is { } key)
                        _uses.Add(key);
                    return true;
                case HandleKind.TypeSpecification:
                    _md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null);
                    return true;
                case HandleKind.MethodDefinition:
                    return Use(_md.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType());
                case HandleKind.FieldDefinition:
                    return Use(_md.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType());
                case HandleKind.MemberReference:
                    var member = _md.GetMemberReference((MemberReferenceHandle)handle);
                    if (member.GetKind() == MemberReferenceKind.Method)
                        member.DecodeMethodSignature(this, null);
                    else
                        member.DecodeFieldSignature(this, null);
                    if (member.Parent.Kind is HandleKind.ModuleReference)
                        return true;
                    Use(member.Parent);
                    return !(member.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition
                        && ByNameMembers.TryGetValue(FullName(member.Parent), out var members) && members.Contains(_md.GetString(member.Name)));
                case HandleKind.MethodSpecification:
                    var specification = _md.GetMethodSpecification((MethodSpecificationHandle)handle);
                    specification.DecodeSignature(this, null);
                    return Use(specification.Method);
                case HandleKind.StandaloneSignature:
                    var signature = _md.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                    if (signature.GetKind() == StandaloneSignatureKind.Method)
                        signature.DecodeMethodSignature(this, null);
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>Adds the attributes' types and the types they take as arguments; false if an attribute's arguments can't be read.</summary>
        bool Attributes(CustomAttributeHandleCollection attributes)
        {
            var known = true;
            foreach (var handle in attributes)
                known &= AttributeArguments(handle);
            return known;
        }

        bool AttributeArguments(CustomAttributeHandle handle)
        {
            var attribute = _md.GetCustomAttribute(handle);
            Use(attribute.Constructor);
            CustomAttributeValue<string?> value;
            try { value = attribute.DecodeValue(this); }
            catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidOperationException)
            {
                return false; // an enum argument of another assembly's that isn't an int, say: whatever types it names are unknown
            }
            foreach (var argument in value.FixedArguments.Concat(value.NamedArguments.Select(named => new CustomAttributeTypedArgument<string?>(named.Type, named.Value))))
                TypeArguments(argument);
            return true;
        }

        void TypeArguments(CustomAttributeTypedArgument<string?> argument)
        {
            if (argument.Value is ImmutableArray<CustomAttributeTypedArgument<string?>> elements)
            {
                foreach (var element in elements)
                    TypeArguments(element);
            }
            else if (argument.Type == SystemType && argument.Value is string serialized && serialized.StartsWith(Serialized, StringComparison.Ordinal))
                SerializedName(serialized[Serialized.Length..], _assembly, _uses);
        }

        /// <summary>
        /// Adds the types an attribute's serialized type name names ("Ns.Type+Nested`1[[Arg, Assembly, ...]], Assembly, Version=...");
        /// without an assembly, the attribute's own.
        /// </summary>
        internal static void SerializedName(string text, string defaultAssembly, HashSet<TypeKey> into)
        {
            var name = new StringBuilder();
            var depth = 0;
            var start = -1;
            var i = 0;
            for (; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    if (depth == 0)
                        name.Append(text[i + 1]);
                    i++;
                }
                else if (c == '[')
                {
                    depth++;
                    if (depth <= 2)
                        start = i + 1;
                }
                else if (c == ']')
                {
                    // A generic argument, [Name, Assembly] inside the brackets or a bare name (as [A,B]); [] and [*] are arrays.
                    if (depth == 2 && start >= 0)
                        SerializedName(text[start..i], defaultAssembly, into);
                    else if (depth == 1 && start >= 0 && text[start..i].Trim(',', '*', ' ') is { Length: > 0 } bare && !bare.Contains('['))
                    {
                        foreach (var argument in bare.Split(','))
                            SerializedName(argument.Trim(), defaultAssembly, into);
                    }
                    start = -1;
                    depth--;
                }
                else if (depth == 0 && c == ',')
                    break;
                else if (depth == 0)
                    name.Append(c);
            }
            var assembly = i < text.Length ? text[(i + 1)..].Split(',')[0].Trim() : defaultAssembly;
            var full = name.ToString().Trim().TrimEnd('&', '*');
            if (full.Length > 0)
                into.Add(new TypeKey(assembly, Outermost(full)));
        }

        /// <summary>Why code may reach a type without naming it (see TestSelection), or null.</summary>
        string? Untraceable(TypeDefinition type)
        {
            if ((type.Attributes & TypeAttributes.Interface) != 0)
                return "it is an interface";
            if ((type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == TypeAttributes.Abstract)
                return "it is abstract";
            if (!type.BaseType.IsNil && FullName(type.BaseType) is var baseType && baseType is not ("System.Object" or "System.ValueType" or "System.Enum"))
                return $"it derives from {(baseType.Length > 0 ? Display(baseType) : "a generic type")}";
            foreach (var implementation in type.GetInterfaceImplementations())
            {
                var implemented = FullName(_md.GetInterfaceImplementation(implementation).Interface);
                return $"it implements {(implemented.Length > 0 ? Display(implemented) : "a generic interface")}";
            }
            if (type.GetGenericParameters().Count > 0)
                return "it is generic";
            foreach (var handle in type.GetMethods())
            {
                var method = _md.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.Virtual) != 0)
                    return $"its method {_md.GetString(method.Name)} is virtual";
            }
            foreach (var handle in type.GetCustomAttributes())
            {
                var (ns, name) = AttributeType(handle);
                if (!InertAttributes.Contains(ns) && !InertAttributes.Contains($"{ns}.{name}"))
                    return $"it carries [{name}]";
            }
            return null;
        }

        /// <summary>An attribute's type's namespace and name.</summary>
        (string Namespace, string Name) AttributeType(CustomAttributeHandle handle)
        {
            var constructor = _md.GetCustomAttribute(handle).Constructor;
            EntityHandle type = constructor.Kind == HandleKind.MethodDefinition
                ? _md.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()
                : _md.GetMemberReference((MemberReferenceHandle)constructor).Parent;
            switch (type.Kind)
            {
                case HandleKind.TypeDefinition:
                    var definition = _md.GetTypeDefinition((TypeDefinitionHandle)type);
                    return (_md.GetString(definition.Namespace), _md.GetString(definition.Name));
                case HandleKind.TypeReference:
                    var reference = _md.GetTypeReference((TypeReferenceHandle)type);
                    return (_md.GetString(reference.Namespace), _md.GetString(reference.Name));
                default:
                    return ("", "");
            }
        }

        /// <summary>A TypeDef's or TypeRef's top-level type, in the assembly that defines it; null for one of another module's, or a TypeSpec.</summary>
        TypeKey? Named(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return new TypeKey(_assembly, Outermost(FullName(handle)));
                case HandleKind.TypeReference:
                    var reference = _md.GetTypeReference((TypeReferenceHandle)handle);
                    while (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                        reference = _md.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope);
                    var scope = reference.ResolutionScope;
                    var assembly = scope.Kind == HandleKind.AssemblyReference
                        ? _md.GetString(_md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
                        : _assembly;
                    return new TypeKey(assembly, Outermost(FullName(handle)));
                default:
                    return null;
            }
        }

        /// <summary>A TypeDef's or TypeRef's full name: a nested type's after its declaring type's and a '+'.</summary>
        string FullName(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    var definition = _md.GetTypeDefinition((TypeDefinitionHandle)handle);
                    var name = _md.GetString(definition.Name);
                    return definition.GetDeclaringType() is { IsNil: false } declaring ? $"{FullName(declaring)}+{name}"
                        : definition.Namespace.IsNil ? name : $"{_md.GetString(definition.Namespace)}.{name}";
                case HandleKind.TypeReference:
                    var reference = _md.GetTypeReference((TypeReferenceHandle)handle);
                    var referenced = _md.GetString(reference.Name);
                    return reference.ResolutionScope.Kind == HandleKind.TypeReference ? $"{FullName(reference.ResolutionScope)}+{referenced}"
                        : reference.Namespace.IsNil ? referenced : $"{_md.GetString(reference.Namespace)}.{referenced}";
                default:
                    return "";
            }
        }

        // ISignatureTypeProvider: every named type goes to the current set; the decoded value isn't used.
        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            Use(handle);
            return null;
        }

        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            Use(handle);
            return null;
        }

        public object? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            Use(handle);
            return null;
        }

        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
        public object? GetSZArrayType(object? elementType) => null;
        public object? GetArrayType(object? elementType, ArrayShape shape) => null;
        public object? GetByReferenceType(object? elementType) => null;
        public object? GetPointerType(object? elementType) => null;
        public object? GetPinnedType(object? elementType) => null;
        public object? GetGenericInstantiation(object? genericType, ImmutableArray<object?> typeArguments) => null;
        public object? GetGenericMethodParameter(object? genericContext, int index) => null;
        public object? GetGenericTypeParameter(object? genericContext, int index) => null;
        public object? GetFunctionPointerType(MethodSignature<object?> signature) => null;
        public object? GetModifiedType(object? modifier, object? unmodifiedType, bool isRequired) => null;

        // ICustomAttributeTypeProvider: a type is its full name; System.Type is SystemType, and a type argument's
        // value is its serialized name after Serialized.
        const string SystemType = "System.Type", Serialized = "serialized:";

        string? ISimpleTypeProvider<string?>.GetPrimitiveType(PrimitiveTypeCode typeCode) => "primitive:" + typeCode;
        string? ISimpleTypeProvider<string?>.GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => FullName(handle);
        string? ISimpleTypeProvider<string?>.GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => FullName(handle);
        string? ISZArrayTypeProvider<string?>.GetSZArrayType(string? elementType) => elementType + "[]";
        public string? GetSystemType() => SystemType;
        public string? GetTypeFromSerializedName(string name) => Serialized + name;
        public bool IsSystemType(string? type) => type == SystemType;

        /// <summary>An enum's underlying type: read for this assembly's enums, and taken to be int for another's (as nearly all are).</summary>
        public PrimitiveTypeCode GetUnderlyingEnumType(string? type)
        {
            var name = type?.StartsWith(Serialized, StringComparison.Ordinal) is true ? type[Serialized.Length..].Split(',')[0] : type;
            foreach (var handle in _md.TypeDefinitions)
            {
                if (FullName(handle) != name)
                    continue;
                foreach (var field in _md.GetTypeDefinition(handle).GetFields())
                {
                    var definition = _md.GetFieldDefinition(field);
                    if ((definition.Attributes & FieldAttributes.Static) == 0)
                    {
                        var blob = _md.GetBlobReader(definition.Signature);
                        blob.ReadSignatureHeader();
                        return (PrimitiveTypeCode)blob.ReadCompressedInteger();
                    }
                }
            }
            return PrimitiveTypeCode.Int32;
        }
    }

    static string Outermost(string name) => name.IndexOf('+') is var plus and > 0 ? name[..plus] : name;
}
