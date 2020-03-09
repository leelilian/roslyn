﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Simplification;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.CodeFixes.AddExplicitCast
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = PredefinedCodeFixProviderNames.AddExplicitCast), Shared]
    internal sealed partial class AddExplicitCastCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        /// <summary>
        /// CS0266: Cannot implicitly convert from type 'x' to 'y'. An explicit conversion exists (are you missing a cast?)
        /// </summary>
        private const string CS0266 = nameof(CS0266);

        /// <summary>
        /// CS1503: Argument 1: cannot convert from 'x' to 'y'
        /// </summary>
        private const string CS1503 = nameof(CS1503);

        /// <summary>
        /// Give a set of least specific types with a limit, and the part exceeding the limit doesn't show any code fix, but logs telemetry 
        /// </summary>
        private const int MaximumConversionOptions = 3;

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(CS0266, CS1503);

        internal override CodeFixCategory CodeFixCategory => CodeFixCategory.Compile;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var document = context.Document;
            var cancellationToken = context.CancellationToken;
            var diagnostic = context.Diagnostics.First();

            var root = await document.GetRequiredSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var targetNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                .GetAncestorsOrThis<ExpressionSyntax>().FirstOrDefault();
            if (targetNode == null)
                return;

            var hasSolution = TryGetTargetTypeInfo(
                semanticModel, diagnostic.Id, targetNode, cancellationToken,
                out var nodeType, out var potentialConversionTypes);
            if (!hasSolution)
            {
                return;
            }

            if (potentialConversionTypes.Length == 1)
            {
                context.RegisterCodeFix(new MyCodeAction(
                    CSharpFeaturesResources.Add_explicit_cast,
                    c => FixAsync(context.Document, context.Diagnostics.First(), c)),
                    context.Diagnostics);
            }
            else
            {
                var actions = ArrayBuilder<CodeAction>.GetInstance();

                // MaximumConversionOptions: we show at most [MaximumConversionOptions] options for this code fixer
                for (var i = 0; i < Math.Min(MaximumConversionOptions, potentialConversionTypes.Length); i++)
                {
                    var convType = potentialConversionTypes[i];
                    actions.Add(new MyCodeAction(
                        string.Format(
                            CSharpFeaturesResources.Convert_type_to_0,
                            convType.ToMinimalDisplayString(semanticModel, context.Span.Start)),
                        _ => ApplySingleConversionToDocumentAsync(document, ApplyFix(root, targetNode, convType))));
                }

                if (potentialConversionTypes.Length > MaximumConversionOptions)
                {
                    // If the number of potential conversion types is larger than options we could show, report telemetry
                    Logger.Log(FunctionId.CodeFixes_AddExplicitCast,
                        KeyValueLogMessage.Create(m =>
                        {
                            m["NumberOfCandidates"] = potentialConversionTypes.Length;
                        }));
                }

                context.RegisterCodeFix(new CodeAction.CodeActionWithNestedActions(
                    CSharpFeaturesResources.Add_explicit_cast,
                    actions.ToImmutableAndFree(), isInlinable: false),
                    context.Diagnostics);
            }
        }

        private static SyntaxNode ApplyFix(SyntaxNode currentRoot, ExpressionSyntax targetNode, ITypeSymbol conversionType)
        {
            // TODO: castExpression.WithAdditionalAnnotations(Simplifier.Annotation) 
            // - the Simplifier doesn't remove the redundant cast from the expression
            // Issue link: https://github.com/dotnet/roslyn/issues/41500
            var castExpression = targetNode.Cast(conversionType).WithAdditionalAnnotations(Simplifier.Annotation);
            var newRoot = currentRoot.ReplaceNode(targetNode, castExpression);
            return newRoot;
        }

        private static Task<Document> ApplySingleConversionToDocumentAsync(Document document, SyntaxNode currentRoot)
            => Task.FromResult(document.WithSyntaxRoot(currentRoot));

        /// <summary>
        /// Output the current type information of the target node and the conversion type(s) that the target node is going to be cast by.
        /// Implicit downcast can appear on Variable Declaration, Return Statement, and Function Invocation
        /// <para/>
        /// For example:
        /// Base b; Derived d = [||]b;       
        /// "b" is the current node with type "Base", and the potential conversion types list which "b" can be cast by is {Derived}
        /// </summary>
        /// <param name="diagnosticId"> The ID of the diagnostic.</param>
        /// <param name="targetNode"> The node to be cast.</param>
        /// <param name="targetNodeType"> Output the type of "targetNode".</param>
        /// <param name="potentialConversionTypes"> Output the potential conversions types that "targetNode" can be cast to</param>
        /// <returns>
        /// True, if the target node has at least one potential conversion type, and they are assigned to "potentialConversionTypes"
        /// False, if the target node has no conversion type.
        /// </returns>
        private static bool TryGetTargetTypeInfo(
            SemanticModel semanticModel, string diagnosticId, SyntaxNode targetNode, CancellationToken cancellationToken,
            [NotNullWhen(true)] out ITypeSymbol? targetNodeType, out ImmutableArray<ITypeSymbol> potentialConversionTypes)
        {
            potentialConversionTypes = ImmutableArray<ITypeSymbol>.Empty;

            var targetNodeInfo = semanticModel.GetTypeInfo(targetNode, cancellationToken);
            targetNodeType = targetNodeInfo.Type;

            if (targetNodeType == null)
                return false;

            // The error happens either on an assignement operation or on an invocation expression.
            // If the error happens on assignment operation, "ConvertedType" is different from the current "Type"
            using var _ = ArrayBuilder<ITypeSymbol>.GetInstance(out var mutablePotentialConversionTypes);
            if (diagnosticId == CS0266
                && targetNodeInfo.ConvertedType != null
                && !targetNodeType.Equals(targetNodeInfo.ConvertedType))
            {
                mutablePotentialConversionTypes.Add(targetNodeInfo.ConvertedType);
            }
            else if (diagnosticId == CS1503
                && targetNode.GetAncestorsOrThis<ArgumentSyntax>().FirstOrDefault() is ArgumentSyntax targetArgument
                && targetArgument.Parent is ArgumentListSyntax argumentList
                && argumentList.Parent is SyntaxNode invocationNode) // invocation node could be Invocation Expression, Object Creation, Base Constructor...
            {
                // Implicit downcast appears on the argument of invocation node, get all candidate functions and extract potential conversion types 
                var symbolInfo = semanticModel.GetSymbolInfo(invocationNode, cancellationToken);
                var candidateSymbols = symbolInfo.CandidateSymbols;

                foreach (var candidateSymbol in candidateSymbols.OfType<IMethodSymbol>())
                {
                    if (CanArgumentTypesBeConvertedToParameterTypes(
                            semanticModel, argumentList.Arguments,
                            candidateSymbol.Parameters, targetArgument,
                            cancellationToken, out var paramIndex))
                    {
                        var correspondingParameter = candidateSymbol.Parameters[paramIndex];
                        var argumentConversionType = correspondingParameter.Type;

                        if (correspondingParameter.IsParams
                            && correspondingParameter.Type is IArrayTypeSymbol arrayType
                            && !(targetNodeType is IArrayTypeSymbol))
                        {
                            // target argument is matched to the parameter with keyword params
                            argumentConversionType = arrayType.ElementType;
                        }

                        mutablePotentialConversionTypes.Add(argumentConversionType);
                    }
                }

                // Sort the potential conversion types by inheritance distance, so that
                // operations are in order and user can choose least specific types(more accurate)
                var comparer = new InheritanceDistanceComparer(semanticModel, targetNodeType);
                mutablePotentialConversionTypes.Sort(comparer);
            }

            using var __ = ArrayBuilder<ITypeSymbol>.GetInstance(out var validPotentialConversionTypes);
            foreach (var targetNodeConversionType in mutablePotentialConversionTypes)
            {
                var commonConversion = semanticModel.Compilation.ClassifyCommonConversion(
                    targetNodeType, targetNodeConversionType);

                // For cases like object creation expression. for example:
                // Derived d = [||]new Base();
                // It is always invalid except the target node has explicit conversion operator or is numeric.
                if (targetNode.IsKind(SyntaxKind.ObjectCreationExpression)
                    && !(commonConversion.IsUserDefined || commonConversion.IsNumeric))
                {
                    continue;
                }

                if (commonConversion.Exists)
                {
                    validPotentialConversionTypes.Add(targetNodeConversionType);
                }
            }

            // clear up duplicate types
            potentialConversionTypes = validPotentialConversionTypes.Distinct().ToImmutableArray();
            return !potentialConversionTypes.IsEmpty;
        }

        /// <summary>
        /// Test if all argument types can be converted to corresponding parameter types.
        /// </summary>
        /// For example:
        /// class Base { }
        /// class Derived1 : Base { }
        /// class Derived2 : Base { }
        /// class Derived3 : Base { }
        /// void DoSomething(int i, Derived1 d) { }
        /// void DoSomething(string s, Derived2 d) { }
        /// void DoSomething(int i, Derived3 d) { }
        /// 
        /// Base b;
        /// DoSomething(1, [||]b);
        ///
        /// *void DoSomething(string s, Derived2 d) { }* is not the perfect match candidate function for
        /// *DoSomething(1, [||]b)* because int and string are not ancestor-descendant relationship. Thus,
        /// Derived2 is not a potential conversion type.
        /// 
        /// <param name="arguments"> The arguments of invocation expression</param>
        /// <param name="parameters"> The parameters of function</param>
        /// <param name="targetArgument"> The target argument that contains target node</param>
        /// <param name="targetParamIndex"> Output the corresponding parameter index of the target arugment if function returns true</param>
        /// <returns>
        /// True, if arguments and parameters match perfectly.
        /// False, otherwise.
        /// </returns>
        // TODO: May need an API to replace this function,
        // link: https://github.com/dotnet/roslyn/issues/42149
        private static bool CanArgumentTypesBeConvertedToParameterTypes(
            SemanticModel semanticModel, SeparatedSyntaxList<ArgumentSyntax> arguments,
            ImmutableArray<IParameterSymbol> parameters, ArgumentSyntax targetArgument,
            CancellationToken cancellationToken, out int targetParamIndex)
        {
            // return invalid index if not all argument types can be converted to corresponding parameter types
            targetParamIndex = -1;
            if (parameters.Length == 0)
                return false;

            // We have analyzed every argument and tried to make it correspond to a particular parameter. 
            // We must now answer the following questions:
            //
            // (1) Is there any named argument used out-of-position and followed by unnamed arguments?
            // (2) Is there any argument without a corresponding parameter?
            // (3) Was there any named argument that specified a parameter that was already
            //     supplied with a positional parameter?
            // (4) Is there any non-optional parameter without a corresponding argument?
            // (5) Is there any named argument that were specified twice?
            //
            // If the answer to any of there questions is "yes" then the method is not applicable.
            // The answer to (5) is always "No" since if there are named argument that were specified
            // twice, it wouldn't report error CS1503, and it cannot trigger this code fixer

            var matchedTypes = new bool[parameters.Length]; // default value is false
            var paramsMatchedByArray = false; // the parameter with keyword params can be either matched by an array type or a variable number of arguments
            var inOrder = true; // assume the arguments are in order by default

            for (var i = 0; i < arguments.Count; i++)
            {
                // Parameter index cannot out of its range, #arguments is larger than #parameter only if the last parameter with keyword params
                var parameterIndex = Math.Min(i, parameters.Length - 1);

                // If the argument has a name, get the corresponding parameter index
                var nameSyntax = arguments[i].NameColon?.Name;
                if (nameSyntax != null)
                {
                    var name = nameSyntax.Identifier.ValueText;
                    if (!FindCorrespondingParameterByName(name, parameters, ref inOrder, ref parameterIndex))
                        return false;

                    bool FindCorrespondingParameterByName(string name, ImmutableArray<IParameterSymbol> parameters, ref bool inOrder, ref int parameterIndex)
                    {
                        for (var j = 0; j < parameters.Length; j++)
                        {
                            if (name.Equals(parameters[j].Name))
                            {
                                // Check if the argument is in order with parameters.
                                // If the argument breaks the order, the rest arguments of matched functions must have names
                                if (i != j)
                                    inOrder = false;
                                parameterIndex = j;
                                return true;
                            }
                        }

                        // rule (2)
                        return false;
                    }
                }

                // The argument is either in order with parameters, or have a matched name with parameters
                var argType = semanticModel.GetTypeInfo(arguments[i].Expression, cancellationToken);

                // TODO: https://github.com/dotnet/roslyn/issues/42235
                // The type of lambda function is null, but it probably can be matched to its corresponding parameter 
                // Keep an eye on the identifier that is syntax and semantic true but the type is null.
                if (argType.Type == null)
                    return false;

                // rule (1)
                if (!inOrder && nameSyntax is null)
                    return false;

                // The type of argument must be convertible to the type of parameter
                if (!parameters[parameterIndex].IsParams
                    && semanticModel.Compilation.ClassifyCommonConversion(argType.Type, parameters[parameterIndex].Type).Exists)
                {
                    // rule (3)
                    if (matchedTypes[parameterIndex])
                        return false;
                    matchedTypes[parameterIndex] = true;
                }
                else if (parameters[parameterIndex].IsParams
                    && semanticModel.Compilation.ClassifyCommonConversion(argType.Type, parameters[parameterIndex].Type).Exists)
                {
                    // The parameter with keyword params takes an array type, then it cannot be matched more than once
                    // rule (3)
                    if (matchedTypes[parameterIndex])
                        return false;
                    matchedTypes[parameterIndex] = true;
                    paramsMatchedByArray = true;
                }
                else if (parameters[parameterIndex].IsParams
                         && parameters.Last().Type is IArrayTypeSymbol paramsType
                         && semanticModel.Compilation.ClassifyCommonConversion(argType.Type, paramsType.ElementType).Exists)
                {
                    // The parameter with keyword params can take a *variable* number of arguments,
                    // compare its element type with the argument's type.

                    // rule (3)
                    if (matchedTypes[parameterIndex] && paramsMatchedByArray)
                        return false;
                    matchedTypes[parameterIndex] = true;
                    paramsMatchedByArray = false;
                }
                else
                {
                    // rule (2)
                    return false;
                }

                if (targetArgument.Equals(arguments[i]))
                    targetParamIndex = parameterIndex;
            }

            // mark all optional parameters as matched
            // rule (4)
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOptional || parameters[i].IsParams)
                {
                    matchedTypes[i] = true;
                }
            }

            // rule (4)
            return matchedTypes.All(a => a);
        }

        protected override async Task FixAllAsync(
            Document document, ImmutableArray<Diagnostic> diagnostics,
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var targetNodes = diagnostics.SelectAsArray(
                d => root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true)
                         .GetAncestorsOrThis<ExpressionSyntax>().FirstOrDefault());

            await editor.ApplyExpressionLevelSemanticEditsAsync(
                document, targetNodes,
                (semanticModel, targetNode) => true,
                (semanticModel, currentRoot, targetNode) =>
                {
                    // All diagnostics have the same error code
                    if (TryGetTargetTypeInfo(semanticModel, diagnostics[0].Id, targetNode, cancellationToken, out var nodeType, out var potentialConversionTypes)
                        && potentialConversionTypes.Length == 1)
                    {
                        return ApplyFix(currentRoot, targetNode, potentialConversionTypes[0]);
                    }

                    return currentRoot;
                },
                cancellationToken).ConfigureAwait(false);
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(string title, Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(title, createChangedDocument, equivalenceKey: title)
            {
            }
        }
    }
}
