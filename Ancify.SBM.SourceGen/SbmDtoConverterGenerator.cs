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
                    if (node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0)
                        return true;
                    if (node is RecordDeclarationSyntax rds && rds.AttributeLists.Count > 0)
                        return true;
                    return false;
                },
                transform: static (syntaxContext, _) =>
                {
                    if (syntaxContext.Node is ClassDeclarationSyntax cds)
                        return syntaxContext.SemanticModel.GetDeclaredSymbol(cds) as INamedTypeSymbol;
                    if (syntaxContext.Node is RecordDeclarationSyntax rds)
                        return syntaxContext.SemanticModel.GetDeclaredSymbol(rds) as INamedTypeSymbol;
                    return null;
                })
            .Where(static symbol => symbol != null && HasDtoConverterAttribute(symbol!))!;

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

        // For records without a parameterless constructor, use the primary constructor.
        bool isRecordWithoutParameterlessCtor = dtoSymbol.IsRecord && !dtoSymbol.InstanceConstructors.Any(c => c.Parameters.Length == 0);
        if (isRecordWithoutParameterlessCtor)
        {
            // Pick a public constructor—usually the primary constructor of a positional record.
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
                    builder.AppendLine($@"
            if (!data.TryGetValue(""{paramName}"", out var {paramName}Value))
                throw new KeyNotFoundException(""Property '{paramName}' not found"");");
                    arguments.Add($"({paramType})Convert.ChangeType({paramName}Value, typeof({paramType}))");
                }
                builder.AppendLine($@"
            var dto = new {className}({string.Join(", ", arguments)});
");
            }
        }
        else
        {
            // For classes (and records with a parameterless constructor or mutable init properties)
            builder.AppendLine($@"            var dto = new {className}();");

            foreach (var member in dtoSymbol.GetMembers().OfType<IPropertySymbol>().Where(p => p.SetMethod != null))
            {
                var propName = member.Name;
                var propType = member.Type.ToDisplayString();
                builder.AppendLine($@"
            if (!data.TryGetValue(""{propName}"", out var {propName}Value))
                throw new KeyNotFoundException(""Property '{propName}' not found"");
            dto.{propName} = ({propType})Convert.ChangeType({propName}Value, typeof({propType}));");
            }
        }

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
    public sealed class SbmDtoAttribute : Attribute { }
}";
}
