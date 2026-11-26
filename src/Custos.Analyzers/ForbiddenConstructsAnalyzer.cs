using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Custos.Analyzers;

/// <summary>
/// 型による検出を弱める書き方を禁止する（設計書 第12.7節「AIが書くC#の約束事」）。
/// どうしても必要な場合は、理由を書いて人間の承認を受け、このアナライザーの側を変える。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ForbiddenConstructsAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "CustosTyping";

    public static readonly DiagnosticDescriptor DynamicRule = new DiagnosticDescriptor(
        "CUS001",
        "dynamic は使わない",
        "dynamic は使わない（設計書 第12.7節）。必要なら理由を書いて人間の承認を受ける",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NullForgivingRule = new DiagnosticDescriptor(
        "CUS002",
        "null許容の警告を消す ! 演算子は使わない",
        "null許容の警告を消す ! 演算子は使わない（設計書 第12.7節）。null の可能性を型で扱う",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PragmaRule = new DiagnosticDescriptor(
        "CUS003",
        "#pragma warning disable は使わない",
        "警告を抑止する #pragma warning disable は使わない（設計書 第12.7節）",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SuppressAttributeRule = new DiagnosticDescriptor(
        "CUS004",
        "警告を抑止する属性は使わない",
        "警告を抑止する属性 {0} は使わない（設計書 第12.7節）",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NullableDisableRule = new DiagnosticDescriptor(
        "CUS005",
        "#nullable disable は使わない",
        "null許容参照型の検査を切る #nullable {0} は使わない（設計書 第12.7節）",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DynamicRule, NullForgivingRule, PragmaRule, SuppressAttributeRule, NullableDisableRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeIdentifier, SyntaxKind.IdentifierName);
        context.RegisterSyntaxNodeAction(AnalyzeNullForgiving, SyntaxKind.SuppressNullableWarningExpression);
        context.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
        context.RegisterSyntaxTreeAction(AnalyzeDirectives);
    }

    private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext context)
    {
        var node = (IdentifierNameSyntax)context.Node;
        if (node.Identifier.ValueText != "dynamic")
        {
            return;
        }

        var type = context.SemanticModel.GetTypeInfo(node, context.CancellationToken).Type;
        if (type != null && type.TypeKind == TypeKind.Dynamic)
        {
            context.ReportDiagnostic(Diagnostic.Create(DynamicRule, node.GetLocation()));
        }
    }

    private static void AnalyzeNullForgiving(SyntaxNodeAnalysisContext context)
    {
        context.ReportDiagnostic(Diagnostic.Create(NullForgivingRule, context.Node.GetLocation()));
    }

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol;
        var attributeType = symbol?.ContainingType;
        if (attributeType == null)
        {
            return;
        }

        var name = attributeType.ToDisplayString();
        if (name == "System.Diagnostics.CodeAnalysis.SuppressMessageAttribute"
            || name == "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute")
        {
            context.ReportDiagnostic(Diagnostic.Create(SuppressAttributeRule, attribute.GetLocation(), attributeType.Name));
        }
    }

    private static void AnalyzeDirectives(SyntaxTreeAnalysisContext context)
    {
        var root = context.Tree.GetRoot(context.CancellationToken);
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.HasStructure)
            {
                continue;
            }

            var structure = trivia.GetStructure();
            if (structure is PragmaWarningDirectiveTriviaSyntax pragma
                && pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword))
            {
                context.ReportDiagnostic(Diagnostic.Create(PragmaRule, pragma.GetLocation()));
            }
            else if (structure is NullableDirectiveTriviaSyntax nullable
                && nullable.SettingToken.IsKind(SyntaxKind.DisableKeyword))
            {
                context.ReportDiagnostic(Diagnostic.Create(NullableDisableRule, nullable.GetLocation(), nullable.SettingToken.ValueText));
            }
        }
    }
}
