using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace BlazeCs;

/// <summary>What a public API change can affect in the projects that compile against it.</summary>
/// <param name="Everything">The change can reach code that never names it, so every dependent recompiles.</param>
/// <param name="Names">Otherwise: a dependent whose source mentions none of these names compiles to the same bytes.</param>
sealed record ApiChange(bool Everything, ImmutableHashSet<string> Names)
{
    public static readonly ApiChange All = new(true, []);

    public ApiChange Union(ApiChange other) =>
        Everything || other.Everything ? All : new(false, Names.Union(other.Names));
}

/// <summary>
/// A unit's surface as dependents see it: its assembly's identity and attributes, every non-private type
/// and member with everything binding can observe (signature, modifiers, constants, defaults,
/// attributes, nullability). Two surfaces differ only where a dependent's binding could differ.
/// </summary>
sealed class ApiSurface
{
    readonly string _assembly;
    readonly Dictionary<string, TypeFacts> _types = new(StringComparer.Ordinal);
    readonly Dictionary<string, MemberFacts> _members = new(StringComparer.Ordinal);

    sealed record TypeFacts(string Name, string Namespace, string Description);

    /// <param name="Name">The name source code uses to reach the member.</param>
    /// <param name="Implicit">Reachable without its name appearing in source (see <see cref="ImplicitNames"/>).</param>
    sealed record MemberFacts(string Name, string Description, bool Implicit);

    /// <summary>Members C# calls without the caller writing their names: patterns, query syntax, operators.</summary>
    static readonly HashSet<string> ImplicitNames =
    [
        "GetEnumerator", "GetAsyncEnumerator", "MoveNext", "MoveNextAsync", "Current", "Dispose", "DisposeAsync",
        "GetAwaiter", "GetResult", "IsCompleted", "OnCompleted", "UnsafeOnCompleted", "Deconstruct", "Add",
        "Select", "SelectMany", "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "GroupBy",
        "Join", "GroupJoin", "Cast", "Item", "Length", "Count", "Slice", "GetPinnableReference", "Invoke",
        "Equals", "GetHashCode", "ToString", "Finalize", "EqualityContract", "<Clone>$", "Create",
        "AppendLiteral", "AppendFormatted", "Main",
    ];

    /// <summary>Between a member key's type and member parts; type names contain "::" (global::).</summary>
    const char MemberSeparator = '\u0001';

    static readonly SymbolDisplayFormat Format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeConstantValue
            | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeContainingType,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.StaticMethod,
        parameterOptions: SymbolDisplayParameterOptions.IncludeModifiers | SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeOptionalBrackets | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword | SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeNamespaceKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public ApiSurface(Compilation compilation)
    {
        var assembly = compilation.Assembly;
        _assembly = assembly.Identity.GetDisplayName(fullKey: true) + Attributes(assembly.GetAttributes())
            + Attributes(assembly.Modules.SelectMany(m => m.GetAttributes()));
        Visit(assembly.GlobalNamespace);
    }

    void Visit(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                Visit(child);
            else if (member is INamedTypeSymbol type)
                Visit(type);
        }
    }

    void Visit(INamedTypeSymbol type)
    {
        if (type.DeclaredAccessibility == Accessibility.Private || type.IsImplicitlyDeclared && type.Name.Length == 0)
            return;

        var key = type.ToDisplayString(Format) + "`" + type.Arity;
        var description = new StringBuilder()
            .Append(type.TypeKind).Append(' ').Append(type.DeclaredAccessibility)
            .Append(type.IsStatic ? " static" : "").Append(type.IsAbstract ? " abstract" : "").Append(type.IsSealed ? " sealed" : "")
            .Append(type.IsRecord ? " record" : "").Append(type.IsRefLikeType ? " ref" : "").Append(type.IsReadOnly ? " readonly" : "")
            .Append(" : ").Append(type.BaseType?.ToDisplayString(Format))
            .Append(" [").AppendJoin(", ", type.Interfaces.Select(i => i.ToDisplayString(Format))).Append(']')
            .Append(type.EnumUnderlyingType?.ToDisplayString(Format))
            .Append(type.DelegateInvokeMethod?.ToDisplayString(Format))
            .Append(Attributes(type.GetAttributes()));
        // A struct's fields, private ones included, decide definite assignment, unmanaged-ness and layout
        // wherever the struct is used, named or not.
        if (type.IsValueType && type.TypeKind != TypeKind.Enum)
        {
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic))
                description.Append("; field ").Append(field.ToDisplayString(Format)).Append(Attributes(field.GetAttributes()));
        }
        _types[key] = new TypeFacts(type.Name, type.ContainingNamespace.ToDisplayString(Format), description.ToString());

        var inInterface = type.TypeKind == TypeKind.Interface;
        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
            {
                Visit(nested);
                continue;
            }
            if (member.DeclaredAccessibility == Accessibility.Private || member.IsImplicitlyDeclared && member is not IMethodSymbol { MethodKind: MethodKind.Constructor })
                continue;
            // Accessors and event methods are covered by their property or event.
            if (member is IMethodSymbol { AssociatedSymbol: not null })
                continue;

            var memberKey = key + MemberSeparator + member.Kind + " " + member.MetadataName + Signature(member);
            var memberDescription = member.ToDisplayString(Format) + Attributes(member.GetAttributes()) + Details(member);
            var name = member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ? type.Name : member.Name;
            // Interface and abstract members bind in implementers, which name only the type.
            var isImplicit = inInterface || member.IsAbstract
                || ImplicitNames.Contains(member.Name)
                || member is IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.ReducedExtension) }
                || member is IPropertySymbol { IsIndexer: true };
            _members[memberKey] = new MemberFacts(name, memberDescription, isImplicit);
        }
    }

    static string Signature(ISymbol member) => member switch
    {
        IMethodSymbol method => "`" + method.Arity + "(" + string.Join(",", method.Parameters.Select(p => p.RefKind + " " + p.Type.ToDisplayString(Format))) + ")",
        IPropertySymbol { IsIndexer: true } indexer => "(" + string.Join(",", indexer.Parameters.Select(p => p.Type.ToDisplayString(Format))) + ")",
        _ => "",
    };

    static string Details(ISymbol member) => member switch
    {
        IMethodSymbol method => Attributes(method.GetReturnTypeAttributes())
            + string.Concat(method.Parameters.Select(p => Attributes(p.GetAttributes())))
            + (method.IsReadOnly ? " readonly" : "") + " " + method.ReturnType.ToDisplayString(Format),
        IPropertySymbol property => " get:" + property.GetMethod?.DeclaredAccessibility + " set:" + property.SetMethod?.DeclaredAccessibility
            + (property.IsRequired ? " required" : "") + (property.SetMethod?.IsInitOnly == true ? " init" : "")
            + string.Concat(property.Parameters.Select(p => Attributes(p.GetAttributes()))),
        IFieldSymbol field => (field.IsRequired ? " required" : "") + (field.IsVolatile ? " volatile" : ""),
        _ => "",
    };

    static string Attributes(IEnumerable<AttributeData> attributes) =>
        string.Concat(attributes.Select(a => " [" + a + "]").Order(StringComparer.Ordinal));

    /// <summary>Which dependents a change from <paramref name="before"/> to this surface can affect.</summary>
    public ApiChange ChangeFrom(ApiSurface before)
    {
        if (_assembly != before._assembly)
            return ApiChange.All;

        var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var namespacesBefore = before._types.Values.Select(t => t.Namespace).ToHashSet(StringComparer.Ordinal);
        var namespacesAfter = _types.Values.Select(t => t.Namespace).ToHashSet(StringComparer.Ordinal);
        // A namespace appearing or disappearing changes how qualified names resolve everywhere.
        if (!namespacesBefore.SetEquals(namespacesAfter))
            return ApiChange.All;

        foreach (var key in _types.Keys.Union(before._types.Keys))
        {
            var now = _types.GetValueOrDefault(key);
            var then = before._types.GetValueOrDefault(key);
            if (now is not null && then is not null)
            {
                // A type's own shape (bases, interfaces, attributes, struct fields) reaches every use of the
                // type and of types derived from it, which their source may not name.
                if (now.Description != then.Description)
                    return ApiChange.All;
                continue;
            }
            var name = (now ?? then)!.Name;
            names.Add(name);
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
                names.Add(name[..^"Attribute".Length]);
        }

        foreach (var key in _members.Keys.Union(before._members.Keys))
        {
            var now = _members.GetValueOrDefault(key);
            var then = before._members.GetValueOrDefault(key);
            if (now?.Description == then?.Description)
                continue;
            // Members of an added or removed type are reached through the type's name, already listed.
            var typeKey = key[..key.IndexOf(MemberSeparator)];
            if (!(_types.ContainsKey(typeKey) && before._types.ContainsKey(typeKey)))
                continue;
            if (now?.Implicit == true || then?.Implicit == true)
                return ApiChange.All;
            names.Add((now ?? then)!.Name);
        }
        return new ApiChange(false, names.ToImmutable());
    }
}
