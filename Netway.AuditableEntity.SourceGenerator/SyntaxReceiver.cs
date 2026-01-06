using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Netway.AuditableEntity.SourceGenerator;

public class SyntaxReceiver : ISyntaxReceiver
{
    public List<ClassDeclarationSyntax> Candidates { get; } = new();

    public void OnVisitSyntaxNode(SyntaxNode node)
    {
        if (node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0)
            Candidates.Add(cds);
    }
}