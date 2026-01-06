using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Netway.AuditableEntity.SourceGenerator;

public class ConfSyntaxReciever : ISyntaxReceiver
{
    public List<ClassDeclarationSyntax> ConfigClasses { get; } = new();

    public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
    {
        if (syntaxNode is ClassDeclarationSyntax classDecl &&
            classDecl.AttributeLists.SelectMany(a => a.Attributes)
                .Any(attr => attr.Name.ToString().Contains("GenerateConfiguration")))
        {
            ConfigClasses.Add(classDecl);
        }
    }
}