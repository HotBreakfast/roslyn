// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.Completion;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Moq;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Completion
{
    public abstract class AbstractCompletionProviderTests<TWorkspaceFixture> : TestBase, IClassFixture<TWorkspaceFixture>
        where TWorkspaceFixture : TestWorkspaceFixture, new()
    {
        protected readonly Mock<ICompletionSession> MockCompletionSession;
        protected TWorkspaceFixture WorkspaceFixture;

        protected AbstractCompletionProviderTests(TWorkspaceFixture workspaceFixture)
        {
            MockCompletionSession = new Mock<ICompletionSession>(MockBehavior.Strict);

            this.WorkspaceFixture = workspaceFixture;
        }

        public override void Dispose()
        {
            this.WorkspaceFixture.CloseTextViewAsync().Wait();
            base.Dispose();
        }

        protected static async Task<bool> CanUseSpeculativeSemanticModelAsync(Document document, int position)
        {
            var service = document.Project.LanguageServices.GetService<ISyntaxFactsService>();
            var node = (await document.GetSyntaxRootAsync()).FindToken(position).Parent;

            return !service.GetMemberBodySpanForSpeculativeBinding(node).IsEmpty;
        }

        internal CompletionServiceWithProviders GetCompletionService(Workspace workspace)
        {
            return CreateCompletionService(workspace, ImmutableArray.Create(CreateCompletionProvider()));
        }

        internal abstract CompletionServiceWithProviders CreateCompletionService(
            Workspace workspace, ImmutableArray<CompletionProvider> exclusiveProviders);

        internal static CompletionHelper GetCompletionHelper(Document document)
        {
            return CompletionHelper.GetHelper(document);
        }

        internal static async Task<CompletionContext> GetCompletionListContextAsync(
            CompletionProvider provider,
            Document document,
            int position,
            CompletionTrigger triggerInfo,
            OptionSet options = null)
        {
            options = options ?? document.Options;
            var service = document.Project.LanguageServices.GetService<CompletionService>();
            var text = await document.GetTextAsync();
            var span = service.GetDefaultItemSpan(text, position);
            var context = new CompletionContext(provider, document, position, span, triggerInfo, options, CancellationToken.None);
            await provider.ProvideCompletionsAsync(context);
            return context;
        }

        internal Task<CompletionList> GetCompletionListAsync(
            CompletionService service,
            Document document, int position, CompletionTrigger triggerInfo, OptionSet options = null)
        {
            return service.GetCompletionsAsync(document, position, triggerInfo, options: options);
        }

        private async Task CheckResultsAsync(
            Document document, int position, string expectedItemOrNull, string expectedDescriptionOrNull, bool usePreviousCharAsTrigger, bool checkForAbsence, Glyph? glyph)
        {
            var code = (await document.GetTextAsync()).ToString();

            CompletionTrigger trigger = CompletionTrigger.Default;

            if (usePreviousCharAsTrigger)
            {
                trigger = CompletionTrigger.CreateInsertionTrigger(insertedCharacter: code.ElementAt(position - 1));
            }

            var completionService = GetCompletionService(document.Project.Solution.Workspace);
            var completionList = await GetCompletionListAsync(completionService, document, position, trigger);
            var items = completionList == null ? default(ImmutableArray<CompletionItem>) : completionList.Items;

            if (checkForAbsence)
            {
                if (items == null)
                {
                    return;
                }

                if (expectedItemOrNull == null)
                {
                    Assert.Empty(items);
                }
                else
                {
                    AssertEx.None(
                        items,
                        c => CompareItems(c.DisplayText, expectedItemOrNull) &&
                            (expectedDescriptionOrNull != null ? completionService.GetDescriptionAsync(document, c).Result.Text == expectedDescriptionOrNull : true));
                }
            }
            else
            {
                if (expectedItemOrNull == null)
                {
                    Assert.NotEmpty(items);
                }
                else
                {
                    AssertEx.Any(items, c => CompareItems(c.DisplayText, expectedItemOrNull)
                        && (expectedDescriptionOrNull != null ? completionService.GetDescriptionAsync(document, c).Result.Text == expectedDescriptionOrNull : true)
                        && (glyph.HasValue ? CompletionHelper.TagsEqual(c.Tags, GlyphTags.GetTags(glyph.Value)) : true));
                }
            }
        }

        private Task VerifyAsync(string markup, string expectedItemOrNull, string expectedDescriptionOrNull, SourceCodeKind sourceCodeKind, bool usePreviousCharAsTrigger, bool checkForAbsence, bool experimental, int? glyph)
        {
            string code;
            int position;
            MarkupTestFile.GetPosition(markup.NormalizeLineEndings(), out code, out position);

            return VerifyWorkerAsync(code, position, expectedItemOrNull, expectedDescriptionOrNull, sourceCodeKind, usePreviousCharAsTrigger, checkForAbsence, experimental, glyph);
        }

        protected async Task VerifyCustomCommitProviderAsync(string markupBeforeCommit, string itemToCommit, string expectedCodeAfterCommit, SourceCodeKind? sourceCodeKind = null, char? commitChar = null)
        {
            string code;
            int position;
            MarkupTestFile.GetPosition(markupBeforeCommit.NormalizeLineEndings(), out code, out position);

            if (sourceCodeKind.HasValue)
            {
                await VerifyCustomCommitProviderWorkerAsync(code, position, itemToCommit, expectedCodeAfterCommit, sourceCodeKind.Value, commitChar);
            }
            else
            {
                await VerifyCustomCommitProviderWorkerAsync(code, position, itemToCommit, expectedCodeAfterCommit, SourceCodeKind.Regular, commitChar);
                await VerifyCustomCommitProviderWorkerAsync(code, position, itemToCommit, expectedCodeAfterCommit, SourceCodeKind.Script, commitChar);
            }
        }

        protected async Task VerifyProviderCommitAsync(string markupBeforeCommit, string itemToCommit, string expectedCodeAfterCommit,
            char? commitChar, string textTypedSoFar, SourceCodeKind? sourceCodeKind = null)
        {
            string code;
            int position;
            MarkupTestFile.GetPosition(markupBeforeCommit.NormalizeLineEndings(), out code, out position);

            expectedCodeAfterCommit = expectedCodeAfterCommit.NormalizeLineEndings();
            if (sourceCodeKind.HasValue)
            {
                await VerifyProviderCommitWorkerAsync(code, position, itemToCommit, expectedCodeAfterCommit, commitChar, textTypedSoFar, sourceCodeKind.Value);
            }
            else
            {
                await VerifyProviderCommitWorkerAsync(code, position, itemToCommit, expectedCodeAfterCommit, commitChar, textTypedSoFar, SourceCodeKind.Regular);
                await VerifyProviderCommitWorkerAsync(code, position, itemToCommit, expectedCodeAfterCommit, commitChar, textTypedSoFar, SourceCodeKind.Script);
            }
        }

        protected virtual bool CompareItems(string actualItem, string expectedItem)
        {
            return actualItem.Equals(expectedItem);
        }

        protected async Task VerifyItemExistsAsync(string markup, string expectedItem, string expectedDescriptionOrNull = null, SourceCodeKind? sourceCodeKind = null, bool usePreviousCharAsTrigger = false, bool experimental = false, int? glyph = null)
        {
            if (sourceCodeKind.HasValue)
            {
                await VerifyAsync(markup, expectedItem, expectedDescriptionOrNull, sourceCodeKind.Value, usePreviousCharAsTrigger, checkForAbsence: false, experimental: experimental, glyph: glyph);
            }
            else
            {
                await VerifyAsync(markup, expectedItem, expectedDescriptionOrNull, SourceCodeKind.Regular, usePreviousCharAsTrigger, checkForAbsence: false, experimental: experimental, glyph: glyph);
                await VerifyAsync(markup, expectedItem, expectedDescriptionOrNull, SourceCodeKind.Script, usePreviousCharAsTrigger, checkForAbsence: false, experimental: experimental, glyph: glyph);
            }
        }

        protected async Task VerifyItemIsAbsentAsync(string markup, string expectedItem, string expectedDescriptionOrNull = null, SourceCodeKind? sourceCodeKind = null, bool usePreviousCharAsTrigger = false, bool experimental = false)
        {
            if (sourceCodeKind.HasValue)
            {
                await VerifyAsync(markup, expectedItem, expectedDescriptionOrNull, sourceCodeKind.Value, usePreviousCharAsTrigger, checkForAbsence: true, experimental: experimental, glyph: null);
            }
            else
            {
                await VerifyAsync(markup, expectedItem, expectedDescriptionOrNull, SourceCodeKind.Regular, usePreviousCharAsTrigger, checkForAbsence: true, experimental: experimental, glyph: null);
                await VerifyAsync(markup, expectedItem, expectedDescriptionOrNull, SourceCodeKind.Script, usePreviousCharAsTrigger, checkForAbsence: true, experimental: experimental, glyph: null);
            }
        }

        protected async Task VerifyAnyItemExistsAsync(string markup, SourceCodeKind? sourceCodeKind = null, bool usePreviousCharAsTrigger = false, bool experimental = false)
        {
            if (sourceCodeKind.HasValue)
            {
                await VerifyAsync(markup, expectedItemOrNull: null, expectedDescriptionOrNull: null, sourceCodeKind: sourceCodeKind.Value, usePreviousCharAsTrigger: usePreviousCharAsTrigger, checkForAbsence: false, experimental: experimental, glyph: null);
            }
            else
            {
                await VerifyAsync(markup, expectedItemOrNull: null, expectedDescriptionOrNull: null, sourceCodeKind: SourceCodeKind.Regular, usePreviousCharAsTrigger: usePreviousCharAsTrigger, checkForAbsence: false, experimental: experimental, glyph: null);
                await VerifyAsync(markup, expectedItemOrNull: null, expectedDescriptionOrNull: null, sourceCodeKind: SourceCodeKind.Script, usePreviousCharAsTrigger: usePreviousCharAsTrigger, checkForAbsence: false, experimental: experimental, glyph: null);
            }
        }

        protected async Task VerifyNoItemsExistAsync(string markup, SourceCodeKind? sourceCodeKind = null, bool usePreviousCharAsTrigger = false, bool experimental = false)
        {
            if (sourceCodeKind.HasValue)
            {
                await VerifyAsync(markup, expectedItemOrNull: null, expectedDescriptionOrNull: null, sourceCodeKind: sourceCodeKind.Value, usePreviousCharAsTrigger: usePreviousCharAsTrigger, checkForAbsence: true, experimental: experimental, glyph: null);
            }
            else
            {
                await VerifyAsync(markup, expectedItemOrNull: null, expectedDescriptionOrNull: null, sourceCodeKind: SourceCodeKind.Regular, usePreviousCharAsTrigger: usePreviousCharAsTrigger, checkForAbsence: true, experimental: experimental, glyph: null);
                await VerifyAsync(markup, expectedItemOrNull: null, expectedDescriptionOrNull: null, sourceCodeKind: SourceCodeKind.Script, usePreviousCharAsTrigger: usePreviousCharAsTrigger, checkForAbsence: true, experimental: experimental, glyph: null);
            }
        }

        internal abstract CompletionProvider CreateCompletionProvider();

        /// <summary>
        /// Override this to change parameters or return without verifying anything, e.g. for script sources. Or to test in other code contexts.
        /// </summary>
        /// <param name="code">The source code (not markup).</param>
        /// <param name="expectedItemOrNull">The expected item. If this is null, verifies that *any* item shows up for this CompletionProvider (or no items show up if checkForAbsence is true).</param>
        /// <param name="expectedDescriptionOrNull">If this is null, the Description for the item is ignored.</param>
        /// <param name="usePreviousCharAsTrigger">Whether or not the previous character in markup should be used to trigger IntelliSense for this provider. If false, invokes it through the invoke IntelliSense command.</param>
        /// <param name="checkForAbsence">If true, checks for absence of a specific item (or that no items are returned from this CompletionProvider)</param>
        protected virtual async Task VerifyWorkerAsync(string code, int position, string expectedItemOrNull, string expectedDescriptionOrNull, SourceCodeKind sourceCodeKind, bool usePreviousCharAsTrigger, bool checkForAbsence, bool experimental, int? glyph)
        {
            if (experimental)
            {
                foreach (var project in (await WorkspaceFixture.GetWorkspaceAsync()).Projects)
                {
                    (await WorkspaceFixture.GetWorkspaceAsync()).OnParseOptionsChanged(project.Id, CreateExperimentalParseOptions(project.ParseOptions));
                }
            }

            Glyph? expectedGlyph = null;
            if (glyph.HasValue)
            {
                expectedGlyph = (Glyph)glyph.Value;
            }

            var document1 = await WorkspaceFixture.UpdateDocumentAsync(code, sourceCodeKind);
            await CheckResultsAsync(document1, position, expectedItemOrNull, expectedDescriptionOrNull, usePreviousCharAsTrigger, checkForAbsence, expectedGlyph);

            if (await CanUseSpeculativeSemanticModelAsync(document1, position))
            {
                var document2 = await WorkspaceFixture.UpdateDocumentAsync(code, sourceCodeKind, cleanBeforeUpdate: false);
                await CheckResultsAsync(document2, position, expectedItemOrNull, expectedDescriptionOrNull, usePreviousCharAsTrigger, checkForAbsence, expectedGlyph);
            }
        }

        protected virtual ParseOptions CreateExperimentalParseOptions(ParseOptions parseOptions)
        {
            return parseOptions;
        }

        /// <summary>
        /// Override this to change parameters or return without verifying anything, e.g. for script sources. Or to test in other code contexts.
        /// </summary>
        /// <param name="codeBeforeCommit">The source code (not markup).</param>
        /// <param name="position">Position where intellisense is invoked.</param>
        /// <param name="itemToCommit">The item to commit from the completion provider.</param>
        /// <param name="expectedCodeAfterCommit">The expected code after commit.</param>
        protected virtual async Task VerifyCustomCommitProviderWorkerAsync(string codeBeforeCommit, int position, string itemToCommit, string expectedCodeAfterCommit, SourceCodeKind sourceCodeKind, char? commitChar = null)
        {
            var document1 = await WorkspaceFixture.UpdateDocumentAsync(codeBeforeCommit, sourceCodeKind);
            await VerifyCustomCommitProviderCheckResultsAsync(document1, codeBeforeCommit, position, itemToCommit, expectedCodeAfterCommit, commitChar);

            if (await CanUseSpeculativeSemanticModelAsync(document1, position))
            {
                var document2 = await WorkspaceFixture.UpdateDocumentAsync(codeBeforeCommit, sourceCodeKind, cleanBeforeUpdate: false);
                await VerifyCustomCommitProviderCheckResultsAsync(document2, codeBeforeCommit, position, itemToCommit, expectedCodeAfterCommit, commitChar);
            }
        }

        private async Task VerifyCustomCommitProviderCheckResultsAsync(Document document, string codeBeforeCommit, int position, string itemToCommit, string expectedCodeAfterCommit, char? commitChar)
        {
            var workspace = await WorkspaceFixture.GetWorkspaceAsync();
            var textBuffer = workspace.Documents.Single().TextBuffer;

            var service = GetCompletionService(workspace);
            var items = (await GetCompletionListAsync(service, document, position, CompletionTrigger.Default)).Items;
            var firstItem = items.First(i => CompareItems(i.DisplayText, itemToCommit));

            var customCommitCompletionProvider = service.ExclusiveProviders?[0] as ICustomCommitCompletionProvider;
            if (customCommitCompletionProvider != null)
            {
                var completionRules = GetCompletionHelper(document);
                var textView = (await WorkspaceFixture.GetWorkspaceAsync()).Documents.Single().GetTextView();
                VerifyCustomCommitWorker(service, customCommitCompletionProvider, firstItem, completionRules, textView, textBuffer, codeBeforeCommit, expectedCodeAfterCommit, commitChar);
            }
            else
            {
                await VerifyCustomCommitWorkerAsync(service, document, firstItem, codeBeforeCommit, expectedCodeAfterCommit, commitChar);
            }
        }

        internal async Task VerifyCustomCommitWorkerAsync(
            CompletionServiceWithProviders service,
            Document document,
            CompletionItem completionItem,
            string codeBeforeCommit,
            string expectedCodeAfterCommit,
            char? commitChar = null)
        {
            int expectedCaretPosition;
            string actualExpectedCode = null;
            MarkupTestFile.GetPosition(expectedCodeAfterCommit, out actualExpectedCode, out expectedCaretPosition);

            if (commitChar.HasValue && !Controller.IsCommitCharacter(service.GetRules(), completionItem, commitChar.Value, string.Empty))
            {
                Assert.Equal(codeBeforeCommit, actualExpectedCode);
                return;
            }

            var commit = await service.GetChangeAsync(document, completionItem, commitChar, CancellationToken.None);

            var text = await document.GetTextAsync();
            var newText = text.WithChanges(commit.TextChanges);
            var newDoc = document.WithText(newText);
            document.Project.Solution.Workspace.TryApplyChanges(newDoc.Project.Solution);

            var textBuffer = (await WorkspaceFixture.GetWorkspaceAsync()).Documents.Single().TextBuffer;
            var textView = (await WorkspaceFixture.GetWorkspaceAsync()).Documents.Single().GetTextView();

            string actualCodeAfterCommit = textBuffer.CurrentSnapshot.AsText().ToString();
            var caretPosition = commit.NewPosition != null ? commit.NewPosition.Value : textView.Caret.Position.BufferPosition.Position;

            Assert.Equal(actualExpectedCode, actualCodeAfterCommit);
            Assert.Equal(expectedCaretPosition, caretPosition);
        }

        internal virtual void VerifyCustomCommitWorker(
            CompletionService service,
            ICustomCommitCompletionProvider customCommitCompletionProvider,
            CompletionItem completionItem,
            CompletionHelper completionRules,
            ITextView textView,
            ITextBuffer textBuffer,
            string codeBeforeCommit,
            string expectedCodeAfterCommit,
            char? commitChar = null)
        {
            int expectedCaretPosition;
            string actualExpectedCode = null;
            MarkupTestFile.GetPosition(expectedCodeAfterCommit, out actualExpectedCode, out expectedCaretPosition);

            if (commitChar.HasValue && !Controller.IsCommitCharacter(service.GetRules(), completionItem, commitChar.Value, string.Empty))
            {
                Assert.Equal(codeBeforeCommit, actualExpectedCode);
                return;
            }

            customCommitCompletionProvider.Commit(completionItem, textView, textBuffer, textView.TextSnapshot, commitChar);

            string actualCodeAfterCommit = textBuffer.CurrentSnapshot.AsText().ToString();
            var caretPosition = textView.Caret.Position.BufferPosition.Position;

            Assert.Equal(actualExpectedCode, actualCodeAfterCommit);
            Assert.Equal(expectedCaretPosition, caretPosition);
        }

        /// <summary>
        /// Override this to change parameters or return without verifying anything, e.g. for script sources. Or to test in other code contexts.
        /// </summary>
        /// <param name="codeBeforeCommit">The source code (not markup).</param>
        /// <param name="position">Position where intellisense is invoked.</param>
        /// <param name="itemToCommit">The item to commit from the completion provider.</param>
        /// <param name="expectedCodeAfterCommit">The expected code after commit.</param>
        protected virtual async Task VerifyProviderCommitWorkerAsync(string codeBeforeCommit, int position, string itemToCommit, string expectedCodeAfterCommit,
            char? commitChar, string textTypedSoFar, SourceCodeKind sourceCodeKind)
        {
            var document1 = await WorkspaceFixture.UpdateDocumentAsync(codeBeforeCommit, sourceCodeKind);
            await VerifyProviderCommitCheckResultsAsync(document1, position, itemToCommit, expectedCodeAfterCommit, commitChar, textTypedSoFar);

            if (await CanUseSpeculativeSemanticModelAsync(document1, position))
            {
                var document2 = await WorkspaceFixture.UpdateDocumentAsync(codeBeforeCommit, sourceCodeKind, cleanBeforeUpdate: false);
                await VerifyProviderCommitCheckResultsAsync(document2, position, itemToCommit, expectedCodeAfterCommit, commitChar, textTypedSoFar);
            }
        }

        private async Task VerifyProviderCommitCheckResultsAsync(
            Document document, int position, string itemToCommit, string expectedCodeAfterCommit, char? commitCharOpt, string textTypedSoFar)
        {
            var workspace = await WorkspaceFixture.GetWorkspaceAsync();
            var textBuffer = workspace.Documents.Single().TextBuffer;
            var textSnapshot = textBuffer.CurrentSnapshot.AsText();

            var service = GetCompletionService(workspace);
            var items = (await GetCompletionListAsync(service, document, position, CompletionTrigger.Default)).Items;
            var firstItem = items.First(i => CompareItems(i.DisplayText, itemToCommit));

            var completionRules = GetCompletionHelper(document);
            var commitChar = commitCharOpt ?? '\t';

            var text = await document.GetTextAsync();

            if (commitChar == '\t' || Controller.IsCommitCharacter(service.GetRules(), firstItem, commitChar, textTypedSoFar))
            {
                var textChange = await DescriptionModifyingPresentationItem.GetTextChangeAsync(
                    service, document, firstItem, commitChar);

                // Adjust TextChange to include commit character, so long as it isn't TAB.
                if (commitChar != '\t')
                {
                    textChange = new TextChange(textChange.Span, textChange.NewText.TrimEnd(commitChar) + commitChar);
                }

                text = text.WithChanges(textChange);
            }
            else
            {
                // nothing was committed, but we should insert the commit character.
                var textChange = new TextChange(new TextSpan(firstItem.Span.End, 0), commitChar.ToString());
                text = text.WithChanges(textChange);
            }

            Assert.Equal(expectedCodeAfterCommit, text.ToString());
        }

        protected async Task VerifyItemInEditorBrowsableContextsAsync(
            string markup, string referencedCode, string item, int expectedSymbolsSameSolution, int expectedSymbolsMetadataReference,
            string sourceLanguage, string referencedLanguage, bool hideAdvancedMembers = false)
        {
            await VerifyItemWithMetadataReferenceAsync(markup, referencedCode, item, expectedSymbolsMetadataReference, sourceLanguage, referencedLanguage, hideAdvancedMembers);
            await VerifyItemWithProjectReferenceAsync(markup, referencedCode, item, expectedSymbolsSameSolution, sourceLanguage, referencedLanguage, hideAdvancedMembers);

            // If the source and referenced languages are different, then they cannot be in the same project
            if (sourceLanguage == referencedLanguage)
            {
                await VerifyItemInSameProjectAsync(markup, referencedCode, item, expectedSymbolsSameSolution, sourceLanguage, hideAdvancedMembers);
            }
        }

        private Task VerifyItemWithMetadataReferenceAsync(string markup, string metadataReferenceCode, string expectedItem, int expectedSymbols,
                                                           string sourceLanguage, string referencedLanguage, bool hideAdvancedMembers)
        {
            var xmlString = string.Format(@"
<Workspace>
    <Project Language=""{0}"" CommonReferences=""true"">
        <Document FilePath=""SourceDocument"">
{1}
        </Document>
        <MetadataReferenceFromSource Language=""{2}"" CommonReferences=""true"" IncludeXmlDocComments=""true"" DocumentationMode=""Diagnose"">
            <Document FilePath=""ReferencedDocument"">
{3}
            </Document>
        </MetadataReferenceFromSource>
    </Project>
</Workspace>", sourceLanguage, SecurityElement.Escape(markup), referencedLanguage, SecurityElement.Escape(metadataReferenceCode));

            return VerifyItemWithReferenceWorkerAsync(xmlString, expectedItem, expectedSymbols, hideAdvancedMembers);
        }

        protected Task VerifyItemWithAliasedMetadataReferencesAsync(string markup, string metadataAlias, string expectedItem, int expectedSymbols,
                                                   string sourceLanguage, string referencedLanguage, bool hideAdvancedMembers)
        {
            var xmlString = string.Format(@"
<Workspace>
    <Project Language=""{0}"" CommonReferences=""true"">
        <Document FilePath=""SourceDocument"">
{1}
        </Document>
        <MetadataReferenceFromSource Language=""{2}"" CommonReferences=""true"" Aliases=""{3}, global"" IncludeXmlDocComments=""true"" DocumentationMode=""Diagnose"">
            <Document FilePath=""ReferencedDocument"">
            </Document>
        </MetadataReferenceFromSource>
    </Project>
</Workspace>", sourceLanguage, SecurityElement.Escape(markup), referencedLanguage, SecurityElement.Escape(metadataAlias));

            return VerifyItemWithReferenceWorkerAsync(xmlString, expectedItem, expectedSymbols, hideAdvancedMembers);
        }

        protected Task VerifyItemWithProjectReferenceAsync(string markup, string referencedCode, string expectedItem, int expectedSymbols, string sourceLanguage, string referencedLanguage, bool hideAdvancedMembers)
        {
            var xmlString = string.Format(@"
<Workspace>
    <Project Language=""{0}"" CommonReferences=""true"">
        <ProjectReference>ReferencedProject</ProjectReference>
        <Document FilePath=""SourceDocument"">
{1}
        </Document>
    </Project>
    <Project Language=""{2}"" CommonReferences=""true"" AssemblyName=""ReferencedProject"" IncludeXmlDocComments=""true"" DocumentationMode=""Diagnose"">
        <Document FilePath=""ReferencedDocument"">
{3}
        </Document>
    </Project>
    
</Workspace>", sourceLanguage, SecurityElement.Escape(markup), referencedLanguage, SecurityElement.Escape(referencedCode));

            return VerifyItemWithReferenceWorkerAsync(xmlString, expectedItem, expectedSymbols, hideAdvancedMembers);
        }

        private Task VerifyItemInSameProjectAsync(string markup, string referencedCode, string expectedItem, int expectedSymbols, string sourceLanguage, bool hideAdvancedMembers)
        {
            var xmlString = string.Format(@"
<Workspace>
    <Project Language=""{0}"" CommonReferences=""true"">
        <Document FilePath=""SourceDocument"">
{1}
        </Document>
        <Document FilePath=""ReferencedDocument"">
{2}
        </Document>
    </Project>
    
</Workspace>", sourceLanguage, SecurityElement.Escape(markup), SecurityElement.Escape(referencedCode));

            return VerifyItemWithReferenceWorkerAsync(xmlString, expectedItem, expectedSymbols, hideAdvancedMembers);
        }

        private async Task VerifyItemWithReferenceWorkerAsync(
            string xmlString, string expectedItem, int expectedSymbols, bool hideAdvancedMembers)
        {
            using (var testWorkspace = await TestWorkspace.CreateAsync(xmlString))
            {
                var position = testWorkspace.Documents.Single(d => d.Name == "SourceDocument").CursorPosition.Value;
                var solution = testWorkspace.CurrentSolution;
                var documentId = testWorkspace.Documents.Single(d => d.Name == "SourceDocument").Id;
                var document = solution.GetDocument(documentId);

                testWorkspace.Options = testWorkspace.Options.WithChangedOption(CompletionOptions.HideAdvancedMembers, document.Project.Language, hideAdvancedMembers);

                var triggerInfo = CompletionTrigger.Default;

                var completionService = GetCompletionService(testWorkspace);
                var completionList = await GetCompletionListAsync(completionService, document, position, triggerInfo);

                if (expectedSymbols >= 1)
                {
                    AssertEx.Any(completionList.Items, c => CompareItems(c.DisplayText, expectedItem));

                    // Throw if multiple to indicate a bad test case
                    var item = completionList.Items.Single(c => CompareItems(c.DisplayText, expectedItem));
                    var description = await completionService.GetDescriptionAsync(document, item);

                    if (expectedSymbols == 1)
                    {
                        Assert.DoesNotContain("+", description.Text, StringComparison.Ordinal);
                    }
                    else
                    {
                        Assert.Contains(GetExpectedOverloadSubstring(expectedSymbols), description.Text, StringComparison.Ordinal);
                    }
                }
                else
                {
                    if (completionList != null)
                    {
                        AssertEx.None(completionList.Items, c => CompareItems(c.DisplayText, expectedItem));
                    }
                }
            }
        }

        protected Task VerifyItemWithMscorlib45Async(string markup, string expectedItem, string expectedDescription, string sourceLanguage)
        {
            var xmlString = string.Format(@"
<Workspace>
    <Project Language=""{0}"" CommonReferencesNet45=""true""> 
        <Document FilePath=""SourceDocument"">
{1}
        </Document>
    </Project>
</Workspace>", sourceLanguage, SecurityElement.Escape(markup));

            return VerifyItemWithMscorlib45WorkerAsync(xmlString, expectedItem, expectedDescription);
        }

        private async Task VerifyItemWithMscorlib45WorkerAsync(
            string xmlString, string expectedItem, string expectedDescription)
        {
            using (var testWorkspace = await TestWorkspace.CreateAsync(xmlString))
            {
                var position = testWorkspace.Documents.Single(d => d.Name == "SourceDocument").CursorPosition.Value;
                var solution = testWorkspace.CurrentSolution;
                var documentId = testWorkspace.Documents.Single(d => d.Name == "SourceDocument").Id;
                var document = solution.GetDocument(documentId);

                var triggerInfo = CompletionTrigger.Default;
                var completionService = GetCompletionService(testWorkspace);
                var completionList = await GetCompletionListAsync(completionService, document, position, triggerInfo);

                var item = completionList.Items.FirstOrDefault(i => i.DisplayText == expectedItem);
                Assert.Equal(expectedDescription, (await completionService.GetDescriptionAsync(document, item)).Text);
            }
        }

        private const char NonBreakingSpace = (char)0x00A0;

        private string GetExpectedOverloadSubstring(int expectedSymbols)
        {
            if (expectedSymbols <= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedSymbols));
            }

            return "+" + NonBreakingSpace + (expectedSymbols - 1) + NonBreakingSpace + FeaturesResources.Overload;
        }

        protected async Task VerifyItemInLinkedFilesAsync(string xmlString, string expectedItem, string expectedDescription)
        {
            using (var testWorkspace = await TestWorkspace.CreateAsync(xmlString))
            {
                var position = testWorkspace.Documents.First().CursorPosition.Value;
                var solution = testWorkspace.CurrentSolution;
                var textContainer = testWorkspace.Documents.First().TextBuffer.AsTextContainer();
                var currentContextDocumentId = testWorkspace.GetDocumentIdInCurrentContext(textContainer);
                var document = solution.GetDocument(currentContextDocumentId);

                var triggerInfo = CompletionTrigger.Default;
                var completionService = GetCompletionService(testWorkspace);
                var completionList = await GetCompletionListAsync(completionService, document, position, triggerInfo);

                var item = completionList.Items.Single(c => c.DisplayText == expectedItem);
                Assert.NotNull(item);
                if (expectedDescription != null)
                {
                    var actualDescription = (await completionService.GetDescriptionAsync(document, item)).Text;
                    Assert.Equal(expectedDescription, actualDescription);
                }
            }
        }
    }
}
