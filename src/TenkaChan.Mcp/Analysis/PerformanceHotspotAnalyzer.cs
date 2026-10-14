using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;

namespace TenkaChan.Mcp.Analysis;

public sealed record HotspotMethodDefinition(
    MethodSummary Summary, SyntaxNode Syntax, IMethodSymbol Symbol,
    SemanticModel SemanticModel, string ProjectDirectory);

/// <summary>
/// Deterministic workload-independent static screening. The intent is to
/// narrow the search space before real CPU and allocation profiling.
/// </summary>
public static class PerformanceHotspotAnalyzer
{
    private sealed record PendingEdge(string CallerId, string CalleeId,
        string File, int Line, int Depth, string Dispatch);

    public static StaticHotspotReport Analyze(
        string snapshotId, IReadOnlyList<HotspotMethodDefinition> definitions,
        int methodLimit = 100, int siteLimit = 300,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
            throw new ArgumentException("Snapshot ID required.");
        methodLimit = Math.Clamp(methodLimit, 1, 500);
        siteLimit = Math.Clamp(siteLimit, 1, 2000);

        var bySymbol = new Dictionary<ISymbol, HotspotMethodDefinition>(
            SymbolEqualityComparer.Default);
        foreach (var item in definitions)
            bySymbol.TryAdd(item.Symbol.OriginalDefinition, item);

        var sites = new List<HotspotSite>();
        var edges = new List<PendingEdge>();
        var rankings = new List<HotspotMethodRanking>();
        foreach (var definition in definitions.OrderBy(d => d.Summary.Project, StringComparer.Ordinal)
            .ThenBy(d => d.Summary.File, StringComparer.Ordinal)
            .ThenBy(d => d.Summary.Line))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var syntax = definition.Syntax;
            var model = definition.SemanticModel;
            var nodes = CallableSyntax.OwnNodes(syntax).ToArray();

            var loops = nodes.Where(IsLoop).ToArray();
            var maximumDepth = loops.Length == 0 ? 0 : loops.Max(n => LoopDepth(n) + 1);
            var nested = loops.Count(n => LoopDepth(n) > 0);
            var ownedSites = new List<HotspotSite>();
            var ownedEdges = new List<PendingEdge>();
            var allocationCandidates = 0;
            var enumerationCandidates = 0;
            var blockingCandidates = 0;

            foreach (var loop in loops)
            {
                var directArithmetic = nodes.OfType<BinaryExpressionSyntax>().Count(binary =>
                    loop.Span.Contains(binary.Span) &&
                    LoopDepth(binary) == LoopDepth(loop) + 1 &&
                    (binary.IsKind(SyntaxKind.AddExpression) ||
                     binary.IsKind(SyntaxKind.SubtractExpression) ||
                     binary.IsKind(SyntaxKind.MultiplyExpression) ||
                     binary.IsKind(SyntaxKind.DivideExpression) ||
                     binary.IsKind(SyntaxKind.ModuloExpression) ||
                     binary.IsKind(SyntaxKind.LeftShiftExpression) ||
                     binary.IsKind(SyntaxKind.RightShiftExpression)));
                // Count direct arithmetic only; nested loops are scored separately.
                AddSite("loop_body_candidate", loop, LoopDepth(loop) + 1,
                    Math.Min(9, 1.5 + directArithmetic * 0.7),
                    "Loop body executes repeatedly; actual iteration count and work are unknown.",
                    "Profile the loop under representative input size and identify heavy callees.");
                if (LoopDepth(loop) > 0)
                    AddSite("nested_loops", loop, LoopDepth(loop) + 1,
                        9, "Nested loop: iterations may multiply with input size.",
                        "Establish actual loop bounds and measure with increasing input sizes.");
            }

            foreach (var n in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var depth = LoopDepth(n);
                if (depth > 0 && n is ObjectCreationExpressionSyntax or
                    ImplicitObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or
                    ImplicitArrayCreationExpressionSyntax)
                {
                    var type = model.GetTypeInfo(n, cancellationToken).Type;
                    var isArray = type is IArrayTypeSymbol;
                    if (isArray || type?.IsReferenceType == true)
                    {
                        var createdTypeName = type?.ToDisplayString() ?? "object";
                        var regex = createdTypeName == "System.Text.RegularExpressions.Regex";
                        AddSite(regex ? "regex_per_iteration" : "allocation_per_iteration",
                            n, depth, regex ? 11 : 4,
                            regex ? "Regex constructed within loop." :
                                "Reference object/array construction inside loop.",
                            "Measure allocated bytes per operation; escape analysis and JIT behavior may differ.");
                        allocationCandidates++;
                    }
                }

                if (depth > 0 && n is VariableDeclaratorSyntax variable &&
                    variable.Initializer?.Value is { } initializer &&
                    model.GetDeclaredSymbol(variable, cancellationToken) is ILocalSymbol localSymbol &&
                    localSymbol.Type.SpecialType == SpecialType.System_Object &&
                    model.GetTypeInfo(initializer, cancellationToken).Type?.IsValueType == true)
                {
                    AddSite("boxing_per_iteration", initializer, depth, 6,
                        "A value type is converted to object inside a loop.",
                        "Inspect IL for box and confirm allocation with a representative benchmark.");
                    allocationCandidates++;
                }

                if (depth > 0 && n is AssignmentExpressionSyntax boxingAssignment &&
                    boxingAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                    model.GetTypeInfo(boxingAssignment.Left, cancellationToken).Type?.SpecialType
                        == SpecialType.System_Object &&
                    model.GetTypeInfo(boxingAssignment.Right, cancellationToken).Type?.IsValueType == true)
                {
                    AddSite("boxing_per_iteration", boxingAssignment.Right, depth, 6,
                        "A value type is assigned to object inside a loop.",
                        "Inspect IL for box and confirm allocation with a representative benchmark.");
                    allocationCandidates++;
                }

                if (depth > 0 && n is AssignmentExpressionSyntax assignment &&
                    assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
                    model.GetTypeInfo(assignment.Left, cancellationToken).Type?.SpecialType
                        == SpecialType.System_String)
                {
                    AddSite("string_concat_per_iteration", n, depth, 8,
                        "Repeated string concatenation inside loop.",
                        "Benchmark realistic string sizes and compare with StringBuilder.");
                    allocationCandidates++;
                }

                if (n is LockStatementSyntax && depth > 0)
                    AddSite("lock_per_iteration", n, depth, 10,
                        "A lock is acquired in a repeated loop.",
                        "Profile contention and hold time under concurrent load.");

                if (n is MemberAccessExpressionSyntax access &&
                    access.Name.Identifier.ValueText == "Result")
                {
                    if (model.GetSymbolInfo(access, cancellationToken).Symbol is IPropertySymbol prop &&
                        prop.ContainingType.OriginalDefinition.ToDisplayString()
                            .StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal))
                    {
                        AddSite("blocking_task_result", n, depth, 10,
                            "Task<T>.Result may synchronously block a thread.",
                            "Profile thread-pool starvation and verify completed-task assumptions.");
                        blockingCandidates++;
                    }
                }

                if (n is not InvocationExpressionSyntax call) continue;
                if (model.GetSymbolInfo(call, cancellationToken).Symbol is not IMethodSymbol method)
                    continue;
                var methodName = method.Name;
                var typeName = method.ContainingType?.OriginalDefinition.ToDisplayString() ?? "";

                if (methodName is "Wait" && typeName.StartsWith(
                        "System.Threading.Tasks.Task", StringComparison.Ordinal)
                    || methodName is "Sleep" && typeName == "System.Threading.Thread")
                {
                    AddSite("blocking_call", call, depth, 10,
                        "Synchronous wait can block a worker thread.",
                        "Use runtime trace / thread profiling to determine blocking duration.");
                    blockingCandidates++;
                }

                if (depth > 0)
                {
                    if (typeName is "System.Linq.Enumerable" or "System.Linq.Queryable")
                    {
                        if (methodName is "ToList" or "ToArray" or "ToDictionary"
                            or "ToLookup" or "ToHashSet")
                        {
                            AddSite("linq_materialization", call, depth, 10,
                                "LINQ materialization is invoked within loop.",
                                "Measure input-dependent enumeration and allocations.");
                            enumerationCandidates++;
                        }
                        else if (methodName is "Count" or "LongCount" or "Sum" or "Average"
                            or "Aggregate" or "Max" or "Min" or "First" or "Single"
                            or "Last" or "Contains" or "All" or "Any")
                        {
                            AddSite("linq_enumeration", call, depth, 6,
                                "LINQ terminal operation is invoked inside loop.",
                                "Verify cheap ICollection/Queryable provider paths before changing it.");
                            enumerationCandidates++;
                        }
                    }
                    if (typeName == "System.Collections.Generic.List<T>" &&
                        methodName == "ToArray")
                    {
                        AddSite("collection_copy_per_iteration", call, depth, 9,
                            "List<T>.ToArray copies the collection on each iteration.",
                            "Check whether the copy is required; measure bytes allocated per iteration.");
                        allocationCandidates++;
                        enumerationCandidates++;
                    }
                    if (typeName == "System.Collections.Generic.List<T>" &&
                        methodName is ("Contains" or "IndexOf" or "Find" or "FindIndex"))
                    {
                        AddSite("list_linear_search", call, depth, 10,
                            "A List<T> search in a loop may repeat linear scans.",
                            "Measure varying list sizes; compare HashSet/Dictionary with matched semantics.");
                        enumerationCandidates++;
                    }
                    if ((typeName is "System.IO.File" or "System.IO.Directory"
                            or "System.Console" or "System.Net.Http.HttpClient")
                        && (methodName.StartsWith("Read", StringComparison.Ordinal)
                            || methodName.StartsWith("Write", StringComparison.Ordinal)
                            || methodName.StartsWith("Send", StringComparison.Ordinal)
                            || methodName.StartsWith("Get", StringComparison.Ordinal)
                            || methodName.StartsWith("Post", StringComparison.Ordinal)))
                    {
                        AddSite("io_per_iteration", call, depth, 12,
                            "Potentially costly I/O operation repeated in a loop.",
                            "Measure real workload latency, throughput and batching options.");
                        blockingCandidates++;
                    }
                }

                // A single sort/regex/serialization call may be expensive even
                // outside loops. Cost is unknown, so this remains a candidate.
                if ((typeName is "System.Array" && methodName == "Sort") ||
                    (typeName is "System.Text.Json.JsonSerializer" &&
                        methodName is "Serialize" or "Deserialize") ||
                    (typeName == "System.Text.RegularExpressions.Regex" &&
                        methodName is "Replace" or "Matches"))
                {
                    AddSite("whole_collection_work", call, depth,
                        depth > 0 ? 8 : 4,
                        "Operation may process an entire collection or text payload.",
                        "Measure representative payload sizes and runtime CPU/allocation costs.");
                    enumerationCandidates++;
                }

                if (!bySymbol.TryGetValue(method.OriginalDefinition, out var callee))
                    continue;
                if (callee.Summary.Id == definition.Summary.Id)
                {
                    AddSite("recursive_call_candidate", call, depth, 7,
                        "Recursive self-call: cumulative work depends on recursion depth and branching.",
                        "Measure recursion depth/call counts and check termination with representative inputs.");
                }
                var dispatch = method.IsVirtual || method.IsAbstract ||
                    method.ContainingType?.TypeKind == TypeKind.Interface
                    ? "possible_dynamic_dispatch" : "direct_resolved";
                ownedEdges.Add(new PendingEdge(definition.Summary.Id,
                    callee.Summary.Id, definition.Summary.File,
                    call.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    depth, dispatch));
                if (depth > 0)
                    AddSite("repeated_local_call", call, depth, 3,
                        "Another project method is invoked from a loop.",
                        "Profile callee inclusive/exclusive CPU time and actual invocation count.");
            }

            sites.AddRange(ownedSites);
            edges.AddRange(ownedEdges);
            var local = Math.Round(ownedSites.Sum(x => x.Weight), 2);
            var reasons = ownedSites.OrderByDescending(x => x.Weight)
                .Select(x => x.Explanation).Distinct().Take(4).ToArray();
            rankings.Add(new HotspotMethodRanking(
                definition.Summary, local, local, maximumDepth, nested,
                allocationCandidates, enumerationCandidates, blockingCandidates,
                ownedEdges.Count, ownedEdges.Count(e => e.Depth > 0),
                0, ownedEdges.Count,
                ownedEdges.Any(e => e.CallerId == e.CalleeId), reasons));

            void AddSite(string category, SyntaxNode site, int depth,
                double baseWeight, string explanation, string verification)
            {
                var location = site.GetLocation().GetLineSpan().StartLinePosition;
                var expression = site.ToString();
                expression = expression.Length <= 220 ? expression : expression[..220] + "...";
                var weight = Math.Round(baseWeight * Math.Pow(2,
                    Math.Clamp(depth - 1, 0, 3)), 2);
                var relative = Path.GetRelativePath(definition.ProjectDirectory,
                    definition.Summary.File).Replace('\\', '/');
                ownedSites.Add(new HotspotSite(
                    DeterministicAnalysisEngine.Fingerprint(definition.Summary.Project,
                        relative, "HOTSPOT:" + category, location.Line + 1,
                        location.Character + 1, expression),
                    definition.Summary.Id, category, definition.Summary.File,
                    location.Line + 1, location.Character + 1, depth, weight,
                    expression, explanation, verification));
            }
        }

        var outbound = edges.GroupBy(e => e.CallerId)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var scores = rankings.ToDictionary(x => x.Method.Id, x => x.LocalScore,
            StringComparer.Ordinal);
        // Bounded two-hop propagation of callee evidence. It is deliberately
        // NOT a count of runtime calls, exclusive CPU percentage or Big-O proof.
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var next = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var method in rankings)
            {
                var incoming = outbound.TryGetValue(method.Method.Id, out var calls)
                    ? calls.Sum(e => Math.Min(35, scores[e.CalleeId] * 0.23) *
                        (e.Depth > 0 ? 1.8 : 1.0))
                    : 0;
                next[method.Method.Id] = Math.Min(2000, method.LocalScore + incoming);
            }
            scores = next;
        }

        var callers = edges.GroupBy(x => x.CalleeId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var sortedMethods = rankings.Select(x => x with
        {
            SuspicionScore = Math.Round(scores[x.Method.Id], 2),
            IncomingCallSites = callers.GetValueOrDefault(x.Method.Id)
        }).OrderByDescending(x => x.SuspicionScore)
          .ThenBy(x => x.Method.Project, StringComparer.Ordinal)
          .ThenBy(x => x.Method.File, StringComparer.Ordinal)
          .ThenBy(x => x.Method.Line).ToArray();

        var sortedSites = sites.OrderByDescending(x => x.Weight)
            .ThenBy(x => x.File, StringComparer.Ordinal)
            .ThenBy(x => x.Line).ThenBy(x => x.Column)
            .ThenBy(x => x.Fingerprint, StringComparer.Ordinal).ToArray();
        var returned = sortedMethods.Take(methodLimit).ToArray();
        var selectedMethods = returned.Select(x => x.Method.Id)
            .ToHashSet(StringComparer.Ordinal);
        return new StaticHotspotReport(snapshotId, "static_candidate",
            sortedMethods.Length, sortedSites.Length,
            returned.Length, Math.Min(siteLimit, sortedSites.Length),
            sortedMethods.Length > methodLimit, sortedSites.Length > siteLimit,
            returned, sortedSites.Take(siteLimit).ToArray(),
            edges.Where(e => selectedMethods.Contains(e.CallerId))
                .OrderBy(e => e.CallerId, StringComparer.Ordinal)
                .ThenBy(e => e.Line)
                .Take(1000)
                .Select(e => new HotspotCallEdge(e.CallerId, e.CalleeId,
                    e.File, e.Line, e.Depth, e.Dispatch)).ToArray(),
            [
                "Static scores rank inspection candidates only. They cannot establish CPU hotspots without a representative runtime profile.",
                "Loop iteration counts, input sizes and virtual/reflective invocation frequencies are unknown; a short loop can be cheap even when nested.",
                "A called method's static risk is propagated two hops, not its measured execution time; recursion and dynamic dispatch may be underestimated.",
                "Source and compiler/JIT optimization can change actual allocations. Confirm with profiling and BenchmarkDotNet."
            ]);
    }

    private static bool IsLoop(SyntaxNode node) =>
        node is ForStatementSyntax or ForEachStatementSyntax or
        ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax;

    private static int LoopDepth(SyntaxNode node)
    {
        var depth = 0;
        foreach (var parent in node.Ancestors())
        {
            if (IsLoop(parent)) depth++;
            if (CallableSyntax.IsBoundary(parent))
                break;
        }
        return depth;
    }
}
