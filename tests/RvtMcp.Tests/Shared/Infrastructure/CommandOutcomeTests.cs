using System;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class CommandOutcomeTests
    {
        // Same shape BatchExecuteHandler returns: new { results = outcome.Results, rolledBack }.
        private static object BatchData(bool continueOnError, params string[] commands)
        {
            var arr = new JArray();
            foreach (var name in commands)
                arr.Add(new JObject { ["command"] = name });

            var outcome = BatchExecutor.Run(arr, continueOnError, (name, _) =>
                name == "bad"
                    ? BatchExecutor.InvokeResult.Fail("intentional failure")
                    : name == "partial"
                        ? BatchExecutor.InvokeResult.Ok(new { failedCount = 2 })
                        : BatchExecutor.InvokeResult.Ok(new { elementId = 1 }));
            var rolledBack = outcome.AnyFailed && !continueOnError;
            return new { results = outcome.Results, rolledBack };
        }

        [Fact]
        public void Plain_success_passes_through()
        {
            var o = CommandOutcome.Normalize("create_level", true, new { elementId = 1 }, null, null);

            Assert.True(o.Success);
            Assert.Null(o.Error);
        }

        [Fact]
        public void Handler_failure_keeps_its_error()
        {
            var o = CommandOutcome.Normalize("create_level", false, null, "No document is open.", null);

            Assert.False(o.Success);
            Assert.Equal("No document is open.", o.Error);
        }

        [Fact]
        public void Rejected_oversized_response_is_a_failure_with_the_agent_error()
        {
            var o = CommandOutcome.Normalize("get_schedule_data", true, new { rows = 1 }, null,
                "Response too large.");

            Assert.False(o.Success);
            Assert.Equal("Response too large.", o.Error);
        }

        [Fact]
        public void Rejected_error_wins_over_a_batch_failure()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(false, "ok", "bad"), null,
                "Response too large.");

            Assert.False(o.Success);
            Assert.Equal("Response too large.", o.Error);
        }

        [Fact]
        public void Rolled_back_batch_is_a_failure()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(false, "ok", "bad", "ok"), null, null);

            Assert.False(o.Success);
            Assert.Equal("Batch rolled back: 1 sub-command(s) failed, no changes kept.", o.Error);
        }

        [Fact]
        public void ContinueOnError_batch_with_failed_sub_commands_is_a_failure()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(true, "ok", "bad", "partial"), null, null);

            Assert.False(o.Success);
            Assert.Equal("2 of 3 batch sub-command(s) failed.", o.Error);
        }

        [Fact]
        public void Batch_with_all_sub_commands_ok_is_a_success()
        {
            var o = CommandOutcome.Normalize("batch_execute", true, BatchData(true, "ok", "ok"), null, null);

            Assert.True(o.Success);
            Assert.Null(o.Error);
        }

        [Fact]
        public void Batch_handler_failure_keeps_its_error()
        {
            var o = CommandOutcome.Normalize("batch_execute", false, null, "No document is open.", null);

            Assert.False(o.Success);
            Assert.Equal("No document is open.", o.Error);
        }

        [Fact]
        public void Batch_shape_is_only_read_for_batch_execute()
        {
            var o = CommandOutcome.Normalize("send_code_to_revit", true,
                new { rolledBack = true, results = new[] { new { ok = false } } }, null, null);

            Assert.True(o.Success);
        }

        [Fact]
        public void BatchExecuteHandler_still_returns_the_shape_the_outcome_reads()
        {
            var source = ReadSource("Handlers/Runtime", "BatchExecuteHandler.cs");

            Assert.Contains("new { results = outcome.Results, rolledBack }", source);
        }

        [Fact]
        public void McpEventHandler_reports_one_outcome_after_the_size_guard()
        {
            var source = ReadSource("Infrastructure/Execution", "McpEventHandler.cs");

            var guard = source.IndexOf("ResponseEnvelopeGuard.Apply(", StringComparison.Ordinal);
            var normalize = source.IndexOf("CommandOutcome.Normalize(", StringComparison.Ordinal);
            var fileLog = source.IndexOf("McpLogger.TryLog(request.CommandName, request.ParamsJson, outcome.Success", StringComparison.Ordinal);
            var sessionLog = source.IndexOf("Success = outcome.Success", StringComparison.Ordinal);

            Assert.True(guard >= 0 && normalize > guard, "outcome must be judged after the size guard");
            Assert.True(fileLog > normalize, "mcp-calls.jsonl (History of past sessions) must log the outcome");
            Assert.True(sessionLog > normalize, "History must record the outcome");
            Assert.DoesNotContain("Success = result.Success", source);

            var toast = source.IndexOf("_toastNotifier?.OnCompleted(", sessionLog, StringComparison.Ordinal);
            Assert.True(toast > sessionLog, "toast call must follow the session log on the success path");
            var toastArgs = source.Substring(toast, source.IndexOf(");", toast, StringComparison.Ordinal) - toast);
            Assert.Contains("outcome.Success", toastArgs);
            Assert.Contains("outcome.Error", toastArgs);
        }

        [Fact]
        public void Every_shell_parses_expected_context_generation_so_older_versions_reach_unsupported_version()
        {
            // P0-3 regression guard: if any R22–R26 shell drops expected_context_generation, the shared
            // McpEventHandler family guard would fail closed with DOCUMENT_CONTEXT_CHANGED before the
            // handler can return UNSUPPORTED_VERSION. All six shells must parse the field identically.
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ReadSourcePath())!, "..", "..", "..", ".."));
            foreach (var year in new[] { "22", "23", "24", "25", "26", "27" })
            {
                var source = File.ReadAllText(Path.Combine(root, "src", "plugin-r" + year, "App.cs"));
                Assert.Contains("ExpectedContextGeneration = request.Value<long?>(\"expected_context_generation\")", source);
            }
        }

        [Fact]
        public void Family_document_context_rejection_emits_a_not_started_operation_receipt()
        {
            // P1-1 regression guard: DOCUMENT_CONTEXT_CHANGED must produce the same not_started
            // _operation receipt as DOCUMENT_UNCONFIRMED / DOCUMENT_CHANGED.
            var source = ReadSource("Infrastructure/Execution", "McpEventHandler.cs");
            var block = source.Substring(source.IndexOf("DOCUMENT_UNCONFIRMED:", StringComparison.Ordinal));
            Assert.Contains("DOCUMENT_CONTEXT_CHANGED:", block.Substring(0, block.IndexOf("};", StringComparison.Ordinal)));
        }

        [Fact]
        public void Scope_complete_folds_private_family_commits_into_one_apply_and_never_destroys_result_data()
        {
            var source = ReadSource("Failures", "FailureControl.cs");

            // P0-1: the redundant second FailureCompletion.Apply is gone — exactly one call in Complete.
            var complete = source.Substring(source.IndexOf("internal void Complete(CommandResult result)", StringComparison.Ordinal));
            complete = complete.Substring(0, complete.IndexOf("internal void Enrich", StringComparison.Ordinal));
            Assert.Equal(1, CountOccurrences(complete, "FailureCompletion.Apply(Operation"));
            // Private commits fold into AnyCommittedChanges while the family is NOT discarded.
            Assert.Contains("AnyCommitted || (PrivateTransientCommits > 0 && !PrivateFamilyDiscarded)", complete);
            Assert.Contains("NativeCommitted = NativeCommitted && !PrivateFamilyDiscarded", complete);

            // P0-2: no global `result.Data as JObject ?? new JObject()` overwrite that would destroy
            // batch_execute's anonymous { results, rolledBack } / save_checkpoint's recovery payload
            // or copy an unredacted handler error onto the wire.
            Assert.DoesNotContain("result.Data as JObject", complete);
            Assert.DoesNotContain("result.Data =", complete);
            Assert.DoesNotContain("lifecycle_diagnostic", complete);
        }

        [Fact]
        public void Private_commit_diversion_is_scoped_to_the_family_tool_not_all_owned_documents()
        {
            // P0-1: send_code_to_revit also TrackOpens documents via SendCodeDocumentRewriter, so a
            // bare ownership check would swallow script AnyCommitted/CommittedLog accounting. The
            // diversion must be gated on the family tool name.
            var source = ReadSource("Failures", "FailureNativeEvents.cs");
            var changed = source.Substring(source.IndexOf("private static void OnDocumentChanged", StringComparison.Ordinal));
            var commitBranch = changed.Substring(changed.IndexOf("TransactionCommitted ||", StringComparison.Ordinal));
            commitBranch = commitBranch.Substring(0, commitBranch.IndexOf("TransactionUndone", StringComparison.Ordinal));
            Assert.Contains("Active.Operation.Tool == \"create_parametric_family\"", commitBranch);
            Assert.Contains("McpDocument.CurrentOwnedDocument(document)", commitBranch);
            // The original accounting path is still present for every other tool.
            Assert.Contains("Active.AnyCommitted = true; Active.BatchRolledBack = false; Active.CommittedLog.Committed", commitBranch);
        }

        [Fact]
        public void Family_stamp_is_refreshed_on_real_document_switch_open_and_close()
        {
            // P1-2: Observe must move BOTH slots on a real document-identity change (and neither on a
            // same-document view switch), and a completed close must bump the family slot.
            var context = ReadSource("Failures", "DialogDocumentContext.cs");
            var observe = context.Substring(context.IndexOf("internal void Observe(", StringComparison.Ordinal));
            observe = observe.Substring(0, observe.IndexOf("internal void Invalidate()", StringComparison.Ordinal));
            Assert.Contains("Invalidate();", observe);
            Assert.Contains("InvalidateFamily();", observe);
            Assert.Contains("if (activeDocumentId == _documentId) return;", observe);

            var events = ReadSource("Failures", "FailureNativeEvents.cs");
            var opened = events.Substring(events.IndexOf("internal static void ObserveDialogDocument", StringComparison.Ordinal));
            opened = opened.Substring(0, opened.IndexOf("private static void OnViewActivated", StringComparison.Ordinal));
            Assert.Contains("DialogContext.InvalidateFamily();", opened);
            var closed = events.Substring(events.IndexOf("private static void OnDocumentClosed", StringComparison.Ordinal));
            closed = closed.Substring(0, closed.IndexOf("private static void OnDocumentSaved", StringComparison.Ordinal));
            Assert.Contains("InvalidateFamily()", closed);
        }

        private static int CountOccurrences(string text, string token)
        {
            var count = 0; var i = 0;
            while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { count++; i += token.Length; }
            return count;
        }

        private static string ReadSourcePath([CallerFilePath] string testFile = "") => testFile;

        private static string ReadSource(string folder, string file, [CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", ".."));
            return File.ReadAllText(Path.Combine(root, "src", "shared", folder, file));
        }
    }
}
