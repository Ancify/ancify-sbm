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
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource("SbmDtoConverterAttribute.g.cs", SourceText.From(AttributeSourceCode, Encoding.UTF8));
        });

        IncrementalValuesProvider<INamedTypeSymbol> dtoTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                // Support both class and record declarations
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
                })
            .Where(static symbol => symbol is not null && HasDtoConverterAttribute(symbol!))!;

        context.RegisterSourceOutput(dtoTypes, (spc, dtoSymbol) =>
        {
            var source = GenerateConverterCode(dtoSymbol);
            spc.AddSource($"{dtoSymbol.Name}_SbmDtoConverter.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    private static bool HasDtoConverterAttribute(INamedTypeSymbol symbol) =>
        symbol.GetAttributes().Any(ad => ad.AttributeClass?.ToDisplayString() == "Ancify.SBM.Generated.SbmDtoAttribute");

    private static string GenerateConverterCode(INamedTypeSymbol dtoSymbol)
    {
        var namespaceName = dtoSymbol.ContainingNamespace.IsGlobalNamespace
            ? "Ancify.SBM.Generated"
            : dtoSymbol.ContainingNamespace.ToDisplayString();
        var className = dtoSymbol.Name;

        var builder = new StringBuilder($@"
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

            var data = message.AsTypeless() ?? throw new ArgumentException(""Message data is null"", nameof(message));
");

        // Check if this is a record without a parameterless constructor:
        bool isRecordWithoutParameterlessCtor = dtoSymbol.IsRecord
            && !dtoSymbol.InstanceConstructors.Any(c => c.Parameters.Length == 0);

        if (isRecordWithoutParameterlessCtor)
        {
            // Use the constructor-based approach (e.g. positional record).
            var primaryCtor = dtoSymbol.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();

            if (primaryCtor == null)
            {
                builder.AppendLine($@"            throw new InvalidOperationException(""No accessible constructor found for {className}."");");
            }
            else
            {
                var parameters = primaryCtor.Parameters;
                var arguments = new List<string>();

                foreach (var param in parameters)
                {
                    var paramName = param.Name;
                    var paramType = param.Type.ToDisplayString();

                    // We'll require all constructor parameters to be present:
                    builder.AppendLine($@"
            if (!data.TryGetValue(""{paramName}"", out var {paramName}Value))
                throw new KeyNotFoundException(""Property '{paramName}' not found in message data."");");

                    arguments.Add($@"({paramType})Convert.ChangeType({paramName}Value, typeof({paramType}))");
                }

                builder.AppendLine($@"
            var dto = new {className}({string.Join(", ", arguments)});
");
            }
        }
        else
        {
            // Class or record with a parameterless constructor -> object-initializer approach.
            // Build up lines for each property inside the initializer.
            var properties = dtoSymbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.SetMethod != null) // must be settable
                .ToArray();

            // Step 1: For each property, we may need an earlier line to handle "throw if missing"
            // or a data.TryGetValue(...) call for the object-initializer assignment.
            //
            // Step 2: We'll store property assignments in a list, then join them into
            // the object initializer.
            //
            // We'll do:
            //   var dto = new MyClass {
            //       Foo = (FooType)Convert.ChangeType(FooValue, typeof(FooType)),
            //       Bar = data.TryGetValue("Bar", out var barValue) ? ... : ...
            //   };

            var preChecks = new List<string>();
            var assignments = new List<string>();

            foreach (var prop in properties)
            {
                string propName = prop.Name;
                string propType = prop.Type.ToDisplayString();

                bool isRequired = prop.IsRequired; // C# 11's IsRequired
                bool isNullable = prop.NullableAnnotation == NullableAnnotation.Annotated
                                  // For value types, we should check .IsNullableType
                                  || (prop.Type.IsValueType && prop.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

                if (isRequired)
                {
                    // Required => we must throw if missing
                    preChecks.Add($@"
            if (!data.TryGetValue(""{propName}"", out var {propName}Value))
                throw new KeyNotFoundException(""Required property '{propName}' not found in message data."");");

                    // Then we can do: {propName} = (PropType) Convert.ChangeType({propName}Value, typeof(PropType))
                    assignments.Add($@"{propName} = ({propType})Convert.ChangeType({propName}Value, typeof({propType}))");
                }
                else
                {
                    // Not required:
                    // Decide how to handle non-nullable vs nullable
                    if (!isNullable)
                    {
                        // Non-nullable property that is not 'required'.
                        // Typically you either (1) throw if missing, or 
                        // (2) default to 'default(...)' if you want it optional.
                        // Let's assume we do throw if missing for non-nullable (though you can revise).
                        preChecks.Add($@"
            if (!data.TryGetValue(""{propName}"", out var {propName}Value))
                throw new KeyNotFoundException(""Non-nullable property '{propName}' not found in message data."");");

                        assignments.Add($@"{propName} = ({propType})Convert.ChangeType({propName}Value, typeof({propType}))");
                    }
                    else
                    {
                        // Nullable property => if missing, treat it as null / default
                        assignments.Add($@"{propName} = data.TryGetValue(""{propName}"", out var {propName}Value) 
                ? ({propType})Convert.ChangeType({propName}Value, typeof({propType})) 
                : default");
                    }
                }
            }

            // Emit any required pre-check lines:
            foreach (var line in preChecks)
            {
                builder.AppendLine(line);
            }

            // Now build up the object initializer:
            builder.AppendLine($@"
            var dto = new {className}
            {{");

            for (int i = 0; i < assignments.Count; i++)
            {
                string suffix = (i == assignments.Count - 1) ? string.Empty : ",";
                builder.AppendLine($"                {assignments[i]}{suffix}");
            }

            builder.AppendLine($@"            }};
");
        }

        // Return statement
        builder.AppendLine($@"
            return dto;
        }}

        public static {className} To{className}(this Message message) => FromMessage(message);
    }}
}}");

        return builder.ToString();
    }

    private const string AttributeSourceCode = @"
using System;

namespace Ancify.SBM.Generated
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class SbmDtoAttribute : Attribute 
    { 
    }
}";
}
