﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis.SymbolUsageAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.RemoveUnusedParametersAndValues
{
    internal abstract partial class AbstractRemoveUnusedParametersAndValuesDiagnosticAnalyzer : AbstractBuiltInCodeStyleDiagnosticAnalyzer
    {
        private sealed partial class SymbolStartAnalyzer
        {
            private sealed partial class BlockAnalyzer
            {
                private readonly SymbolStartAnalyzer _symbolStartAnalyzer;
                private readonly Options _options;

                /// <summary>
                /// Indicates if the operation block has an <see cref="IDelegateCreationOperation"/> or an <see cref="IAnonymousFunctionOperation"/>.
                /// We use this value in <see cref="ShouldAnalyze(IOperation, ISymbol)"/> to determine whether to bail from analysis or not.
                /// </summary>
                private bool _hasDelegateCreationOrAnonymousFunction;

                /// <summary>
                /// Indicates if a delegate instance escaped this operation block, via an assignment to a field or a property symbol.
                /// that can be accessed outside this executable code block.
                /// We use this value in <see cref="ShouldAnalyze(IOperation, ISymbol)"/> to determine whether to bail from analysis or not.
                /// </summary>
                private bool _delegateAssignedToFieldOrProperty;

                /// <summary>
                /// Indicates if the operation block has an <see cref="IConversionOperation"/> with a delegate type as it's source type
                /// and a non-delegate type as it's target.
                /// We use this value in <see cref="ShouldAnalyze(IOperation, ISymbol)"/> to determine whether to bail from analysis or not.
                /// </summary>
                private bool _hasConversionFromDelegateTypeToNonDelegateType;

                private BlockAnalyzer(SymbolStartAnalyzer symbolStartAnalyzer, Options options)
                {
                    _symbolStartAnalyzer = symbolStartAnalyzer;
                    _options = options;
                }

                public static void Analyze(OperationBlockStartAnalysisContext context, SymbolStartAnalyzer symbolStartAnalyzer)
                {
                    if (HasSyntaxErrors() || context.OperationBlocks.IsEmpty)
                    {
                        return;
                    }

                    // All operation blocks for a symbol belong to the same tree.
                    var firstBlock = context.OperationBlocks[0];
                    if (!symbolStartAnalyzer._compilationAnalyzer.TryGetOptions(firstBlock.Syntax.SyntaxTree, firstBlock.Language,
                        context.Options, context.CancellationToken, out var options))
                    {
                        return;
                    }

                    var blockAnalyzer = new BlockAnalyzer(symbolStartAnalyzer, options);
                    context.RegisterOperationAction(blockAnalyzer.AnalyzeExpressionStatement, OperationKind.ExpressionStatement);
                    context.RegisterOperationAction(blockAnalyzer.AnalyzeDelegateCreationOrAnonymousFunction, OperationKind.DelegateCreation, OperationKind.AnonymousFunction);
                    context.RegisterOperationAction(blockAnalyzer.AnalyzeConversion, OperationKind.Conversion);
                    context.RegisterOperationAction(blockAnalyzer.AnalyzeFieldOrPropertyReference, OperationKind.FieldReference, OperationKind.PropertyReference);
                    context.RegisterOperationBlockEndAction(blockAnalyzer.AnalyzeOperationBlockEnd);

                    return;

                    // Local Functions.
                    bool HasSyntaxErrors()
                    {
                        foreach (var operationBlock in context.OperationBlocks)
                        {
                            if (operationBlock.SemanticModel.GetSyntaxDiagnostics(operationBlock.Syntax.Span, context.CancellationToken).HasAnyErrors())
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                }

                private void AnalyzeExpressionStatement(OperationAnalysisContext context)
                {
                    if (_options.UnusedValueExpressionStatementSeverity == ReportDiagnostic.Suppress)
                    {
                        return;
                    }

                    var expressionStatement = (IExpressionStatementOperation)context.Operation;
                    var value = expressionStatement.Operation;

                    // Bail out cases for report unused expression value:

                    //  1. Null type and void returning method invocations: no value being dropped here.
                    if (value.Type == null ||
                        value.Type.SpecialType == SpecialType.System_Void)
                    {
                        return;
                    }

                    //  2. Bail out for syntax error (constant expressions) and semantic error (invalid operation) cases.
                    if (value.ConstantValue.HasValue ||
                        value is IInvalidOperation)
                    {
                        return;
                    }

                    //  3. Assignments, increment/decrement operations: value is actually being assigned.
                    if (value is IAssignmentOperation ||
                        value is IIncrementOrDecrementOperation)
                    {
                        return;
                    }

                    //  4. Bool returning method invocations: these are extremely noisy as large number of bool 
                    //     returning methods return the status of the operation.
                    //     In future, if we feel noise level is fine, we can consider removing this check.
                    if (value.Type.SpecialType == SpecialType.System_Boolean)
                    {
                        return;
                    }

                    //  5. If expression statement and its underlying expression have differing first tokens that likely indicates
                    //     an explicit discard. For example, VB call statement is used to explicitly ignore the value returned by
                    //     an invocation by prefixing the invocation with keyword "Call".
                    if (value.Syntax.GetFirstToken() != expressionStatement.Syntax.GetFirstToken())
                    {
                        return;
                    }

                    //  6. Special cases where return value is not required to be checked:
                    //     Methods belonging to System.Threading.Interlocked and System.Collections.Immutable.ImmutableInterlocked
                    //     return the original value, which is not required to be checked.
                    if (value is IInvocationOperation invocation &&
                        (invocation.TargetMethod.ContainingType.OriginalDefinition == _symbolStartAnalyzer._interlockedTypeOpt ||
                         invocation.TargetMethod.ContainingType.OriginalDefinition == _symbolStartAnalyzer._immutableInterlockedTypeOpt))
                    {
                        return;
                    }

                    var properties = s_propertiesMap[(_options.UnusedValueExpressionStatementPreference, isUnusedLocalAssignment: false, isRemovableAssignment: false)];
                    var diagnostic = DiagnosticHelper.Create(s_expressionValueIsUnusedRule,
                                                             value.Syntax.GetLocation(),
                                                             _options.UnusedValueExpressionStatementSeverity,
                                                             additionalLocations: null,
                                                             properties);
                    context.ReportDiagnostic(diagnostic);
                }

                private void AnalyzeDelegateCreationOrAnonymousFunction(OperationAnalysisContext operationAnalysisContext)
                    => _hasDelegateCreationOrAnonymousFunction = true;

                private void AnalyzeConversion(OperationAnalysisContext operationAnalysisContext)
                {
                    var conversion = (IConversionOperation)operationAnalysisContext.Operation;
                    if (!_hasConversionFromDelegateTypeToNonDelegateType &&
                        conversion.Operand.Type.IsDelegateType() &&
                        !conversion.Type.IsDelegateType())
                    {
                        _hasConversionFromDelegateTypeToNonDelegateType = true;
                    }
                }

                private void AnalyzeFieldOrPropertyReference(OperationAnalysisContext operationAnalysisContextContext)
                {
                    var fieldOrPropertyReference = operationAnalysisContextContext.Operation;
                    if (!_delegateAssignedToFieldOrProperty &&
                        fieldOrPropertyReference.Type.IsDelegateType() &&
                        fieldOrPropertyReference.Parent is ISimpleAssignmentOperation simpleAssignment &&
                        simpleAssignment.Target == fieldOrPropertyReference)
                    {
                        _delegateAssignedToFieldOrProperty = true;
                    }
                }

                /// <summary>
                /// Method invoked in <see cref="AnalyzeOperationBlockEnd(OperationBlockAnalysisContext)"/>
                /// for each operation block to determine if we should analyze the operation block or bail out.
                /// </summary>
                private bool ShouldAnalyze(IOperation operationBlock, ISymbol owningSymbol)
                {
                    switch (operationBlock.Kind)
                    {
                        case OperationKind.None:
                        case OperationKind.ParameterInitializer:
                            // Skip blocks from attributes (which have OperationKind.None) and parameter initializers.
                            // We don't have any unused values in such operation blocks.
                            return false;
                    }

                    // We currently do not support points-to analysis, so we cannot accurately 
                    // track delegate invocations for all cases.
                    // We attempt to do our best effort delegate invocation analysis as follows:

                    //  1. If we have no delegate creations or lambdas, our current analysis works fine,
                    //     return true.
                    if (!_hasDelegateCreationOrAnonymousFunction)
                    {
                        return true;
                    }

                    //  2. Bail out if we have a delegate escape via an assigment to a field/property reference.
                    if (_delegateAssignedToFieldOrProperty)
                    {
                        return false;
                    }

                    //  3. Bail out if we have a conversion from a delegate type to a non-delegate type.
                    //     We can analyze this correctly when we do points-to-analysis.
                    if (_hasConversionFromDelegateTypeToNonDelegateType)
                    {
                        return false;
                    }

                    //  4. Bail out for method returning delegates or ref/out parameters of delegate type.
                    //     We can analyze this correctly when we do points-to-analysis.
                    if (owningSymbol is IMethodSymbol method &&
                        (method.ReturnType.IsDelegateType() ||
                         method.Parameters.Any(p => p.IsRefOrOut() && p.Type.IsDelegateType())))
                    {
                        return false;
                    }

                    //  5. Otherwise, we execute analysis by walking the reaching symbol write chain to attempt to
                    //     find the target method being invoked.
                    //     This works for most common and simple cases where a local is assigned a lambda and invoked later.
                    //     If we are unable to find a target, we will conservatively mark all current symbol writes as read.
                    return true;
                }

                private void AnalyzeOperationBlockEnd(OperationBlockAnalysisContext context)
                {
                    // Bail out if we are neither computing unused parameters nor unused value assignments.
                    var isComputingUnusedParams = _options.IsComputingUnusedParams(context.OwningSymbol);
                    if (_options.UnusedValueAssignmentSeverity == ReportDiagnostic.Suppress &&
                        !isComputingUnusedParams)
                    {
                        return;
                    }

                    // We perform analysis to compute unused parameters and value assignments in two passes.
                    // Unused value assignments can be identified by analyzing each operation block independently in the first pass.
                    // However, to identify unused parameters we need to first analyze all operation blocks and then iterate
                    // through the parameters to identify unused ones

                    // Builder to store the symbol read/write usage result for each operation block computed during the first pass.
                    // These are later used to compute unused parameters in second pass.
                    var symbolUsageResultsBuilder = PooledHashSet<SymbolUsageResult>.GetInstance();

                    try
                    {
                        // Flag indicating if we found an operation block where all symbol writes were used. 
                        bool hasBlockWithAllUsedWrites;

                        AnalyzeUnusedValueAssignments(context, isComputingUnusedParams, symbolUsageResultsBuilder, out hasBlockWithAllUsedWrites);

                        AnalyzeUnusedParameters(context, isComputingUnusedParams, symbolUsageResultsBuilder, hasBlockWithAllUsedWrites);
                    }
                    finally
                    {
                        symbolUsageResultsBuilder.Free();
                    }
                }

                private void AnalyzeUnusedValueAssignments(
                    OperationBlockAnalysisContext context,
                    bool isComputingUnusedParams,
                    PooledHashSet<SymbolUsageResult> symbolUsageResultsBuilder,
                    out bool hasBlockWithAllUsedSymbolWrites)
                {
                    hasBlockWithAllUsedSymbolWrites = false;

                    foreach (var operationBlock in context.OperationBlocks)
                    {
                        if (!ShouldAnalyze(operationBlock, context.OwningSymbol))
                        {
                            continue;
                        }

                        // First perform the fast, aggressive, imprecise operation-tree based analysis.
                        // This analysis might flag some "used" symbol writes as "unused", but will not miss reporting any truly unused symbol writes.
                        // This initial pass helps us reduce the number of methods for which we perform the slower second pass.
                        // We perform the first fast pass only if there are no delegate creations/lambda methods.
                        // This is due to the fact that tracking which local/parameter points to which delegate creation target
                        // at any given program point needs needs flow analysis (second pass).
                        if (!_hasDelegateCreationOrAnonymousFunction)
                        {
                            var resultFromOperationBlockAnalysis = SymbolUsageAnalysis.Run(operationBlock, context.OwningSymbol, context.CancellationToken);
                            if (!resultFromOperationBlockAnalysis.HasUnreadSymbolWrites())
                            {
                                // Assert that even slow pass (dataflow analysis) would have yielded no unused symbol writes.
                                Debug.Assert(!SymbolUsageAnalysis.Run(context.GetControlFlowGraph(operationBlock), context.OwningSymbol, context.CancellationToken)
                                             .HasUnreadSymbolWrites());

                                hasBlockWithAllUsedSymbolWrites = true;
                                continue;
                            }
                        }

                        // Now perform the slower, precise, CFG based dataflow analysis to identify the actual unused symbol writes.
                        var cfg = context.GetControlFlowGraph(operationBlock);
                        var symbolUsageResult = SymbolUsageAnalysis.Run(cfg, context.OwningSymbol, context.CancellationToken);
                        symbolUsageResultsBuilder.Add(symbolUsageResult);

                        foreach (var (symbol, unreadWriteOperation) in symbolUsageResult.GetUnreadSymbolWrites())
                        {
                            if (unreadWriteOperation == null)
                            {
                                // Null operation is used for initial write for the parameter from method declaration.
                                // So, the initial value of the parameter is never read in this operation block.
                                // However, we do not report this as an unused parameter here as a different operation block
                                // might be reading the initial parameter value.
                                // For example, a constructor with both a constructor initializer and body will have two different operation blocks
                                // and a parameter must be unused across both these blocks to be marked unused.

                                // However, we do report unused parameters for local function here.
                                // Local function parameters are completely scoped to this operation block, and should be reported per-operation block.
                                var unusedParameter = (IParameterSymbol)symbol;
                                if (isComputingUnusedParams &&
                                    unusedParameter.ContainingSymbol.IsLocalFunction())
                                {
                                    var hasReference = symbolUsageResult.SymbolsRead.Contains(unusedParameter);
                                    _symbolStartAnalyzer.ReportUnusedParameterDiagnostic(unusedParameter, hasReference, context.ReportDiagnostic, context.Options, context.CancellationToken);
                                }

                                continue;
                            }

                            if (ShouldReportUnusedValueDiagnostic(symbol, unreadWriteOperation, symbolUsageResult, out var properties))
                            {
                                var diagnostic = DiagnosticHelper.Create(s_valueAssignedIsUnusedRule,
                                                                         _symbolStartAnalyzer._compilationAnalyzer.GetDefinitionLocationToFade(unreadWriteOperation),
                                                                         _options.UnusedValueAssignmentSeverity,
                                                                         additionalLocations: null,
                                                                         properties,
                                                                         symbol.Name);
                                context.ReportDiagnostic(diagnostic);
                            }
                        }
                    }

                    return;

                    // Local functions.
                    bool ShouldReportUnusedValueDiagnostic(
                        ISymbol symbol,
                        IOperation unreadWriteOperation,
                        SymbolUsageResult resultFromFlowAnalysis,
                        out ImmutableDictionary<string, string> properties)
                    {
                        properties = null;
                        if (_options.UnusedValueAssignmentSeverity == ReportDiagnostic.Suppress)
                        {
                            return false;
                        }

                        // Flag to indicate if the symbol has no reads.
                        var isUnusedLocalAssignment = symbol is ILocalSymbol localSymbol &&
                                                      !resultFromFlowAnalysis.SymbolsRead.Contains(localSymbol);

                        var isRemovableAssignment = IsRemovableAssignmentWithoutSideEffects(unreadWriteOperation);

                        if (isUnusedLocalAssignment &&
                            !isRemovableAssignment &&
                            _options.UnusedValueAssignmentPreference == UnusedValuePreference.UnusedLocalVariable)
                        {
                            // Meets current user preference of using unused local symbols for storing computation result.
                            // Skip reporting diagnostic.
                            return false;
                        }

                        properties = s_propertiesMap[(_options.UnusedValueAssignmentPreference, isUnusedLocalAssignment, isRemovableAssignment)];
                        return true;
                    }

                    // Indicates if the given unused symbol write is a removable assignment.
                    // This is true if the expression for the assigned value has no side effects.
                    bool IsRemovableAssignmentWithoutSideEffects(IOperation unusedSymbolWriteOperation)
                    {
                        if (unusedSymbolWriteOperation.Parent is IAssignmentOperation assignment &&
                            assignment.Target == unusedSymbolWriteOperation)
                        {
                            if (assignment.Value.ConstantValue.HasValue)
                            {
                                // Constant expressions have no side effects.
                                return true;
                            }

                            switch (assignment.Value.Kind)
                            {
                                // Parameter/local references have no side effects and can be removed.
                                case OperationKind.ParameterReference:
                                case OperationKind.LocalReference:
                                    return true;

                                // Field references with null instance (static fields) or 'this' or 'Me' instance can
                                // have no side effects and can be removed.
                                case OperationKind.FieldReference:
                                    var fieldReference = (IFieldReferenceOperation)assignment.Value;
                                    return fieldReference.Instance == null || fieldReference.Instance.Kind == OperationKind.InstanceReference;
                            }
                        }
                        else if (unusedSymbolWriteOperation.Parent is IIncrementOrDecrementOperation)
                        {
                            // Increment or decrement operations have no side effects.
                            return true;
                        }

                        // Assume all other operations can have side effects, and cannot be removed.
                        return false;
                    }
                }

                private void AnalyzeUnusedParameters(
                    OperationBlockAnalysisContext context,
                    bool isComputingUnusedParams,
                    PooledHashSet<SymbolUsageResult> symbolUsageResultsBuilder,
                    bool hasBlockWithAllUsedSymbolWrites)
                {
                    // Process parameters for the context's OwningSymbol that are unused across all operation blocks.

                    // Bail out cases:
                    //  1. Skip analysis if we are not computing unused parameters based on user's option preference.
                    if (!isComputingUnusedParams)
                    {
                        return;
                    }

                    //  2. Bail out if we found a single operation block where all symbol writes were used.
                    if (hasBlockWithAllUsedSymbolWrites)
                    {
                        return;
                    }

                    // 3. Bail out if symbolUsageResultsBuilder is empty, indicating we skipped analysis for all operation blocks.
                    if (symbolUsageResultsBuilder.Count == 0)
                    {
                        return;
                    }

                    // 4. Report unused parameters only for method symbols.
                    if (!(context.OwningSymbol is IMethodSymbol method))
                    {
                        return;
                    }

                    foreach (var parameter in method.Parameters)
                    {
                        bool isUsed = false;
                        bool isSymbolRead = false;
                        var isRefOrOutParam = parameter.IsRefOrOut();

                        // Iterate through symbol usage results for each operation block.
                        foreach (var symbolUsageResult in symbolUsageResultsBuilder)
                        {
                            if (symbolUsageResult.IsInitialParameterValueUsed(parameter))
                            {
                                // Parameter is used in this block.
                                isUsed = true;
                                break;
                            }

                            isSymbolRead |= symbolUsageResult.SymbolsRead.Contains(parameter);

                            // Ref/Out parameters are considered used if they have any reads or writes
                            // Note that we always have one write for the parameter input value from the caller.
                            if (isRefOrOutParam &&
                                (isSymbolRead ||
                                symbolUsageResult.GetSymbolWriteCount(parameter) > 1))
                            {
                                isUsed = true;
                                break;
                            }
                        }

                        if (!isUsed)
                        {
                            _symbolStartAnalyzer._unusedParameters.GetOrAdd(parameter, isSymbolRead);
                        }
                    }
                }
            }
        }
    }
}
