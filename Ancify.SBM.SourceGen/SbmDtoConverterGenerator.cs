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
        // 1. Emit the attribute source at compile-time 
        //    so your user code can do: [SbmDto(ignoreCasing: true)]
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource("SbmDtoConverterAttribute.g.cs", SourceText.From(AttributeSourceCode, Encoding.UTF8));
        });

        // 2. Gather all class/record declarations that have at least one attribute list
        //    then filter down to those with [SbmDto(...)] on them.
        IncrementalValuesProvider<INamedTypeSymbol> dtoTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                {
                    // We are interested in class or record declarations that have attributes
                    return (node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0)
                        || (node is RecordDeclarationSyntax rds && rds.AttributeLists.Count > 0);
                },
                transform: static (syntaxContext, _) =>
                {
                    // Turn the syntax node into a symbol 
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
    /// Minimal check that the type has [SbmDtoAttribute].
    /// </summary>
    private static bool HasDtoConverterAttribute(INamedTypeSymbol symbol) =>
        symbol.GetAttributes().Any(ad => ad.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");

    /// <summary>
    /// Read the 'ignoreCasing' flag from the SbmDtoAttribute, defaulting to false if not found.
    /// </summary>
    private static bool GetIgnoreCasing(INamedTypeSymbol symbol)
    {
        // Grab the [SbmDto(...)] attribute
        var attr = symbol.GetAttributes().FirstOrDefault(ad =>
            ad.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");

        if (attr == null)
            return false;

        // Our attribute constructor has a single bool parameter: SbmDtoAttribute(bool ignoreCasing = false).
        // So we can read it from attr.ConstructorArguments if present
        foreach (var ca in attr.ConstructorArguments)
        {
            if (ca.Kind == TypedConstantKind.Primitive && ca.Value is bool bVal)
                return bVal;
        }

        return false;
    }

    private static string GenerateConverterCode(INamedTypeSymbol dtoSymbol)
    {
        // Determine which namespace to place the generated converter in
        var namespaceName = dtoSymbol.ContainingNamespace is { IsGlobalNamespace: false }
            ? dtoSymbol.ContainingNamespace.ToDisplayString()
            : "Ancify.SBM.Generated";

        var className = dtoSymbol.Name;
        var ignoreCasing = GetIgnoreCasing(dtoSymbol);

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

            // Expecting message.AsTypeless() to return IReadOnlyDictionary<object, object>
            var data = message.AsTypeless() 
                ?? throw new ArgumentException(""Message data is null"", nameof(message));
");

        // Detect if this is a record with no parameterless constructor => use constructor-based approach
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
                    var paramName = p.Name;
                    var paramType = p.Type.ToDisplayString();

                    sb.AppendLine($@"
            if (!TryGetValueIgnoreCase(data, ""{paramName}"", {(ignoreCasing ? "true" : "false")}, out var {paramName}Value))
                throw new KeyNotFoundException(""Property '{paramName}' not found in message data."");

            // Convert to the correct type
            var {paramName}Typed = ({paramType})Convert.ChangeType({paramName}Value, typeof({paramType}));
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
            // For a class or a record with a parameterless constructor, use an object-initializer
            // so that we properly set required properties and avoid the C# 11 "required" error.

            // Build up property assignments.
            var settableProps = dtoSymbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.SetMethod is not null)
                .ToArray();

            // Begin object creation
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
                    // For required properties, throw if missing
                    assignments.Add($@"
                {propName} = 
                    TryGetValueIgnoreCase(data, ""{propName}"", {(ignoreCasing ? "true" : "false")}, out var {propName}Value)
                        ? ({propType})Convert.ChangeType({propName}Value, typeof({propType}))
                        : throw new KeyNotFoundException(""Required property '{propName}' not found in message data."")");
                }
                else
                {
                    if (!isNullable)
                    {
                        // Non-nullable but not required => throw if missing (change this logic as desired)
                        assignments.Add($@"
                {propName} = 
                    TryGetValueIgnoreCase(data, ""{propName}"", {(ignoreCasing ? "true" : "false")}, out var {propName}Value)
                        ? ({propType})Convert.ChangeType({propName}Value, typeof({propType}))
                        : throw new KeyNotFoundException(""Non-nullable property '{propName}' not found in message data."")");
                    }
                    else
                    {
                        // Nullable => if missing, default to null/zero 
                        assignments.Add($@"
                {propName} = 
                    TryGetValueIgnoreCase(data, ""{propName}"", {(ignoreCasing ? "true" : "false")}, out var {propName}Value)
                        ? ({propType})Convert.ChangeType({propName}Value, typeof({propType}))
                        : default");
                    }
                }
            }

            sb.AppendLine(string.Join(",", assignments));
            sb.AppendLine($@"
            }};
");
        }

        // Return dto
        sb.AppendLine($@"
            return dto;
        }}

        public static {className} To{className}(this Message message) => FromMessage(message);

        /// <summary>
        /// A helper used by the generated code to do case-sensitive or optional case-insensitive lookups.
        /// Also tries the 'camelCase' variant of the property if ignoreCasing is true.
        /// </summary>
        private static bool TryGetValueIgnoreCase(
            IReadOnlyDictionary<object, object> data, 
            string propName, 
            bool ignoreCasing,
            out object value)
        {{
            // 1) Exact match using the property name (as string)
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
    /// This is the entire source code for the attribute, including the new bool 'IgnoreCasing' property.
    /// It is added via RegisterPostInitializationOutput so that user code sees [SbmDto(ignoreCasing: ...)].
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
