using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Ancify.SBM.SourceGen;

[Generator]
public class DtoConverterSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Emit the [SbmDto] attribute once
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("SbmDtoAttribute.g.cs", SourceText.From(AttributeSourceCode, Encoding.UTF8));
        });

        // 2. Gather all class/record declarations that have attribute-lists
        var dtoTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                {
                    // We only care about classes or records that have at least one AttributeList
                    return (node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0)
                        || (node is RecordDeclarationSyntax rds && rds.AttributeLists.Count > 0);
                },
                transform: static (ctx, _) =>
                {
                    if (ctx.Node is ClassDeclarationSyntax cds)
                        return ctx.SemanticModel.GetDeclaredSymbol(cds) as INamedTypeSymbol;
                    if (ctx.Node is RecordDeclarationSyntax rds)
                        return ctx.SemanticModel.GetDeclaredSymbol(rds) as INamedTypeSymbol;
                    return null;
                }
            )
            // Filter to those that actually have [SbmDto]
            .Where(static symbol => symbol is not null && HasSbmDtoAttribute(symbol!))!;

        // 3. Generate code for each discovered type
        context.RegisterSourceOutput(dtoTypes, (spc, dtoSymbol) =>
        {
            if (dtoSymbol == null)
                return; // Avoid null warnings

            string source = GenerateConverterCode(dtoSymbol);
            spc.AddSource($"{dtoSymbol.Name}_SbmDtoConverter.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    /// <summary>
    /// Checks whether an INamedTypeSymbol has [SbmDto] on it.
    /// </summary>
    private static bool HasSbmDtoAttribute(INamedTypeSymbol symbol)
    {
        return symbol.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");
    }

    /// <summary>
    /// Reads the 'ignoreCasing' bool from [SbmDto(...)].
    /// </summary>
    private static bool GetIgnoreCasing(INamedTypeSymbol symbol)
    {
        var attr = symbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");
        if (attr == null)
            return false;

        // The attribute might be (bool ignoreCasing = false, bool allowReflection = true).
        // The constructor arguments should appear in order.
        foreach (var ca in attr.ConstructorArguments)
        {
            if (ca.Kind == TypedConstantKind.Primitive && ca.Value is bool bVal)
            {
                // The first bool we find is 'ignoreCasing', second is 'allowReflection'
                return bVal;
            }
        }
        return false; // default
    }

    /// <summary>
    /// Reads the 'allowReflection' bool from [SbmDto(...)].
    /// </summary>
    private static bool GetAllowReflection(INamedTypeSymbol symbol)
    {
        var attr = symbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");
        if (attr == null)
            return true; // default

        // The second bool param in the constructor 
        // If not provided, it defaults to true
        if (attr.ConstructorArguments.Length == 2)
        {
            if (attr.ConstructorArguments[1].Kind == TypedConstantKind.Primitive &&
                attr.ConstructorArguments[1].Value is bool bVal)
            {
                return bVal;
            }
        }

        return true; // if not specified, default to true
    }

    /// <summary>
    /// Generate the entire converter code for one type T that is marked with [SbmDto].
    /// We'll produce:
    ///   - FromDictionary(IReadOnlyDictionary<object, object> data, bool ignoreCasing)
    ///   - FromMessage(Message message)
    ///   - a reflection fallback method if allowReflection=true
    ///   - a read-only wrapper named {className}ReadOnlyWrapper
    /// </summary>
    private static string GenerateConverterCode(INamedTypeSymbol dtoSymbol)
    {
        bool ignoreCasing = GetIgnoreCasing(dtoSymbol);
        bool allowReflection = GetAllowReflection(dtoSymbol);

        string ns = dtoSymbol.ContainingNamespace is { IsGlobalNamespace: false }
            ? dtoSymbol.ContainingNamespace.ToDisplayString()
            : "Ancify.SBM.Generated";

        string className = dtoSymbol.Name;

        // Detect if it's a record with no parameterless constructor
        bool isRecordWithoutCtor = dtoSymbol.IsRecord &&
            !dtoSymbol.InstanceConstructors.Any(c => c.Parameters.Length == 0);

        var sb = new StringBuilder($@"
// <auto-generated />
#nullable enable
#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8625

using System;
using System.Reflection;
using System.Collections.Generic;
using Ancify.SBM.Shared.Model.Networking;

namespace {ns}
{{
    public static partial class {className}Converter
    {{
        /// <summary>
        /// Builds a {className} from a typeless dictionary. 
        /// If 'ignoreCasing' is true, we also try 'camelCase' keys.
        /// If 'allowReflection' is false, then any sub-object that isn't also SbmDto will throw an error.
        /// </summary>
        public static {className} FromDictionary(
            IReadOnlyDictionary<object, object> data,
            bool ignoreCasing)
        {{
            if (data == null)
                throw new ArgumentNullException(nameof(data));
");

        // If it's a record without a public parameterless constructor,
        // we'll try the "best" constructor (longest public one, for instance).
        if (isRecordWithoutCtor)
        {
            var ctor = dtoSymbol.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();

            if (ctor == null)
            {
                sb.AppendLine($@"            throw new InvalidOperationException(""No public constructor found for {className}."");");
            }
            else
            {
                var parameters = ctor.Parameters;
                var argList = new List<string>();

                foreach (var param in parameters)
                {
                    string pName = param.Name;
                    var nts = param.Type as INamedTypeSymbol;
                    bool isNested = nts != null && HasSbmDtoAttribute(nts);

                    sb.AppendLine($@"
            if (!TryGetValueIgnoreCase(data, ""{pName}"", ignoreCasing, out var {pName}Value))
                throw new KeyNotFoundException(""Required constructor param '{pName}' missing."");");

                    if (isNested)
                    {
                        // Build a fully qualified call to <TypeName>Converter.FromDictionary
                        sb.AppendLine($@"
            var {pName}Typed = {GetNestedConverterFullName(nts!)}.FromDictionary(
                (System.Collections.Generic.IReadOnlyDictionary<object, object>){pName}Value,
                ignoreCasing
            );
");
                    }
                    else
                    {
                        // Fallback to ConvertValue
                        string typeOfString = GetTypeOfString(param.Type);
                        sb.AppendLine($@"
            var {pName}Typed = ({param.Type.ToDisplayString()})ConvertValue(
                {pName}Value, 
                {typeOfString}, 
                ignoreCasing, 
                {(allowReflection ? "true" : "false")}
            );
");
                    }
                    argList.Add($"{pName}Typed");
                }

                sb.AppendLine($@"
            var dto = new {className}({string.Join(", ", argList)});
");
            }
        }
        else
        {
            // If the type has a paramless ctor, do an object initializer
            sb.AppendLine($@"
            var dto = new {className}
            {{");

            var assignments = new List<string>();
            var props = dtoSymbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.SetMethod != null)
                .ToArray();

            foreach (var prop in props)
            {
                string propName = prop.Name;
                bool isRequired = prop.IsRequired;
                bool isNullable = prop.NullableAnnotation == NullableAnnotation.Annotated
                    || (prop.Type.IsValueType && prop.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

                var nts = prop.Type as INamedTypeSymbol;
                bool isNested = nts != null && HasSbmDtoAttribute(nts);

                // Build the snippet for how we set this property
                string snippet;
                if (isRequired)
                {
                    snippet = $@"
                    TryGetValueIgnoreCase(data, ""{propName}"", ignoreCasing, out var {propName}Value)
                        ? {GenConvert($"{propName}Value", prop.Type, isNested, allowReflection)}
                        : throw new KeyNotFoundException(""Required property '{propName}' not found."")";
                }
                else
                {
                    if (!isNullable)
                    {
                        snippet = $@"
                    TryGetValueIgnoreCase(data, ""{propName}"", ignoreCasing, out var {propName}Value)
                        ? {GenConvert($"{propName}Value", prop.Type, isNested, allowReflection)}
                        : throw new KeyNotFoundException(""Non-nullable property '{propName}' not found."")";
                    }
                    else
                    {
                        snippet = $@"
                    TryGetValueIgnoreCase(data, ""{propName}"", ignoreCasing, out var {propName}Value)
                        ? {GenConvert($"{propName}Value", prop.Type, isNested, allowReflection)}
                        : default";
                    }
                }

                assignments.Add($"{propName} = {snippet}");
            }

            sb.AppendLine(string.Join(",", assignments));
            sb.AppendLine("            };");
        }

        // Return the dto
        sb.AppendLine(@"
            return dto;
        }

        /// <summary>
        /// Builds a DTO from a Message by calling FromDictionary with the SbmDto ignoreCasing setting.
        /// </summary>
        public static " + className + $@" FromMessage(Message message)
        {{
            if (message == null) throw new ArgumentNullException(nameof(message));
            var data = message.AsTypeless() 
                ?? throw new ArgumentException(""Message data is null"", nameof(message));

            return FromDictionary(data, {(ignoreCasing ? "true" : "false")});
        }}

        /// <summary>
        /// Extension method to do 'message.To{className}()'
        /// </summary>
        public static {className} To{className}(this Message message)
            => FromMessage(message);

        //--------------------------------------------------------------------------------
        // Helpers
        //--------------------------------------------------------------------------------

        /// <summary>
        /// Attempt to retrieve 'key' from the dictionary. If not found, returns false. 
        /// If 'ignoreCasing' is true, also tries the camelCase version of the key.
        /// </summary>
        private static bool TryGetValueIgnoreCase(
            IReadOnlyDictionary<object, object> dict,
            string key,
            bool ignoreCasing,
            out object value)
        {{
            if (dict.TryGetValue(key, out value))
                return true;

            if (ignoreCasing)
            {{
                string cased = ToCamelCase(key);
                if (dict.TryGetValue(cased, out value))
                    return true;
            }}

            value = null!;
            return false;
        }}

        private static string ToCamelCase(string s)
        {{
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length == 1) return s.ToLowerInvariant();
            if (char.IsLower(s[0])) return s;
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }}

        /// <summary>
        /// The fallback conversion method. 
        /// If allowReflection=false, we throw if we see a sub-object that's not also [SbmDto].
        /// </summary>
        private static object ConvertValue(object rawValue, Type targetType, bool ignoreCasing, bool allowReflection)
        {{
            // 1) null => default
            if (rawValue == null)
            {{
                return targetType.IsValueType
                    ? Activator.CreateInstance(targetType)
                    : null;
            }}

            if (targetType == typeof(System.Guid) && rawValue.GetType() == typeof(string))
                return Guid.Parse((string)rawValue);

            // 2) if targetType is object or string => trivial
            if (targetType == typeof(object)) return rawValue;
            if (targetType == typeof(string)) return rawValue.ToString();

            // 3) primitives and enums
            if (targetType.IsPrimitive || targetType.IsEnum)
                return Convert.ChangeType(rawValue, targetType);

            // 4) handle Nullable<T> (value type)
            var underlying = System.Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {{
                if (rawValue == null) return null;
                return ConvertValue(rawValue, underlying, ignoreCasing, allowReflection);
            }}

            // 5) If it's a List<T> or Dictionary<K,V>, handle recursively
            if (targetType.IsGenericType)
            {{
                Type def = targetType.GetGenericTypeDefinition();
                Type[] args = targetType.GetGenericArguments();

                // List<T>
                if (def == typeof(List<>))
                {{
                    var rawEnumerable = rawValue as System.Collections.IEnumerable
                        ?? throw new InvalidCastException($""Cannot cast {{rawValue.GetType()}} to IEnumerable"");

                    var list = (System.Collections.IList)Activator.CreateInstance(targetType)!;
                    var itemType = args[0];

                    foreach (var item in rawEnumerable)
                    {{
                        var convertedItem = ConvertValue(item, itemType, ignoreCasing, allowReflection);
                        list.Add(convertedItem);
                    }}
                    return list;
                }}

                // Dictionary<K,V>
                if (def == typeof(Dictionary<,>))
                {{
                    var rawDict = rawValue as IReadOnlyDictionary<object, object>;
                    if (rawDict == null)
                    {{
                        // might be an IDictionary<object, object>
                        if (rawValue is IDictionary<object, object> idict)
                            rawDict = new {className}ReadOnlyWrapper(idict);
                        else
                            throw new InvalidCastException($""Cannot cast {{rawValue.GetType()}} to IReadOnlyDictionary<object, object>"");
                    }}

                    var dict = (System.Collections.IDictionary)Activator.CreateInstance(targetType)!;
                    Type keyT = args[0];
                    Type valT = args[1];

                    foreach (var kvp in rawDict)
                    {{
                        var k2 = ConvertValue(kvp.Key, keyT, ignoreCasing, allowReflection);
                        var v2 = ConvertValue(kvp.Value, valT, ignoreCasing, allowReflection);
                        dict[k2] = v2;
                    }}
                    return dict;
                }}
            }}

            // 6) If it's a class that's not string => either call a sub-SbmDto converter or do reflection
            if (targetType.IsClass && targetType != typeof(string))
            {{
                if (!allowReflection)
                {{
                    throw new InvalidOperationException(
                        $""Reflection is disabled, and type '{{targetType}}' is not recognized as a [SbmDto]."");
                }}

                // reflection fallback
                var dictData = rawValue as IReadOnlyDictionary<object, object>;
                if (dictData == null)
                {{
                    if (rawValue is IDictionary<object, object> id)
                        dictData = new {className}ReadOnlyWrapper(id);
                    else
                        throw new InvalidCastException($""Cannot cast {{rawValue.GetType()}} to dictionary for type {{targetType}}."");
                }}

                var instance = Activator.CreateInstance(targetType);
                if (instance == null)
                    throw new InvalidOperationException($""Failed to create instance of {{targetType}}"");

                var propInfos = targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(pi => pi.CanWrite);

                var directDict = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var x in dictData)
                {{
                    if (x.Key is string sKey)
                        directDict[sKey] = x.Value;
                }}

                Dictionary<string, object> lowerDict = null;
                if (ignoreCasing)
                {{
                    lowerDict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var x in dictData)
                    {{
                        if (x.Key is string sKey)
                        {{
                            string low = sKey.ToLowerInvariant();
                            if (!lowerDict.ContainsKey(low))
                                lowerDict[low] = x.Value;
                        }}
                    }}
                }}

                foreach (var pi in propInfos)
                {{
                    if (directDict.TryGetValue(pi.Name, out var valObj))
                    {{
                        var convVal = ConvertValue(valObj, pi.PropertyType, ignoreCasing, allowReflection);
                        pi.SetValue(instance, convVal);
                        continue;
                    }}
                    if (ignoreCasing && lowerDict != null)
                    {{
                        var lowName = pi.Name.ToLowerInvariant();
                        if (lowerDict.TryGetValue(lowName, out var val2))
                        {{
                            var conv2 = ConvertValue(val2, pi.PropertyType, ignoreCasing, allowReflection);
                            pi.SetValue(instance, conv2);
                            continue;
                        }}
                    }}
                }}

                return instance;
            }}

            // final fallback => Convert.ChangeType
            return Convert.ChangeType(rawValue, targetType);
        }}

        /// <summary>
        /// A uniquely named read-only wrapper class for 'Dictionary<object, object>' 
        /// so we don't conflict with other generated wrappers.
        /// </summary>
        class {className}ReadOnlyWrapper : IReadOnlyDictionary<object, object>
        {{
            private readonly IDictionary<object, object> _dict;

            public {className}ReadOnlyWrapper(IDictionary<object, object> dict)
            {{
                _dict = dict ?? throw new ArgumentNullException(nameof(dict));
            }}

            public object this[object key] => _dict[key];
            public IEnumerable<object> Keys => _dict.Keys;
            public IEnumerable<object> Values => _dict.Values;
            public int Count => _dict.Count;

            public bool ContainsKey(object key) => _dict.ContainsKey(key);
            public bool TryGetValue(object key, out object value) => _dict.TryGetValue(key, out value);

            public IEnumerator<KeyValuePair<object, object>> GetEnumerator()
            {{
                foreach (var kvp in _dict)
                    yield return new KeyValuePair<object, object>(kvp.Key, kvp.Value);
            }}

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }}
    }}
}}
");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the snippet for either:
    ///   - nestedConverter.FromDictionary(...)
    ///   - or (SomeType) ConvertValue(...).
    /// </summary>
    private static string GenConvert(
        string localName,
        ITypeSymbol propType,
        bool isNested,
        bool allowReflection)
    {
        if (!isNested)
        {
            // fallback to ConvertValue
            string typeOfString = GetTypeOfString(propType);
            return $@"({propType.ToDisplayString()})ConvertValue({localName}, {typeOfString}, ignoreCasing, {(allowReflection ? "true" : "false")})";
        }
        else
        {
            // call the fully-qualified <PropType>Converter.FromDictionary(...)
            return $@"
{GetNestedConverterFullName(propType)}.FromDictionary(
    (System.Collections.Generic.IReadOnlyDictionary<object, object>) {localName},
    ignoreCasing
)";
        }
    }

    /// <summary>
    /// For a nested [SbmDto] type, produce the fully-qualified converter name:
    /// e.g., "Ancify.Coordinator.API.Services2.Dtos.Deployment.ServerDataConverter"
    /// </summary>
    private static string GetNestedConverterFullName(ITypeSymbol typeSymbol)
    {
        // The top-level namespace might be global or empty if there's no namespace.
        string ns = typeSymbol.ContainingNamespace?.IsGlobalNamespace == true
            ? ""
            : typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";

        // The short name might have a trailing '?' if it's nullable
        string baseName = typeSymbol.Name.EndsWith("?")
            ? typeSymbol.Name.Substring(0, typeSymbol.Name.Length - 1)
            : typeSymbol.Name;

        // If there's no namespace, just do "ServerDataConverter"
        if (string.IsNullOrEmpty(ns))
            return baseName + "Converter";

        // Otherwise "Ancify.Coordinator.API.Services2.Dtos.Deployment.ServerDataConverter"
        return ns + "." + baseName + "Converter";
    }

    /// <summary>
    /// If the property type is "Foo.BarType", we produce "typeof(Foo.BarType)". 
    /// If there's a trailing '?', we remove it. 
    /// </summary>
    private static string GetTypeOfString(ITypeSymbol typeSymbol)
    {
        bool isValueType = typeSymbol.IsValueType;
        bool isNullableRef = (typeSymbol.NullableAnnotation == NullableAnnotation.Annotated) && !isValueType;

        string text = typeSymbol.ToDisplayString();
        if (isNullableRef && text.EndsWith("?"))
        {
            text = text.Substring(0, text.Length - 1); // remove trailing '?'
        }
        return $"typeof({text})";
    }

    /// <summary>
    /// We do NOT include '| AttributeTargets.Record' because older frameworks 
    /// may not know about 'Record'. 'class' alone is enough for records.
    /// Also we add bool AllowReflection param with default=true.
    /// </summary>
    private const string AttributeSourceCode = @"
// <auto-generated />
using System;

namespace Ancify.SBM.Generated
{
    /// <summary>
    /// Apply [SbmDto] to classes (and records) to have the DtoConverterSourceGenerator 
    /// generate code for them.
    /// 
    /// Set ignoreCasing=true to allow partial case-insensitive matching 
    /// (including PascalCase => camelCase).
    /// 
    /// Set allowReflection=false to throw instead of reflection fallback for sub-objects 
    /// that are not also marked as SbmDto.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class SbmDtoAttribute : Attribute
    {
        public bool IgnoreCasing { get; }
        public bool AllowReflection { get; }

        public SbmDtoAttribute(bool ignoreCasing = false, bool allowReflection = true)
        {
            IgnoreCasing = ignoreCasing;
            AllowReflection = allowReflection;
        }
    }
}
";
}
