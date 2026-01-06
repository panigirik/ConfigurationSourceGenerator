using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Netway.AuditableEntity.SourceGenerator;

[Generator]
public class AuditEntityConfigurationGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
        context.RegisterForSyntaxNotifications(() => new ConfSyntaxReciever());
    }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.SyntaxReceiver is not ConfSyntaxReciever receiver)
            return;

        var compilation = context.Compilation;
        bool auditBaseGenerated = false;

        foreach (var classSyntax in receiver.ConfigClasses)
        {
            var model = compilation.GetSemanticModel(classSyntax.SyntaxTree);
            if (model.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol configSymbol)
                continue;

            if (!configSymbol.GetAttributes().Any(attr =>
                attr.AttributeClass?.Name is "GenerateConfigurationAttribute" or "GenerateConfiguration"))
                continue;

            var configureMethod = configSymbol.GetMembers()
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == "Configure");

            var builderParam = configureMethod?.Parameters
                .FirstOrDefault(p => p.Type.Name.StartsWith("EntityTypeBuilder"));

            var entityType = (builderParam?.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
            if (entityType == null)
                continue;

            var entityName = entityType.Name;
            var configNamespace = configSymbol.ContainingNamespace.ToDisplayString();
            var entityNamespace = entityType.ContainingNamespace.ToDisplayString();

            var props = entityType.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public)
                .ToList();

            var auditProps = props.Where(p => p.Name is "CreatedAt" or "UpdatedAt" or "CreatedBy" or "ModifiedBy").ToList();
            var otherProps = props.Except(auditProps).Where(p => IsSimpleType(p.Type)).ToList();

            // Генерируем базу один раз
            if (!auditBaseGenerated)
            {
                var auditSource = GenerateAuditBase(configNamespace, context);
                context.AddSource($"AuditEntityConfiguration.g.cs", SourceText.From(auditSource, Encoding.UTF8));
                auditBaseGenerated = true;
            }

            var configSource = GenerateEntityConfig(entityName, configNamespace, entityNamespace, otherProps);
            context.AddSource($"{entityName}CommonConfiguration.g.cs", SourceText.From(configSource, Encoding.UTF8));
        }
    }

    private static bool IsSimpleType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
            return true;

        if (type.ToDisplayString() == "System.Guid")
            return true;
        
        return type.SpecialType switch
        {
            SpecialType.System_String => true,
            SpecialType.System_DateTime => true,
            SpecialType.System_Double => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_Boolean => true,
            _ => false
        };
    }


private string GenerateAuditBase(string configNs, GeneratorExecutionContext context)
{
    var auditFile = context.AdditionalFiles.FirstOrDefault(f =>
        Path.GetFileName(f.Path).Equals("AuditFields.txt", StringComparison.OrdinalIgnoreCase));

    string[] lines = Array.Empty<string>();
    if (auditFile != null)
    {
        var text = auditFile.GetText(context.CancellationToken);
        lines = text?.Lines.Select(l => l.ToString()).ToArray() ?? Array.Empty<string>();
    }

    var builder = new StringBuilder($@"
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace {configNs}
{{
    public abstract class AuditEntityConfiguration<T> : IEntityTypeConfiguration<T> where T : class
    {{
        public virtual void Configure(EntityTypeBuilder<T> builder)
        {{
");

    foreach (var line in lines)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains(':'))
            continue;

        var parts = line.Split(':');
        if (parts.Length != 2)
            continue;

        var type = parts[0].Trim();
        var name = parts[1].Trim();
        var columnName = name;

        var propertyLine = new StringBuilder($@"            builder.Property(""{name}"")");

        switch (type.ToLowerInvariant())
        {
            case "datetime":
                propertyLine.Append(@".HasColumnType(""timestamp"")");
                break;
            case "string":
                propertyLine.Append(".HasMaxLength(255)");
                break;
            case "int":
                propertyLine.Append(@".HasColumnType(""integer"")");
                break;
            case "bool":
                propertyLine.Append(@".HasColumnType(""boolean"")");
                break;
        }

        propertyLine.Append($@".HasColumnName(""{ToSnakeCase(columnName)}"");");
        builder.AppendLine(propertyLine.ToString());
    }

    builder.AppendLine("        }");
    builder.AppendLine("    }");
    builder.AppendLine("}");

    return builder.ToString();
}

private static string ToSnakeCase(string input)
{
    if (string.IsNullOrWhiteSpace(input))
        return input;

    var builder = new StringBuilder();
    for (int i = 0; i < input.Length; i++)
    {
        if (char.IsUpper(input[i]))
        {
            if (i > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(input[i]));
        }
        else
        {
            builder.Append(input[i]);
        }
    }

    return builder.ToString();
}


private string GenerateEntityConfig(string entityName, string configNs, string entityNs, List<IPropertySymbol> props)
{
    StringBuilder sb = new StringBuilder($@"
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using {entityNs};

namespace {configNs}
{{
    public partial class {entityName}CommonConfiguration : AuditEntityConfiguration<{entityName}>
    {{
        public override void Configure(EntityTypeBuilder<{entityName}> builder)
        {{
");

    foreach (IPropertySymbol prop in props)
    {
        string columnName = prop.Name;
        StringBuilder line = new StringBuilder($@"            builder.Property(e => e.{prop.Name})");

        ImmutableArray<AttributeData> attributes = prop.GetAttributes();
        
        AttributeData columnAttr = attributes.FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.Schema.ColumnAttribute");
        if (columnAttr != null)
        {
            if (columnAttr.ConstructorArguments.Length > 0 &&
                columnAttr.ConstructorArguments[0].Value is string colName)
            {
                columnName = colName;
            }

            var typeNameArg = columnAttr.NamedArguments
                .FirstOrDefault(na => na.Key == "TypeName");
            if (typeNameArg.Value.Value is string typeName)
            {
                line.Append($@".HasColumnType(""{typeName}"")");
            }
        }
        
        if (attributes.Any(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.RequiredAttribute"))
        {
            line.Append(".IsRequired()");
        }
        
        AttributeData maxLengthAttr = attributes.FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.MaxLengthAttribute");
        if (maxLengthAttr != null && maxLengthAttr.ConstructorArguments.Length == 1)
        {
            object maxLength = maxLengthAttr.ConstructorArguments[0].Value;
            line.Append($".HasMaxLength({maxLength})");
        }
        
        AttributeData strLengthAttr = attributes.FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.StringLengthAttribute");
        if (strLengthAttr != null && strLengthAttr.ConstructorArguments.Length == 1)
        {
            object max = strLengthAttr.ConstructorArguments[0].Value;
            line.Append($".HasMaxLength({max})");
        }
        
        if (attributes.Any(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.ConcurrencyCheckAttribute"))
        {
            line.Append(".IsConcurrencyToken()");
        }
        
        if (attributes.Any(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.TimestampAttribute"))
        {
            line.Append(".IsRowVersion()");
        }
        
        if (!line.ToString().Contains(".HasMaxLength(") && prop.Type.SpecialType == SpecialType.System_String)
        {
            line.Append(".HasMaxLength(255)");
        }

        line.Append($@".HasColumnName(""{ToSnakeCase(columnName)}"");");
        sb.AppendLine(line.ToString());
    }

    sb.AppendLine("            base.Configure(builder);");
    sb.AppendLine("        }");
    sb.AppendLine("    }");
    sb.AppendLine("}");

    return sb.ToString();
}


    
}
