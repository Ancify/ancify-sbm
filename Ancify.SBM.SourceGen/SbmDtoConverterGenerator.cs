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
        // 1. Emit the attribute source
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource("SbmDtoConverterAttribute.g.cs", SourceText.From(AttributeSourceCode, Encoding.UTF8));
        });

        // 2. Gather all class/record declarations that have attributes, 
        //    filter to those with [SbmDto(...)].
        var dtoTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                {
                    return (node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0)
                        || (node is RecordDeclarationSyntax rds && rds.AttributeLists.Count > 0);
                },
                transform: static (syntaxContext, _) =>
                {
                    if (syntaxContext.Node is ClassDeclarationSyntax cds)
                        return syntaxContext.SemanticModel.GetDeclaredSymbol(cds) as INamedTypeSymbol;
                    if (syntaxContext.Node is RecordDeclarationSyntax rds)
                        return syntaxContext.SemanticModel.GetDeclaredSymbol(rds) as INamedTypeSymbol;
                    return null;
                }
            )
            .Where(static symbol => symbol is not null && HasDtoConverterAttribute(symbol!))!;

        // 3. For each discovered symbol, generate the converter code
        context.RegisterSourceOutput(dtoTypes, (spc, dtoSymbol) =>
        {
            var source = GenerateConverterCode(dtoSymbol);
            spc.AddSource($"{dtoSymbol.Name}_SbmDtoConverter.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    /// <summary>
    /// Check if the type has [SbmDtoAttribute].
    /// </summary>
    private static bool HasDtoConverterAttribute(INamedTypeSymbol symbol) =>
        symbol.GetAttributes().Any(ad => ad.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");

    /// <summary>
    /// Read the 'ignoreCasing' flag from SbmDtoAttribute (bool ignoreCasing = false).
    /// </summary>
    private static bool GetIgnoreCasing(INamedTypeSymbol symbol)
    {
        var attr = symbol.GetAttributes().FirstOrDefault(ad =>
            ad.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");
        if (attr == null)
            return false;

        foreach (var ca in attr.ConstructorArguments)
        {
            if (ca.Kind == TypedConstantKind.Primitive && ca.Value is bool bVal)
                return bVal;
        }
        return false;
    }

    private static string GenerateConverterCode(INamedTypeSymbol dtoSymbol)
    {
        // Determine target namespace
        var namespaceName = dtoSymbol.ContainingNamespace is { IsGlobalNamespace: false }
            ? dtoSymbol.ContainingNamespace.ToDisplayString()
            : "Ancify.SBM.Generated";

        var className = dtoSymbol.Name;
        bool ignoreCasing = GetIgnoreCasing(dtoSymbol);

        var sb = new StringBuilder($@"
using System;
using System.Collections.Generic;
using Ancify.SBM.Shared.Model.Networking;

namespace {namespaceName}
{{
    public static partial class {className}Converter
    {{
        public static {className} FromMessage(Message message)
        {{
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var data = message.AsTypeless() 
                ?? throw new ArgumentException(""Message data is null"", nameof(message));
");

        // Detect if record with no parameterless constructor => constructor-based approach
        bool isRecordWithoutParamlessCtor = dtoSymbol.IsRecord
            && !dtoSymbol.InstanceConstructors.Any(c => c.Parameters.Length == 0);

        if (isRecordWithoutParamlessCtor)
        {
            // For a positional record, find its primary/public constructor
            var primaryCtor = dtoSymbol.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();

            if (primaryCtor == null)
            {
                sb.AppendLine($@"            throw new InvalidOperationException(""No accessible constructor found for {className}."");");
            }
            else
            {
                var parameters = primaryCtor.Parameters;
                var argList = new List<string>();
                foreach (var p in parameters)
                {
                    string paramName = p.Name;
                    string paramType = p.Type.ToDisplayString();

                    sb.AppendLine($@"
            if (!TryGetValueIgnoreCase(data, ""{paramName}"", {(ignoreCasing ? "true" : "false")}, out var {paramName}Value))
                throw new KeyNotFoundException(""Property '{paramName}' not found in message data."");

            var {paramName}Typed = ({paramType})ConvertValue({paramName}Value, typeof({paramType}));
");
                    argList.Add(paramName + "Typed");
                }

                sb.AppendLine($@"
            var dto = new {className}({string.Join(", ", argList)});
");
            }
        }
        else
        {
            // Class or record with parameterless constructor => object initializer approach
            var settableProps = dtoSymbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.SetMethod is not null)
                .ToArray();

            sb.AppendLine($@"
            var dto = new {className}
            {{");

            var assignments = new List<string>();

            foreach (var prop in settableProps)
            {
                string propName = prop.Name;
                string propType = prop.Type.ToDisplayString();
                bool isRequired = prop.IsRequired;
                bool isNullable = prop.NullableAnnotation == NullableAnnotation.Annotated
                    || (prop.Type.IsValueType && prop.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

                if (isRequired)
                {
                    assignments.Add($@"
                {propName} =
                    TryGetValueIgnoreCase(data, ""{propName}"", {(ignoreCasing ? "true" : "false")}, out var {propName}Value)
                        ? ({propType})ConvertValue({propName}Value, typeof({propType}))
                        : throw new KeyNotFoundException(""Required property '{propName}' not found in message data."")");
                }
                else
                {
                    if (!isNullable)
                    {
                        // Non-nullable but not required => throw if missing (adjust as desired)
                        assignments.Add($@"
                {propName} =
                    TryGetValueIgnoreCase(data, ""{propName}"", {(ignoreCasing ? "true" : "false")}, out var {propName}Value)
                        ? ({propType})ConvertValue({propName}Value, typeof({propType}))
                        : throw new KeyNotFoundException(""Non-nullable property '{propName}' not found in message data."")");
                    }
                    else
                    {
                        // Nullable => if missing, default to null/zero
                        assignments.Add($@"
                {propName} =
                    TryGetValueIgnoreCase(data, ""{propName}"", {(ignoreCasing ? "true" : "false")}, out var {propName}Value)
                        ? ({propType})ConvertValue({propName}Value, typeof({propType}))
                        : default");
                    }
                }
            }

            sb.AppendLine(string.Join(",", assignments));
            sb.AppendLine($@"
            }};
");
        }

        sb.AppendLine($@"
            return dto;
        }}

        public static {className} To{className}(this Message message) => FromMessage(message);

        /// <summary>
        /// A helper used by the generated code to do 
        /// case-sensitive or optional case-insensitive lookups.
        /// Also tries the 'camelCase' variant of the property if ignoreCasing is true.
        /// </summary>
        private static bool TryGetValueIgnoreCase(
            IReadOnlyDictionary<object, object> data,
            string propName,
            bool ignoreCasing,
            out object value)
        {{
            // 1) Exact match
            if (data.TryGetValue(propName, out value))
                return true;

            if (ignoreCasing)
            {{
                // 2) Try 'camelCase' => e.g. 'DeploymentId' -> 'deploymentId'
                string camel = ToCamelCase(propName);
                if (data.TryGetValue(camel, out value))
                    return true;
            }}

            value = null;
            return false;
        }}

        /// <summary>
        /// ConvertValue: Recursively convert a typeless object into the desired targetType.
        /// Handles primitive types, strings, enums, Nullable&lt;T&gt;, List&lt;T&gt;, 
        /// Dictionary&lt;K,V&gt; and a fallback to Convert.ChangeType for any others.
        /// </summary>
        private static object ConvertValue(object rawValue, Type targetType)
        {{
            // 1) null => return default(T) if value type, else null
            if (rawValue == null)
            {{
                return targetType.IsValueType
                    ? Activator.CreateInstance(targetType)
                    : null;
            }}

            // If the targetType itself is an object, we might short-circuit:
            if (targetType == typeof(object))
                return rawValue;

            // 2) If target is string
            if (targetType == typeof(string))
            {{
                return rawValue.ToString();
            }}

            // 3) If target is a primitive or enum
            if (targetType.IsPrimitive || targetType.IsEnum)
            {{
                return Convert.ChangeType(rawValue, targetType);
            }}

            // 4) Handle Nullable<T>
            if (Nullable.GetUnderlyingType(targetType) is Type underlying)
            {{
                // if rawValue is null, it's just null
                if (rawValue == null)
                    return null;
                return ConvertValue(rawValue, underlying);
            }}

            // 5) If target is a generic type: List<T>, Dictionary<K,V>, etc.
            if (targetType.IsGenericType)
            {{
                Type genericDef = targetType.GetGenericTypeDefinition();
                Type[] genericArgs = targetType.GetGenericArguments();

                // 5a) List<T>
                if (genericDef == typeof(List<>))
                {{
                    // cast rawValue to something enumerable
                    var rawEnumerable = rawValue as System.Collections.IEnumerable;
                    if (rawEnumerable == null)
                    {{
                        throw new InvalidCastException($""Cannot cast {{rawValue.GetType()}} to IEnumerable to convert to {{targetType}}."");
                    }}

                    // Create a List<T>
                    var list = (System.Collections.IList)Activator.CreateInstance(targetType);
                    Type itemType = genericArgs[0];

                    foreach (var item in rawEnumerable)
                    {{
                        var convertedItem = ConvertValue(item, itemType);
                        list.Add(convertedItem);
                    }}
                    return list;
                }}

                // 5b) Dictionary<K, V>
                if (genericDef == typeof(Dictionary<,>))
                {{
                    var rawDict = rawValue as IReadOnlyDictionary<object, object>;
                    if (rawDict == null)
                    {{
                        // We might have a normal Dictionary<object, object>, so let's also check that
                        if (rawValue is IDictionary<object, object> normalDict)
                        {{
                            // We'll wrap it in a read-only dictionary if we want
                            rawDict = new ReadOnlyWrapper(normalDict);
                        }}
                        else
                        {{
                            throw new InvalidCastException($""Cannot cast {{rawValue.GetType()}} to IReadOnlyDictionary<object, object> to convert to {{targetType}}."");
                        }}
                    }}

                    var dict = (System.Collections.IDictionary)Activator.CreateInstance(targetType);
                    Type keyType = genericArgs[0];
                    Type valType = genericArgs[1];

                    foreach (var kvp in rawDict)
                    {{
                        var convertedKey = ConvertValue(kvp.Key, keyType);
                        var convertedVal = ConvertValue(kvp.Value, valType);
                        dict[convertedKey] = convertedVal;
                    }}
                    return dict;
                }}
            }}

            // 6) Fallback to Convert.ChangeType
            return Convert.ChangeType(rawValue, targetType);
        }}

        // Simple read-only wrapper if needed
        private class ReadOnlyWrapper : IReadOnlyDictionary<object, object>
        {{
            private readonly IDictionary<object, object> _dict;
            public ReadOnlyWrapper(IDictionary<object, object> dict) => _dict = dict;
            public object this[object key] => _dict[key];
            public IEnumerable<object> Keys => _dict.Keys;
            public IEnumerable<object> Values => _dict.Values;
            public int Count => _dict.Count;
            public bool ContainsKey(object key) => _dict.ContainsKey(key);
            public IEnumerator<KeyValuePair<object, object>> GetEnumerator() => _dict.GetEnumerator()
                as IEnumerator<KeyValuePair<object, object>>;
            public bool TryGetValue(object key, out object value) => _dict.TryGetValue(key, out value);
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _dict.GetEnumerator();
        }}

        private static string ToCamelCase(string pascal)
        {{
            if (string.IsNullOrEmpty(pascal))
                return pascal;

            if (pascal.Length == 1)
                return pascal.ToLowerInvariant();

            if (char.IsLower(pascal[0]))
                return pascal;

            return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        }}
    }}
}}
");

        return sb.ToString();
    }

    /// <summary>
    /// Source for the [SbmDto] attribute, supporting 'ignoreCasing'.
    /// </summary>
    private const string AttributeSourceCode = @"
using System;

namespace Ancify.SBM.Generated
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class SbmDtoAttribute : Attribute
    {
        public bool IgnoreCasing { get; }

        public SbmDtoAttribute(bool ignoreCasing = false)
        {
            IgnoreCasing = ignoreCasing;
        }
    }
}
";
}
