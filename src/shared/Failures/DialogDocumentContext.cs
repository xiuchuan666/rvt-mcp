using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace RvtMcp.Failures
{
    // One aligned 64-bit activation stamp per x64 Revit process. The gateway can read it even
    // while Revit is modal. Only the add-in writes, on the Revit UI thread. No model data is shared.
    //
    // Two independent slots share one 16-byte map (both x64 8-byte aligned, no tearing):
    //   offset 0 — Stamp: active-document identity, invalidates dialog answers (scope session/next).
    //              Same-document view activation and background document closes must NOT bump it.
    //   offset 8 — FamilyStamp: native document/view lifecycle generation for create_parametric_family.
    //              Bumped on document open/close and view activation, independent of dialog answers.
    internal sealed class DialogDocumentContext : IDisposable
    {
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private string _documentId;
        internal long Stamp { get; private set; }
        internal long FamilyStamp { get; private set; }

        internal DialogDocumentContext(string target)
        {
            _map = MemoryMappedFile.CreateNew(Name(target), 2 * sizeof(long));
            try
            {
                _view = _map.CreateViewAccessor(0, 2 * sizeof(long));
                Stamp = DateTime.UtcNow.Ticks;
                _view.Write(0, Stamp);
                FamilyStamp = Stamp;
                _view.Write(sizeof(long), FamilyStamp);
            }
            catch { _view?.Dispose(); _map.Dispose(); throw; }
        }

        internal void Observe(string activeDocumentId)
        {
            // Same document (including another view of it) changes nothing: neither slot moves, so
            // ordinary dialog answers survive view switches. A REAL document-identity change moves
            // both slots — offset 0 for dialog answers and offset 8 for family queue-race detection.
            if (activeDocumentId == _documentId) return;
            Invalidate();
            InvalidateFamily();
            _documentId = activeDocumentId;
        }

        internal void Invalidate() => _view.Write(0, ++Stamp);

        // Native document/view lifecycle generation for family authoring. Deliberately separate
        // from Stamp so ordinary tools' dialog declarations are not invalidated by view switches
        // or background family closes. Same UI-thread writer as Stamp; no cross-slot coupling.
        internal void InvalidateFamily() => _view.Write(sizeof(long), ++FamilyStamp);

        internal static void RequireCurrent(DialogDocumentContext context, long? declaredStamp)
        {
            if (context == null || context.Stamp != declaredStamp)
                throw new InvalidOperationException("DOCUMENT_CHANGED: Dialog declarations expired before dispatch. Review the active document and declare answers again.");
        }

        internal static string FamilyRejection(DialogDocumentContext context, long? expectedStamp)
        {
            // Older/newer mismatched shells must fail closed: absence of the shared stamp
            // cannot prove the document/Home context stayed unchanged until execution.
            // Sampled immediately before the gateway writes the request; rejects a change during
            // queueing. Confirmation-time identity (including Home) is enforced separately by
            // ToolReadPolicy.DocumentRejection, so this is the queue-race guard, not the A→B guard.
            if (context == null || context.FamilyStamp != expectedStamp)
                return "DOCUMENT_CONTEXT_CHANGED: Revit's document or view state changed after the family call was dispatched. The independent family was not started. Confirm the target again (for an open project: revit_switch_target(verify=true); for Revit Home: revit_switch_target(version=\"pid:<n>\", verify=false)) and retry.";
            return null;
        }

        internal static long? Read(string target)
            => ReadSlot(target, 0);

        internal static long? ReadFamily(string target)
            => ReadSlot(target, sizeof(long));

        private static long? ReadSlot(string target, int offset)
        {
#if NET5_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) return null;
#endif
            try
            {
                using (var map = MemoryMappedFile.OpenExisting(Name(target), MemoryMappedFileRights.Read))
                using (var view = map.CreateViewAccessor(offset, sizeof(long), MemoryMappedFileAccess.Read))
                {
                    var stamp = view.ReadInt64(0);
                    return stamp == 0 ? (long?)null : stamp;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            // Defensive: an older add-in's 8-byte map normally still reads slot 8 as 0 (page-granular,
            // zero-initialized), but never let a short-map view fault escape to the gateway.
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static string Name(string target) => @"Local\RvtMcp.DialogContext." + target;

        public void Dispose() { _view.Dispose(); _map.Dispose(); }
    }
}
