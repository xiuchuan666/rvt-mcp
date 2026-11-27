using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class DocumentTargetPolicyTests
    {
        [Theory]
        [InlineData("create_level", null, "doc-a", "DOCUMENT_UNCONFIRMED")]
        [InlineData("create_level", "doc-a", "doc-b", "DOCUMENT_CHANGED")]
        [InlineData("create_level", "doc-a", null, "DOCUMENT_CHANGED")]
        [InlineData("send_code_to_revit", "doc-a", "doc-b", "DOCUMENT_CHANGED")]
        [InlineData("send_code_to_revit", "doc-a", null, "DOCUMENT_CHANGED")]
        [InlineData("send_code_to_revit", null, "doc-a", "DOCUMENT_UNCONFIRMED")]
        [InlineData("send_code_to_revit", null, null, null)]
        [InlineData("batch_execute", "doc-a", "doc-b", "DOCUMENT_CHANGED")]
        [InlineData("create_level", "doc-a", "doc-a", null)]
        [InlineData("get_current_view_info", null, "doc-b", null)]
        [InlineData("open_model", null, null, null)]
        public void Queued_request_checks_actual_document_before_execution(string command, string expected, string actual, string error)
        {
            var rejection = ToolReadPolicy.DocumentRejection(command, expected, actual);
            if (error == null) Assert.Null(rejection);
            else Assert.StartsWith(error + ":", rejection);
        }

        [Theory]
        [InlineData("create_level", null, "doc-a", false, "PAIRING_REQUIRED")]
        [InlineData("send_code_to_revit", null, "doc-a", false, "PAIRING_REQUIRED")]
        [InlineData("send_code_to_revit", null, null, false, "PAIRING_REQUIRED")]
        [InlineData("create_level", null, "doc-a", true, "DOCUMENT_UNCONFIRMED")]
        [InlineData("create_level", "doc-a", "doc-b", false, "DOCUMENT_CHANGED")]
        [InlineData("create_level", "doc-a", "doc-a", false, null)]
        [InlineData("get_current_view_info", null, "doc-a", false, null)]
        [InlineData("open_model", null, null, false, null)]
        public void Gateway_without_document_protocol_is_told_to_update_instead_of_to_verify(
            string command, string expected, string actual, bool gatewayConfirmsDocuments, string error)
        {
            var rejection = ToolReadPolicy.DocumentRejection(command, expected, actual, gatewayConfirmsDocuments);
            if (error == null) Assert.Null(rejection);
            else Assert.StartsWith(error + ":", rejection);
            if (error == "PAIRING_REQUIRED")
            {
                Assert.DoesNotContain("revit_switch_target(verify=true) before", rejection);
                Assert.Contains("connected rvt-mcp gateway", rejection);
                Assert.Contains("same rvt-mcp Setup release", rejection);
                Assert.Contains("restart Revit", rejection);
                Assert.Contains("MCP client", rejection);
            }
        }
        [Theory]
        [InlineData(null, null, true)]
        [InlineData(null, "doc-a", false)]
        [InlineData("doc-a", "doc-a", true)]
        [InlineData("doc-a", "doc-b", false)]
        public void Independent_family_authoring_preserves_exact_document_or_home_confirmation(
            string expected, string actual, bool allowed)
        {
            var rejection = ToolReadPolicy.DocumentRejection("create_parametric_family", expected, actual);
            Assert.Equal(allowed, rejection == null);
            if (!allowed) Assert.StartsWith("DOCUMENT_CHANGED:", rejection);
            Assert.Equal(allowed, ToolReadPolicy.DocumentRejection("CREATE_PARAMETRIC_FAMILY", expected, actual, true) == null);
            Assert.StartsWith("PAIRING_REQUIRED:", ToolReadPolicy.DocumentRejection("create_parametric_family", null, actual, false));
            Assert.Equal(allowed && expected != null,
                ToolReadPolicy.DocumentRejection("create_parametric_family", expected, actual, false) == null);
            Assert.Contains("READ_ONLY", ToolReadPolicy.Rejection("create_parametric_family", true, true));
        }

        [Fact]
        public void Independent_family_authoring_rejects_a_changed_native_document_context()
        {
            using var context = new RvtMcp.Failures.DialogDocumentContext("family-policy-test-" + System.Guid.NewGuid().ToString("N"));
            Assert.Null(RvtMcp.Failures.DialogDocumentContext.FamilyRejection(context, context.Stamp));
            Assert.StartsWith("DOCUMENT_CONTEXT_CHANGED:",
                RvtMcp.Failures.DialogDocumentContext.FamilyRejection(context, context.Stamp - 1));
            context.Invalidate();
            Assert.StartsWith("DOCUMENT_CONTEXT_CHANGED:",
                RvtMcp.Failures.DialogDocumentContext.FamilyRejection(context, context.Stamp - 1));
            Assert.StartsWith("DOCUMENT_CONTEXT_CHANGED:",
                RvtMcp.Failures.DialogDocumentContext.FamilyRejection(null, 12));
        }

        [Fact]
        public void Ordinary_writes_still_reject_unconfirmed_or_changed_documents()
        {
            Assert.Contains("DOCUMENT_UNCONFIRMED", ToolReadPolicy.DocumentRejection("create_level", null, null));
            Assert.Contains("DOCUMENT_CHANGED", ToolReadPolicy.DocumentRejection("create_level", "doc-a", "doc-b"));
        }
    }
}
