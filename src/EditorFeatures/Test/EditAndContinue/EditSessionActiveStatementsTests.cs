﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.EditAndContinue;
using Microsoft.CodeAnalysis.Editor.UnitTests;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Moq;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Microsoft.VisualStudio.Debugger.Contracts.EditAndContinue;
using Xunit;

namespace Microsoft.CodeAnalysis.EditAndContinue.UnitTests
{
    [UseExportProvider]
    public class EditSessionActiveStatementsTests : TestBase
    {
        internal static ImmutableArray<ManagedActiveStatementDebugInfo> GetActiveStatementDebugInfos(
            string[] markedSources,
            string extension = ".cs",
            int[] methodRowIds = null,
            Guid[] modules = null,
            int[] methodVersions = null,
            int[] ilOffsets = null,
            ActiveStatementFlags[] flags = null)
        {
            IEnumerable<(TextSpan Span, int Id, SourceText Text, string DocumentName, DocumentId DocumentId)> EnumerateAllSpans()
            {
                var sourceIndex = 0;
                foreach (var markedSource in markedSources)
                {
                    var documentName = TestWorkspace.GetDefaultTestSourceDocumentName(sourceIndex, extension);
                    var documentId = DocumentId.CreateNewId(ProjectId.CreateNewId(), documentName);
                    var text = SourceText.From(markedSource);

                    foreach (var (span, id) in ActiveStatementsDescription.GetActiveSpans(markedSource))
                    {
                        yield return (span, id, text, documentName, documentId);
                    }

                    sourceIndex++;
                }
            }

            IEnumerable<ManagedActiveStatementDebugInfo> Enumerate()
            {
                var moduleId = new Guid("00000000-0000-0000-0000-000000000001");
                var threadId = new Guid("00000000-0000-0000-0000-000000000010");

                var index = 0;
                foreach (var (span, id, text, documentName, documentId) in EnumerateAllSpans().OrderBy(s => s.Id))
                {
                    yield return new ManagedActiveStatementDebugInfo(
                        new ManagedInstructionId(
                            new ManagedMethodId(
                                (modules != null) ? modules[index] : moduleId,
                                new ManagedModuleMethodId(
                                    token: 0x06000000 | (methodRowIds != null ? methodRowIds[index] : index + 1),
                                    version: (methodVersions != null) ? methodVersions[index] : 1)),
                            ilOffset: (ilOffsets != null) ? ilOffsets[index] : 0),
                        documentName: documentName,
                        sourceSpan: text.Lines.GetLinePositionSpan(span).ToSourceSpan(),
                        flags: (flags != null) ? flags[index] : ((id == 0 ? ActiveStatementFlags.IsLeafFrame : ActiveStatementFlags.IsNonLeafFrame) | ActiveStatementFlags.MethodUpToDate));

                    index++;
                }
            }

            return Enumerate().ToImmutableArray();
        }

        private sealed class Validator : IDisposable
        {
            public readonly TestWorkspace Workspace;
            public readonly EditSession EditSession;

            private static readonly TestComposition s_composition = EditorTestCompositions.EditorFeatures.AddParts(
                typeof(DummyLanguageService));

            public Validator(
                string[] markedSource,
                ImmutableArray<ManagedActiveStatementDebugInfo> activeStatements,
                ImmutableDictionary<ManagedMethodId, ImmutableArray<NonRemappableRegion>> nonRemappableRegions = null,
                Func<Solution, Solution> adjustSolution = null,
                CommittedSolution.DocumentState initialState = CommittedSolution.DocumentState.MatchesBuildOutput,
                bool openDocuments = false)
            {
                Workspace = TestWorkspace.CreateCSharp(ActiveStatementsDescription.ClearTags(markedSource), composition: s_composition, openDocuments: openDocuments);

                if (adjustSolution != null)
                {
                    Workspace.ChangeSolution(adjustSolution(Workspace.CurrentSolution));
                }

                var solution = Workspace.CurrentSolution;

                var mockDebuggerService = new MockManagedEditAndContinueDebuggerService()
                {
                    GetActiveStatementsImpl = () => activeStatements
                };

                var mockCompilationOutputsProvider = new Func<Project, CompilationOutputs>(_ => new MockCompilationOutputs(Guid.NewGuid()));

                var debuggingSession = new DebuggingSession(solution, mockCompilationOutputsProvider, SpecializedCollections.EmptyEnumerable<KeyValuePair<DocumentId, CommittedSolution.DocumentState>>());

                if (initialState != CommittedSolution.DocumentState.None)
                {
                    EditAndContinueWorkspaceServiceTests.SetDocumentsState(debuggingSession, solution, initialState);
                }

                debuggingSession.Test_SetNonRemappableRegions(nonRemappableRegions ?? ImmutableDictionary<ManagedMethodId, ImmutableArray<NonRemappableRegion>>.Empty);

                var telemetry = new EditSessionTelemetry();
                EditSession = new EditSession(debuggingSession, telemetry, mockDebuggerService);
            }

            public void Dispose()
            {
                Workspace.Dispose();
            }

            public ImmutableArray<DocumentId> GetDocumentIds()
                => (from p in Workspace.CurrentSolution.Projects
                    from d in p.DocumentIds
                    select d).ToImmutableArray();
        }

        private static string Delete(string src, string marker)
        {
            while (true)
            {
                var startStr = "/*delete" + marker;
                var endStr = "*/";
                var start = src.IndexOf(startStr);
                if (start == -1)
                {
                    return src;
                }

                var end = src.IndexOf(endStr, start + startStr.Length) + endStr.Length;
                src = src.Substring(0, start) + src.Substring(end);
            }
        }

        private static string Insert(string src, string marker)
        {
            while (true)
            {
                var startStr = "/*insert" + marker + "[";
                var endStr = "*/";

                var start = src.IndexOf(startStr);
                if (start == -1)
                {
                    return src;
                }

                var startOfLineCount = start + startStr.Length;
                var endOfLineCount = src.IndexOf(']', startOfLineCount);
                var lineCount = int.Parse(src.Substring(startOfLineCount, endOfLineCount - startOfLineCount));

                var end = src.IndexOf(endStr, endOfLineCount) + endStr.Length;

                src = src.Substring(0, start) + string.Join("", Enumerable.Repeat(Environment.NewLine, lineCount)) + src.Substring(end);
            }
        }

        private static string Update(string src, string marker)
            => Insert(Delete(src, marker), marker);

        private static string InspectActiveStatement(ActiveStatement statement)
            => $"{statement.Ordinal}: {statement.Span} flags=[{statement.Flags}] pdid={statement.PrimaryDocumentId.DebugName} docs=[{string.Join(",", statement.DocumentIds.Select(d => d.DebugName))}]";

        private static string InspectActiveStatementAndInstruction(ActiveStatement statement)
            => InspectActiveStatement(statement) + " " + statement.InstructionId.GetDebuggerDisplay();

        private static string InspectActiveStatementAndInstruction(ActiveStatement statement, SourceText text)
            => InspectActiveStatementAndInstruction(statement) + $" '{GetFirstLineText(statement.Span, text)}'";

        private static string InspectActiveStatementUpdate(ManagedActiveStatementUpdate update)
            => $"{update.Method.GetDebuggerDisplay()} IL_{update.ILOffset:X4}: {update.NewSpan.GetDebuggerDisplay()}";

        private static string InspectExceptionRegionUpdate(ManagedExceptionRegionUpdate r)
            => $"{r.Method.GetDebuggerDisplay()} | {r.NewSpan.GetDebuggerDisplay()} Delta={r.Delta}";

        private static string GetFirstLineText(LinePositionSpan span, SourceText text)
            => text.Lines[span.Start.Line].ToString().Trim();

        [Fact]
        public async Task BaseActiveStatementsAndExceptionRegions1()
        {
            var markedSource = new[]
            {
@"class Test1
{
    static void M1()
    {
        try { } finally { <AS:1>F1();</AS:1> }
    }

    static void F1()
    {
        <AS:0>Console.WriteLine(1);</AS:0>
    }
}",
@"class Test2
{
    static void M2()
    {
        try
        {
          try
          {
              <AS:3>F2();</AS:3>
          }
          catch (Exception1 e1)
          {
          }
        }
        catch (Exception2 e2)
        {
        }
    }

    static void F2()
    {
        <AS:2>Test1.M1()</AS:2>
    }

    static void Main()
    {
        try { <AS:4>M2();</AS:4> } finally { }
    }
}
"
            };

            var module1 = new Guid("11111111-1111-1111-1111-111111111111");
            var module2 = new Guid("22222222-2222-2222-2222-222222222222");

            var activeStatements = GetActiveStatementDebugInfos(markedSource,
                methodRowIds: new[] { 1, 2, 3, 4, 5 },
                ilOffsets: new[] { 1, 1, 1, 2, 3 },
                modules: new[] { module1, module1, module2, module2, module2 });

            // add an extra active statement that has no location, it should be ignored:
            activeStatements = activeStatements.Add(
                new ManagedActiveStatementDebugInfo(
                    new ManagedInstructionId(new ManagedMethodId(module: Guid.NewGuid(), token: 0x06000005, version: 1), ilOffset: 10),
                    documentName: null,
                    sourceSpan: default,
                    ActiveStatementFlags.IsNonLeafFrame));

            // add an extra active statement from project not belonging to the solution, it should be ignored:
            activeStatements = activeStatements.Add(
                new ManagedActiveStatementDebugInfo(
                    new ManagedInstructionId(new ManagedMethodId(module: Guid.NewGuid(), token: 0x06000005, version: 1), ilOffset: 10),
                    "NonRoslynDocument.mcpp",
                    new SourceSpan(1, 1, 1, 10),
                    ActiveStatementFlags.IsNonLeafFrame));

            // Add an extra active statement from language that doesn't support Roslyn EnC should be ignored:
            // See https://github.com/dotnet/roslyn/issues/24408 for test scenario.
            activeStatements = activeStatements.Add(
                new ManagedActiveStatementDebugInfo(
                    new ManagedInstructionId(new ManagedMethodId(module: Guid.NewGuid(), token: 0x06000005, version: 1), ilOffset: 10),
                    "a.dummy",
                    new SourceSpan(1, 1, 1, 10),
                    ActiveStatementFlags.IsNonLeafFrame));

            var adjustSolution = new Func<Solution, Solution>(solution =>
            {
                var project = solution.AddProject("dummy_proj", "dummy_proj", DummyLanguageService.LanguageName);
                return project.Solution.AddDocument(DocumentId.CreateNewId(project.Id, DummyLanguageService.LanguageName), "a.dummy", "");
            });

            using var validator = new Validator(markedSource, activeStatements, adjustSolution: adjustSolution);
            var baseActiveStatementsMap = await validator.EditSession.BaseActiveStatements.GetValueAsync(CancellationToken.None).ConfigureAwait(false);
            var docs = validator.GetDocumentIds();

            // Active Statements

            var statements = baseActiveStatementsMap.InstructionMap.Values.OrderBy(v => v.Ordinal).ToArray();
            AssertEx.Equal(new[]
            {
                "0: (9,14)-(9,35) flags=[IsLeafFrame, MethodUpToDate] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000001 v1 IL_0001",
                "1: (4,32)-(4,37) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000002 v1 IL_0001",
                "2: (21,14)-(21,24) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test2.cs docs=[test2.cs] mvid=22222222-2222-2222-2222-222222222222 0x06000003 v1 IL_0001",
                "3: (8,20)-(8,25) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test2.cs docs=[test2.cs] mvid=22222222-2222-2222-2222-222222222222 0x06000004 v1 IL_0002",
                "4: (26,20)-(26,25) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test2.cs docs=[test2.cs] mvid=22222222-2222-2222-2222-222222222222 0x06000005 v1 IL_0003"
            }, statements.Select(InspectActiveStatementAndInstruction));

            // Active Statements per document

            Assert.Equal(2, baseActiveStatementsMap.DocumentMap.Count);

            AssertEx.Equal(new[]
            {
                "0: (9,14)-(9,35) flags=[IsLeafFrame, MethodUpToDate] pdid=test1.cs docs=[test1.cs]",
                "1: (4,32)-(4,37) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs]"
            }, baseActiveStatementsMap.DocumentMap[docs[0]].Select(InspectActiveStatement));

            AssertEx.Equal(new[]
            {
                "2: (21,14)-(21,24) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test2.cs docs=[test2.cs]",
                "3: (8,20)-(8,25) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test2.cs docs=[test2.cs]",
                "4: (26,20)-(26,25) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test2.cs docs=[test2.cs]"
            }, baseActiveStatementsMap.DocumentMap[docs[1]].Select(InspectActiveStatement));

            // Exception Regions

            var baseExceptionRegions = await validator.EditSession.GetBaseActiveExceptionRegionsAsync(validator.Workspace.CurrentSolution, CancellationToken.None).ConfigureAwait(false);

            AssertEx.Equal(new[]
            {
                "[]",
                "[(4,8)-(4,46)]",
                "[]",
                "[(14,8)-(16,9),(10,10)-(12,11)]",
                "[(26,35)-(26,46)]"
            }, baseExceptionRegions.Select(r => "[" + string.Join(",", r.Spans) + "]"));

            // GetActiveStatementAndExceptionRegionSpans

            // Assume 2 updates in Project2:
            //   Test2.M2: adding a line in front of try-catch.
            //   Test2.F2: moving the entire method 2 lines down.

            static LinePositionSpan AddDelta(LinePositionSpan span, int lineDelta)
                => new LinePositionSpan(new LinePosition(span.Start.Line + lineDelta, span.Start.Character), new LinePosition(span.End.Line + lineDelta, span.End.Character));

            var newActiveStatementsInChangedDocuments = ImmutableArray.Create(
                (
                    docs[1],

                    ImmutableArray.Create(
                        statements[2].WithSpan(AddDelta(statements[2].Span, +2)),
                        statements[3].WithSpan(AddDelta(statements[3].Span, +1)),
                        statements[4]),

                    ImmutableArray.Create(
                        baseExceptionRegions[2].Spans,
                        baseExceptionRegions[3].Spans.SelectAsArray(es => AddDelta(es, +1)),
                        baseExceptionRegions[4].Spans)
                )
            );

            EditSession.GetActiveStatementAndExceptionRegionSpans(
                module2,
                baseActiveStatementsMap,
                baseExceptionRegions,
                updatedMethodTokens: ImmutableArray.Create(0x06000004), // contains only recompiled methods in the project we are interested in (module2)
                ImmutableDictionary<ManagedMethodId, ImmutableArray<NonRemappableRegion>>.Empty,
                newActiveStatementsInChangedDocuments,
                out var activeStatementsInUpdatedMethods,
                out var nonRemappableRegions,
                out var exceptionRegionUpdates);

            AssertEx.Equal(new[]
            {
                "0x06000004 v1 | AS (8,20)-(8,25) δ=1",
                "0x06000004 v1 | ER (14,8)-(16,9) δ=1",
                "0x06000004 v1 | ER (10,10)-(12,11) δ=1"
            }, nonRemappableRegions.Select(r => $"{r.Method.GetDebuggerDisplay()} | {r.Region.GetDebuggerDisplay()}"));

            AssertEx.Equal(new[]
            {
                "0x06000004 v1 | (15,8)-(17,9) Delta=-1",
                "0x06000004 v1 | (11,10)-(13,11) Delta=-1"
            }, exceptionRegionUpdates.Select(InspectExceptionRegionUpdate));

            AssertEx.Equal(new[]
            {
                "0x06000004 v1 IL_0002: (9,20)-(9,25)"
            }, activeStatementsInUpdatedMethods.Select(InspectActiveStatementUpdate));
        }

        [Fact, WorkItem(24439, "https://github.com/dotnet/roslyn/issues/24439")]
        public async Task BaseActiveStatementsAndExceptionRegions2()
        {
            var baseSource =
@"class Test
{
    static void F1()
    {   
        try
        {
            <AS:0>F2();</AS:0>
        }
        catch (Exception) {
            Console.WriteLine(1);
            Console.WriteLine(2);
            Console.WriteLine(3);
        }
        /*insert1[1]*/
    }

    static void F2()
    {
        <AS:1>throw new Exception();</AS:1>
    }
}";
            var updatedSource = Update(baseSource, "1");

            var module1 = new Guid("11111111-1111-1111-1111-111111111111");
            var baseText = SourceText.From(baseSource);
            var updatedText = SourceText.From(updatedSource);

            var baseActiveStatementInfos = GetActiveStatementDebugInfos(new[] { baseSource },
                modules: new[] { module1, module1 },
                methodVersions: new[] { 1, 1 },
                flags: new[]
                {
                    ActiveStatementFlags.MethodUpToDate | ActiveStatementFlags.IsNonLeafFrame, // F1
                    ActiveStatementFlags.MethodUpToDate | ActiveStatementFlags.IsLeafFrame,    // F2
                });

            using var validator = new Validator(new[] { baseSource }, baseActiveStatementInfos);
            var baseActiveStatementMap = await validator.EditSession.BaseActiveStatements.GetValueAsync(CancellationToken.None).ConfigureAwait(false);
            var docs = validator.GetDocumentIds();

            // Active Statements

            var baseActiveStatements = baseActiveStatementMap.InstructionMap.Values.OrderBy(v => v.Ordinal).ToArray();

            AssertEx.Equal(new[]
            {
                "0: (6,18)-(6,23) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000001 v1 IL_0000 '<AS:0>F2();</AS:0>'",
                "1: (18,14)-(18,36) flags=[IsLeafFrame, MethodUpToDate] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000002 v1 IL_0000 '<AS:1>throw new Exception();</AS:1>'"
            }, baseActiveStatements.Select(s => InspectActiveStatementAndInstruction(s, baseText)));

            // Exception Regions

            var baseExceptionRegions = await validator.EditSession.GetBaseActiveExceptionRegionsAsync(validator.Workspace.CurrentSolution, CancellationToken.None).ConfigureAwait(false);

            // Note that the spans correspond to the base snapshot (V2). 
            AssertEx.Equal(new[]
            {
                "[(8,8)-(12,9) 'catch (Exception) {']",
                "[]",
            }, baseExceptionRegions.Select(r => "[" + string.Join(", ", r.Spans.Select(s => $"{s} '{GetFirstLineText(s, baseText)}'")) + "]"));

            // GetActiveStatementAndExceptionRegionSpans

            var newActiveStatementsInChangedDocuments = ImmutableArray.Create(
                (
                    docs[0],

                    ImmutableArray.Create(
                        baseActiveStatements[0],
                        baseActiveStatements[1].WithSpan(baseActiveStatements[1].Span.AddLineDelta(+1))),

                    ImmutableArray.Create(
                        baseExceptionRegions[0].Spans,
                        baseExceptionRegions[1].Spans)
                )
            );

            EditSession.GetActiveStatementAndExceptionRegionSpans(
                module1,
                baseActiveStatementMap,
                baseExceptionRegions,
                updatedMethodTokens: ImmutableArray.Create(0x06000001), // F1
                ImmutableDictionary<ManagedMethodId, ImmutableArray<NonRemappableRegion>>.Empty,
                newActiveStatementsInChangedDocuments,
                out var activeStatementsInUpdatedMethods,
                out var nonRemappableRegions,
                out var exceptionRegionUpdates);

            // although the span has not changed the method has, so we need to add corresponding non-remappable regions
            AssertEx.Equal(new[]
            {
                "0x06000001 v1 | AS (6,18)-(6,23) δ=0",
                "0x06000001 v1 | ER (8,8)-(12,9) δ=0",
            }, nonRemappableRegions.OrderBy(r => r.Region.Span.Start.Line).Select(r => $"{r.Method.GetDebuggerDisplay()} | {r.Region.GetDebuggerDisplay()}"));

            AssertEx.Equal(new[]
            {
                "0x06000001 v1 | (8,8)-(12,9) Delta=0",
            }, exceptionRegionUpdates.Select(InspectExceptionRegionUpdate));

            AssertEx.Equal(new[]
            {
                "0x06000001 v1 IL_0000: (6,18)-(6,23) '<AS:0>F2();</AS:0>'"
            }, activeStatementsInUpdatedMethods.Select(update => $"{InspectActiveStatementUpdate(update)} '{GetFirstLineText(update.NewSpan.ToLinePositionSpan(), updatedText)}'"));
        }

        [Fact]
        public async Task BaseActiveStatementsAndExceptionRegions_OutOfSyncDocuments()
        {
            var markedSource = new[]
            {
@"class C
{
    static void M()
    {
        try 
        {
            <AS:0>M();</AS:0>
        }
        catch (Exception e)
        {
        }
    }
}"
            };

            var thread1 = Guid.NewGuid();

            // Thread1 stack trace: F (AS:0 leaf)

            var activeStatements = GetActiveStatementDebugInfos(
                markedSource,
                methodRowIds: new[] { 1 },
                ilOffsets: new[] { 1 },
                flags: new[]
                {
                    ActiveStatementFlags.IsLeafFrame | ActiveStatementFlags.MethodUpToDate
                });

            using var validator = new Validator(markedSource, activeStatements, initialState: CommittedSolution.DocumentState.OutOfSync);
            var baseActiveStatementMap = await validator.EditSession.BaseActiveStatements.GetValueAsync(CancellationToken.None).ConfigureAwait(false);
            var docs = validator.GetDocumentIds();

            // Active Statements - available in out-of-sync documents, as they reflect the state of the debuggee and not the base document content

            Assert.Single(baseActiveStatementMap.DocumentMap);

            AssertEx.Equal(new[]
            {
                "0: (6,18)-(6,22) flags=[IsLeafFrame, MethodUpToDate] pdid=test1.cs docs=[test1.cs]",
            }, baseActiveStatementMap.DocumentMap[docs[0]].Select(InspectActiveStatement));

            Assert.Equal(1, baseActiveStatementMap.InstructionMap.Count);

            var s = baseActiveStatementMap.InstructionMap.Values.OrderBy(v => v.InstructionId.Method.Token).Single();
            Assert.Equal(0x06000001, s.InstructionId.Method.Token);
            Assert.Equal(0, s.PrimaryDocumentOrdinal);
            Assert.Equal(docs[0], s.DocumentIds.Single());
            Assert.True(s.IsLeaf);

            // Exception Regions - not available in out-of-sync documents as we need the content of the base document to calculate them

            var baseExceptionRegions = await validator.EditSession.GetBaseActiveExceptionRegionsAsync(validator.Workspace.CurrentSolution, CancellationToken.None).ConfigureAwait(false);

            AssertEx.Equal(new[]
            {
                "out-of-sync"
            }, baseExceptionRegions.Select(r => r.Spans.IsDefault ? "out-of-sync" : "[" + string.Join(",", r.Spans) + "]"));

            // document got synchronized:
            validator.EditSession.DebuggingSession.LastCommittedSolution.Test_SetDocumentState(docs[0], CommittedSolution.DocumentState.MatchesBuildOutput);

            baseExceptionRegions = await validator.EditSession.GetBaseActiveExceptionRegionsAsync(validator.Workspace.CurrentSolution, CancellationToken.None).ConfigureAwait(false);

            AssertEx.Equal(new[]
            {
                "[]"
            }, baseExceptionRegions.Select(r => r.Spans.IsDefault ? "out-of-sync" : "[" + string.Join(",", r.Spans) + "]"));
        }

        [Fact]
        public async Task BaseActiveStatementsAndExceptionRegions_WithInitialNonRemappableRegions()
        {
            var markedSourceV1 =
@"class Test
{
    static void F1()
    {
        try
        {
            <AS:0>M();</AS:0>
        }
        <ER:0.0>catch
        {
        }</ER:0.0>
    }

    static void F2()
    {   /*delete2
      */try
        {
        }
        <ER:1.0>catch
        {
            <AS:1>M();</AS:1>
        }</ER:1.0>/*insert2[1]*/
    }

    static void F3()
    {   
        try
        {
            try 
            {   /*delete1
              */<AS:2>M();</AS:2>/*insert1[3]*/
            }
            <ER:2.0>finally
            {
            }</ER:2.0>
        }
        <ER:2.1>catch
        {
        }</ER:2.1>
/*delete1

*/  }

    static void F4()
    {   /*insert1[1]*//*insert2[2]*/
        try
        {
            try
            {
            }
            <ER:3.0>catch
            {
                <AS:3>M();</AS:3>
            }</ER:3.0>
        }
        <ER:3.1>catch
        {
        }</ER:3.1>
    }
}";
            var markedSourceV2 = Update(markedSourceV1, "1");
            var markedSourceV3 = Update(markedSourceV2, "2");

            var module1 = new Guid("11111111-1111-1111-1111-111111111111");
            var sourceTextV1 = SourceText.From(markedSourceV1);
            var sourceTextV2 = SourceText.From(markedSourceV2);
            var sourceTextV3 = SourceText.From(markedSourceV3);

            var activeStatementsPreRemap = GetActiveStatementDebugInfos(new[] { markedSourceV1 },
                modules: new[] { module1, module1, module1, module1 },
                methodVersions: new[] { 2, 2, 1, 1 }, // method F3 and F4 were not remapped
                flags: new[]
                {
                    ActiveStatementFlags.MethodUpToDate | ActiveStatementFlags.IsNonLeafFrame, // F1
                    ActiveStatementFlags.MethodUpToDate | ActiveStatementFlags.IsNonLeafFrame, // F2
                    ActiveStatementFlags.None | ActiveStatementFlags.IsNonLeafFrame,           // F3
                    ActiveStatementFlags.None | ActiveStatementFlags.IsNonLeafFrame,           // F4
                });

            var exceptionSpans = ActiveStatementsDescription.GetExceptionRegions(markedSourceV1, activeStatementsPreRemap.Length);

            var spanPreRemap2 = activeStatementsPreRemap[2].SourceSpan.ToLinePositionSpan();
            var erPreRemap20 = sourceTextV1.Lines.GetLinePositionSpan(exceptionSpans[2][0]);
            var erPreRemap21 = sourceTextV1.Lines.GetLinePositionSpan(exceptionSpans[2][1]);
            var spanPreRemap3 = activeStatementsPreRemap[3].SourceSpan.ToLinePositionSpan();
            var erPreRemap30 = sourceTextV1.Lines.GetLinePositionSpan(exceptionSpans[3][0]);
            var erPreRemap31 = sourceTextV1.Lines.GetLinePositionSpan(exceptionSpans[3][1]);

            // Assume that the following edits have been made to F3 and F4 and set up non-remappable regions mapping
            // from the pre-remap spans of AS:2 and AS:3 to their current location.
            var initialNonRemappableRegions = new Dictionary<ManagedMethodId, ImmutableArray<NonRemappableRegion>>
            {
                { new ManagedMethodId(module1, 0x06000003, 1), ImmutableArray.Create(
                    // move AS:2 one line up:
                    new NonRemappableRegion(spanPreRemap2, lineDelta: -1, isExceptionRegion: false),
                    // move ER:2.0 and ER:2.1 two lines down:
                    new NonRemappableRegion(erPreRemap20, lineDelta: +2, isExceptionRegion: true),
                    new NonRemappableRegion(erPreRemap21, lineDelta: +2, isExceptionRegion: true)) },
                { new ManagedMethodId(module1, 0x06000004, 1), ImmutableArray.Create(
                    // move AS:3 one line down:
                    new NonRemappableRegion(spanPreRemap3, lineDelta: +1, isExceptionRegion: false),
                    // move ER:3.0 and ER:3.1 one line down:
                    new NonRemappableRegion(erPreRemap30, lineDelta: +1, isExceptionRegion: true),
                    new NonRemappableRegion(erPreRemap31, lineDelta: +1, isExceptionRegion: true)) }
            }.ToImmutableDictionary();

            using var validator = new Validator(new[] { markedSourceV2 }, activeStatementsPreRemap, initialNonRemappableRegions);
            var baseActiveStatementMap = await validator.EditSession.BaseActiveStatements.GetValueAsync(CancellationToken.None).ConfigureAwait(false);
            var docs = validator.GetDocumentIds();

            // Active Statements

            var baseActiveStatements = baseActiveStatementMap.InstructionMap.Values.OrderBy(v => v.Ordinal).ToArray();

            // Note that the spans of AS:2 and AS:3 correspond to the base snapshot (V2).
            AssertEx.Equal(new[]
            {
                "0: (6,18)-(6,22) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000001 v2 IL_0000 '<AS:0>M();</AS:0>'",
                "1: (20,18)-(20,22) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000002 v2 IL_0000 '<AS:1>M();</AS:1>'",
                "2: (29,22)-(29,26) flags=[IsNonLeafFrame] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000003 v1 IL_0000 '{   <AS:2>M();</AS:2>'",
                "3: (53,22)-(53,26) flags=[IsNonLeafFrame] pdid=test1.cs docs=[test1.cs] mvid=11111111-1111-1111-1111-111111111111 0x06000004 v1 IL_0000 '<AS:3>M();</AS:3>'"
            }, baseActiveStatements.Select(s => InspectActiveStatementAndInstruction(s, sourceTextV2)));

            // Exception Regions

            var baseExceptionRegions = await validator.EditSession.GetBaseActiveExceptionRegionsAsync(validator.Workspace.CurrentSolution, CancellationToken.None).ConfigureAwait(false);

            // Note that the spans correspond to the base snapshot (V2). 
            AssertEx.Equal(new[]
            {
                "[(8,16)-(10,9) '<ER:0.0>catch']",
                "[(18,16)-(21,9) '<ER:1.0>catch']",
                "[(38,16)-(40,9) '<ER:2.1>catch', (34,20)-(36,13) '<ER:2.0>finally']",
                "[(56,16)-(58,9) '<ER:3.1>catch', (51,20)-(54,13) '<ER:3.0>catch']",
            }, baseExceptionRegions.Select(r => "[" + string.Join(", ", r.Spans.Select(s => $"{s} '{GetFirstLineText(s, sourceTextV2)}'")) + "]"));

            // GetActiveStatementAndExceptionRegionSpans

            // Assume 2 more updates:
            //   F2: Move 'try' one line up (a new non-remappable entries will be added)
            //   F4: Insert 2 new lines before the first 'try' (an existing non-remappable entries will be updated)
            var newActiveStatementsInChangedDocuments = ImmutableArray.Create(
                (
                    docs[0],

                    ImmutableArray.Create(
                        baseActiveStatements[0],
                        baseActiveStatements[1].WithSpan(baseActiveStatements[1].Span.AddLineDelta(-1)),
                        baseActiveStatements[2],
                        baseActiveStatements[3].WithSpan(baseActiveStatements[3].Span.AddLineDelta(+2))),

                    ImmutableArray.Create(
                        baseExceptionRegions[0].Spans,
                        baseExceptionRegions[1].Spans.SelectAsArray(es => es.AddLineDelta(-1)),
                        baseExceptionRegions[2].Spans,
                        baseExceptionRegions[3].Spans.SelectAsArray(es => es.AddLineDelta(+2)))
                )
            );

            EditSession.GetActiveStatementAndExceptionRegionSpans(
                module1,
                baseActiveStatementMap,
                baseExceptionRegions,
                updatedMethodTokens: ImmutableArray.Create(0x06000002, 0x06000004), // F2, F4
                initialNonRemappableRegions,
                newActiveStatementsInChangedDocuments,
                out var activeStatementsInUpdatedMethods,
                out var nonRemappableRegions,
                out var exceptionRegionUpdates);

            // Note: Since no method have been remapped yet all the following spans are in their pre-remap locations: 
            AssertEx.Equal(new[]
            {
                "0x06000002 v2 | ER (18,16)-(21,9) δ=-1",
                "0x06000002 v2 | AS (20,18)-(20,22) δ=-1",
                "0x06000003 v1 | AS (30,22)-(30,26) δ=-1", // AS:2 moved -1 in first edit, 0 in second
                "0x06000003 v1 | ER (32,20)-(34,13) δ=2",  // ER:2.0 moved +2 in first edit, 0 in second
                "0x06000003 v1 | ER (36,16)-(38,9) δ=2",   // ER:2.0 moved +2 in first edit, 0 in second
                "0x06000004 v1 | ER (50,20)-(53,13) δ=3",  // ER:3.0 moved +1 in first edit, +2 in second              
                "0x06000004 v1 | AS (52,22)-(52,26) δ=3",  // AS:3 moved +1 in first edit, +2 in second
                "0x06000004 v1 | ER (55,16)-(57,9) δ=3",   // ER:3.1 moved +1 in first edit, +2 in second     
            }, nonRemappableRegions.OrderBy(r => r.Region.Span.Start.Line).Select(r => $"{r.Method.GetDebuggerDisplay()} | {r.Region.GetDebuggerDisplay()}"));

            AssertEx.Equal(new[]
            {
                "0x06000002 v2 | (17,16)-(20,9) Delta=1",
                "0x06000003 v1 | (34,20)-(36,13) Delta=-2",
                "0x06000003 v1 | (38,16)-(40,9) Delta=-2",
                "0x06000004 v1 | (53,20)-(56,13) Delta=-3",
                "0x06000004 v1 | (58,16)-(60,9) Delta=-3",
            }, exceptionRegionUpdates.OrderBy(r => r.NewSpan.StartLine).Select(InspectExceptionRegionUpdate));

            AssertEx.Equal(new[]
            {
                "0x06000002 v2 IL_0000: (19,18)-(19,22) '<AS:1>M();</AS:1>'",
                "0x06000004 v1 IL_0000: (55,22)-(55,26) '<AS:3>M();</AS:3>'"
            }, activeStatementsInUpdatedMethods.Select(update => $"{InspectActiveStatementUpdate(update)} '{GetFirstLineText(update.NewSpan.ToLinePositionSpan(), sourceTextV3)}'"));
        }

        [Fact]
        public async Task BaseActiveStatementsAndExceptionRegions_Recursion()
        {
            var markedSource = new[]
            {
@"class C
{
    static void M()
    {
        try 
        {
            <AS:1>M();</AS:1>
        }
        catch (Exception e)
        {
        }
    }

    static void F()
    {
        <AS:0>M();</AS:0>
    }
}"
            };

            var thread1 = Guid.NewGuid();
            var thread2 = Guid.NewGuid();

            // Thread1 stack trace: F (AS:0), M (AS:1 leaf)
            // Thread2 stack trace: F (AS:0), M (AS:1), M (AS:1 leaf)

            var activeStatements = GetActiveStatementDebugInfos(
                markedSource,
                methodRowIds: new[] { 1, 2 },
                ilOffsets: new[] { 1, 1 },
                flags: new[]
                {
                    ActiveStatementFlags.IsNonLeafFrame | ActiveStatementFlags.NonUserCode | ActiveStatementFlags.PartiallyExecuted | ActiveStatementFlags.MethodUpToDate,
                    ActiveStatementFlags.IsNonLeafFrame | ActiveStatementFlags.IsLeafFrame | ActiveStatementFlags.MethodUpToDate
                });

            using var validator = new Validator(markedSource, activeStatements);
            var baseActiveStatementMap = await validator.EditSession.BaseActiveStatements.GetValueAsync(CancellationToken.None).ConfigureAwait(false);
            var docs = validator.GetDocumentIds();

            // Active Statements

            Assert.Equal(1, baseActiveStatementMap.DocumentMap.Count);

            AssertEx.Equal(new[]
            {
                "0: (15,14)-(15,18) flags=[PartiallyExecuted, NonUserCode, MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs]",
                "1: (6,18)-(6,22) flags=[IsLeafFrame, MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs]",
            }, baseActiveStatementMap.DocumentMap[docs[0]].Select(InspectActiveStatement));

            Assert.Equal(2, baseActiveStatementMap.InstructionMap.Count);

            var statements = baseActiveStatementMap.InstructionMap.Values.OrderBy(v => v.InstructionId.Method.Token).ToArray();
            var s = statements[0];
            Assert.Equal(0x06000001, s.InstructionId.Method.Token);
            Assert.Equal(0, s.PrimaryDocumentOrdinal);
            Assert.Equal(docs[0], s.DocumentIds.Single());
            Assert.True(s.IsNonLeaf);

            s = statements[1];
            Assert.Equal(0x06000002, s.InstructionId.Method.Token);
            Assert.Equal(1, s.PrimaryDocumentOrdinal);
            Assert.Equal(docs[0], s.DocumentIds.Single());
            Assert.True(s.IsLeaf);
            Assert.True(s.IsNonLeaf);

            // Exception Regions

            var baseExceptionRegions = await validator.EditSession.GetBaseActiveExceptionRegionsAsync(validator.Workspace.CurrentSolution, CancellationToken.None).ConfigureAwait(false);

            AssertEx.Equal(new[]
            {
                "[]",
                "[(8,8)-(10,9)]"
            }, baseExceptionRegions.Select(r => "[" + string.Join(",", r.Spans) + "]"));
        }

        [Fact, WorkItem(24320, "https://github.com/dotnet/roslyn/issues/24320")]
        public async Task BaseActiveStatementsAndExceptionRegions_LinkedDocuments()
        {
            var markedSource = new[]
            {
@"class Test1
{
    static void Main() => <AS:2>Project2::Test1.F();</AS:2>
    static void F() => <AS:1>Project4::Test2.M();</AS:1>
}",
@"
class Test2
{
    static void M() => <AS:0>Console.WriteLine();</AS:0>
}"
            };

            var module1 = Guid.NewGuid();
            var module2 = Guid.NewGuid();
            var module4 = Guid.NewGuid();

            var activeStatements = GetActiveStatementDebugInfos(
                markedSource,
                methodRowIds: new[] { 1, 2, 1 },
                modules: new[] { module4, module2, module1 });

            // Project1: Test1.cs [AS 2], Test2.cs      
            // Project2: Test1.cs (link from P1) [AS 1]
            // Project3: Test1.cs (link from P1)
            // Project4: Test2.cs (link from P1) [AS 0]

            var adjustSolution = new Func<Solution, Solution>(solution =>
            {
                var project1 = solution.Projects.Single();
                var doc1 = project1.Documents.First();
                var doc2 = project1.Documents.Skip(1).First();
                Assert.True(doc1.TryGetText(out var text1));
                Assert.True(doc2.TryGetText(out var text2));

                void AddProjectAndLinkDocument(string projectName, Document doc, SourceText text)
                {
                    var p = solution.AddProject(projectName, projectName, "C#");
                    solution = p.Solution.AddDocument(DocumentId.CreateNewId(p.Id, projectName + "->" + doc.Name), doc.Name, text, filePath: doc.FilePath);
                }

                AddProjectAndLinkDocument("Project2", doc1, text1);
                AddProjectAndLinkDocument("Project3", doc1, text1);
                AddProjectAndLinkDocument("Project4", doc2, text2);
                return solution;
            });

            using var validator = new Validator(markedSource, activeStatements, adjustSolution: adjustSolution, openDocuments: true);

            var baseActiveStatementsMap = await validator.EditSession.BaseActiveStatements.GetValueAsync(CancellationToken.None).ConfigureAwait(false);
            var docs = validator.GetDocumentIds();

            // Active Statements

            var documentMap = baseActiveStatementsMap.DocumentMap;

            Assert.Equal(5, docs.Length);
            Assert.Equal(5, documentMap.Count);

            // TODO: currently we associate all linked documents to the AS regardless of whether they belong to a project that matches the AS module.
            // https://github.com/dotnet/roslyn/issues/24320

            AssertEx.Equal(new[]
            {
                "1: (3,29)-(3,49) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs,Project2->test1.cs,Project3->test1.cs]",
                "2: (2,32)-(2,52) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs,Project2->test1.cs,Project3->test1.cs]"
            }, documentMap[docs[0]].Select(InspectActiveStatement));

            AssertEx.Equal(new[]
            {
                "0: (3,29)-(3,49) flags=[IsLeafFrame, MethodUpToDate] pdid=test2.cs docs=[test2.cs,Project4->test2.cs]",
            }, documentMap[docs[1]].Select(InspectActiveStatement));

            AssertEx.Equal(new[]
            {
                "1: (3,29)-(3,49) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs,Project2->test1.cs,Project3->test1.cs]",
                "2: (2,32)-(2,52) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs,Project2->test1.cs,Project3->test1.cs]"
            }, documentMap[docs[2]].Select(InspectActiveStatement));

            AssertEx.Equal(new[]
            {
                "1: (3,29)-(3,49) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs,Project2->test1.cs,Project3->test1.cs]",
                "2: (2,32)-(2,52) flags=[MethodUpToDate, IsNonLeafFrame] pdid=test1.cs docs=[test1.cs,Project2->test1.cs,Project3->test1.cs]"
            }, documentMap[docs[3]].Select(InspectActiveStatement));

            AssertEx.Equal(new[]
            {
                "0: (3,29)-(3,49) flags=[IsLeafFrame, MethodUpToDate] pdid=test2.cs docs=[test2.cs,Project4->test2.cs]",
            }, documentMap[docs[4]].Select(InspectActiveStatement));

            Assert.Equal(3, baseActiveStatementsMap.InstructionMap.Count);

            var statements = baseActiveStatementsMap.InstructionMap.Values.OrderBy(v => v.Ordinal).ToArray();
            var s = statements[0];
            Assert.Equal(0x06000001, s.InstructionId.Method.Token);
            Assert.Equal(module4, s.InstructionId.Method.Module);

            s = statements[1];
            Assert.Equal(0x06000002, s.InstructionId.Method.Token);
            Assert.Equal(module2, s.InstructionId.Method.Module);

            s = statements[2];
            Assert.Equal(0x06000001, s.InstructionId.Method.Token);
            Assert.Equal(module1, s.InstructionId.Method.Module);
        }
    }
}
