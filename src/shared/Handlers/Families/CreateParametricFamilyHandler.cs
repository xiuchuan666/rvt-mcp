using System;
using System.IO;
#if REVIT2027
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FailureControl = RvtMcp.Plugin.Failures.FailureControl;
using FamilyItemFactory = Autodesk.Revit.Creation.FamilyItemFactory;
#endif
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;


namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Revit 2027 family-authoring slice: one native extrusion whose Length, Width and Height
    /// are type parameters driven by labelled dimensions. The native sketch and extent are
    /// associated through those dimensions; no mesh or baked solid is used.
    /// </summary>
    public sealed class CreateParametricFamilyHandler : IRevitCommand
    {
        private const string Scope = "type";
        public string Name => "create_parametric_family";
        public string Description => "Revit 2027: create a new .rfa containing one parameter-driven rectangular extrusion; never overwrites a file.";
        public string ParametersSchema
        {
            get
            {
#if REVIT2027
                return @"{
  ""type"": ""object"",
  ""required"": [""family_name"", ""template_path"", ""output_path"", ""length_mm"", ""width_mm"", ""height_mm""],
  ""properties"": {
    ""family_name"": {""type"": ""string""},
    ""template_path"": {""type"": ""string"", ""description"": ""Absolute path of a Revit family template (.rft).""},
    ""output_path"": {""type"": ""string"", ""description"": ""New absolute local .rfa path. Existing files are never overwritten.""},
    ""length_mm"": {""type"": ""number"", ""exclusiveMinimum"": 0},
    ""width_mm"": {""type"": ""number"", ""exclusiveMinimum"": 0},
    ""height_mm"": {""type"": ""number"", ""exclusiveMinimum"": 0},
    ""parameter_names"": {""type"": ""string"", ""default"": ""Length,Width,Height""},
    ""parameter_scope"": {""type"": ""string"", ""enum"": [""type""], ""default"": ""type""},
    ""type_name"": {""type"": ""string"", ""default"": ""Standard""},
    ""timeout_seconds"": {""type"": ""integer"", ""minimum"": 1, ""maximum"": 900}
  }
}";
#else
                return @"{""type"":""object""}";
#endif
            }
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
#if !REVIT2027
            return CommandResult.Fail("UNSUPPORTED_VERSION: create_parametric_family requires Revit 2027. This Revit version was not changed and no file was written.");
#else
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var familyName = request.Value<string>("family_name")?.Trim();
            var templatePath = request.Value<string>("template_path");
            var outputPath = request.Value<string>("output_path");
            var typeName = request.Value<string>("type_name") ?? "Standard";
            var parameterNames = request.Value<string>("parameter_names") ?? "Length,Width,Height";
            var parameterScope = request.Value<string>("parameter_scope") ?? Scope;
            var lengthMm = request.Value<double?>("length_mm") ?? double.NaN;
            var widthMm = request.Value<double?>("width_mm") ?? double.NaN;
            var heightMm = request.Value<double?>("height_mm") ?? double.NaN;

            var inputError = ParametricFamilyInput.Validate(familyName, templatePath, outputPath,
                lengthMm, widthMm, heightMm, parameterNames, parameterScope, typeName);
            if (inputError != null) return CommandResult.Fail(inputError);

            templatePath = Path.GetFullPath(templatePath);
            outputPath = Path.GetFullPath(outputPath);
            var temporaryPath = ParametricFamilyInput.TemporaryOutputPath(outputPath);
            Document familyDoc = null;
            var closed = false;
            var closeAttempted = false;
            var saved = false;
            var closeError = (string)null;
            CommandResult outcome;
            var stage = "opening the family template";
            using (McpDocument.Begin(app))
            {
                try
                {
                    // TrackOpened must run before any failure/save/close event for this new document.
                    // Begin snapshots all pre-existing documents, so a user's family is never owned.
                    familyDoc = McpDocument.TrackOpened(app.Application.NewFamilyDocument(templatePath));
                    if (familyDoc == null || !familyDoc.IsFamilyDocument)
                        throw new InvalidOperationException("Revit did not open the supplied template as a family document.");
                    // Fail fast BEFORE any transaction or disk write if Revit activated the new family
                    // (e.g. Home state or a third-party callback). Document.Close cannot close the active
                    // document, so an active family here means no save, no temp file, nothing to clean up.
                    if (IsActiveDocument(app, familyDoc))
                    {
                        // Mark closeAttempted so the catch block does not re-diagnose and duplicate
                        // this same active-document message; no close is possible or needed here.
                        closeAttempted = true;
                        throw new InvalidOperationException("Revit opened the new family as the active document, which cannot be closed through Document.Close. The family remains open for user review; no file was written.");
                    }
                    stage = "creating the native extrusion and parameter constraints";

                    var dimensions = new[] { lengthMm, widthMm, heightMm };
                    object familyData;
                    using (var transaction = new Transaction(familyDoc, "RvtMcp: create parameterized extrusion"))
                    {
                        if (transaction.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("Could not start the family creation transaction.");
                        try
                        {
                            stage = "creating the native extrusion and parameter constraints";
                            var manager = familyDoc.FamilyManager;
                            if (manager == null) throw new InvalidOperationException("The family template does not expose a FamilyManager.");
                            stage = "selecting family type and creating Length, Width, Height parameters";
                            var familyType = SelectType(manager, typeName);
                            var parameters = CreateParameters(manager);
                            SetValues(manager, parameters, dimensions);

                            stage = "creating the extrusion sketch";
                            var plan = SelectFamilyPlan(familyDoc);
                            var plane = CreateWorkPlane(familyDoc);
                            var extrusion = CreateExtrusion(familyDoc, plane, dimensions);
                            if (extrusion == null) throw new InvalidOperationException("Revit did not create the extrusion.");
                            var start = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
                            if (start == null || Math.Abs(start.AsDouble()) > 1e-8)
                                throw new InvalidOperationException("The extrusion did not start at the work-plane origin (zero).");

                            stage = "associating extrusion and labelled dimensions";
                            var constraints = AssociateExtrusionParameters(familyDoc, manager, parameters, extrusion, dimensions, ref stage);
                            stage = "regenerating and reading back native geometry";
                            familyDoc.Regenerate();
                            familyData = ReadBack(familyDoc, manager, parameters, extrusion, constraints, familyName, typeName, dimensions);
                            stage = "committing the family creation transaction";
                            if (transaction.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Revit did not commit the family creation transaction.");
                        }
                        catch
                        {
                            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                            throw;
                        }
                    }

                    if (File.Exists(outputPath) || Directory.Exists(outputPath)) // A second guard immediately before the native save.
                        throw new IOException("output_path already exists. This tool never overwrites an existing file or directory.");
                    stage = "saving the temporary family";
                    // Refuse to write if the scope was already stopped (e.g. ledger storage failure on
                    // the transaction commit). The pre-event also re-checks this; this is the handler gate.
                    FailureControl.ThrowIfStopped();
                    // Save only a uniquely named temporary .rfa. The final same-volume rename is
                    // atomic and non-overwriting on Windows. The typed one-shot authorization is
                    // checked by FailureControl's real SavingAs event, not a caller-supplied grant.
                    using (FailureControl.AuthorizeFamilySave(familyDoc, temporaryPath))
                        familyDoc.SaveAs(temporaryPath, new SaveAsOptions { OverwriteExistingFile = false });
                    FailureControl.ThrowIfStopped();
                    if (!File.Exists(temporaryPath)) throw new IOException("Revit did not create the temporary family file.");
                    // Second guard: a third-party event could have activated the family mid-operation.
                    if (IsActiveDocument(app, familyDoc))
                    {
                        closeAttempted = true; // avoid duplicating the active-document text in the catch block
                        throw new IOException("The saved temporary family became Revit's active document and cannot be closed through Document.Close. It remains open for user review; the requested output was not written.");
                    }
                    closeAttempted = true;
                    if (!McpDocument.Close(familyDoc, false))
                        throw new IOException("Revit did not close the temporary family document. The saved temporary file was retained; the requested output was not written.");
                    familyDoc = null;
                    closed = true;
                    stage = "renaming the temporary family to output_path";
                    File.Move(temporaryPath, outputPath);
                    saved = true;
                    var result = JObject.FromObject(familyData);
                    result["created"] = true;
                    result["saved"] = true;
                    result["output_path"] = outputPath;
                    result["file_size_bytes"] = new FileInfo(outputPath).Length;
                    result["host_verification"] = "in_revit_2027";
                    result["native_commit"] = "temporary_rfa_saved";
                    result["file_output"] = "non_overwriting_rename";
                    result["document_cleanup"] = "closed";
                    outcome = CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (saved)
                    {
                        outcome = CommandResult.Fail("The requested family file was created by a non-overwriting rename, but its response could not be completed. Inspect output_path before retrying: " + ex.Message);
                    }
                    else
                    {
                        if (!closed && !closeAttempted && familyDoc != null)
                        {
                            closeAttempted = true;
                            if (IsActiveDocument(app, familyDoc))
                            {
                                // RevitAPI Document.Close explicitly rejects the active document.
                                // Never activate another document or close the user's document to force cleanup.
                                closeError = "The new family is Revit's active document, which Document.Close cannot close. Close or save it manually in Revit before retrying.";
                            }
                            else
                            {
                                try
                                {
                                    closed = McpDocument.Close(familyDoc, false);
                                    if (!closed) closeError = "Revit returned false when closing the family document.";
                                }
                                catch (Exception closeException) { closeError = closeException.Message; }
                            }
                        }
                        var cleanupError = closed || familyDoc == null
                            ? ParametricFamilyInput.DeleteTemporaryFile(temporaryPath)
                            : "The owned family document remains open; its temporary file, if created, was retained for inspection at " + temporaryPath + ".";
                        // The family is fully discarded only when its document was actually closed
                        // (or never opened) AND the temporary file was deleted. Anything still open
                        // or still on disk must be reported as retained, not as clean rollback.
                        var fullyDiscarded = (closed || familyDoc == null) && cleanupError == null;
                        if (fullyDiscarded && FailureControl.Active != null)
                            FailureControl.Active.MarkPrivateFamilyDiscarded();
                        var lifecycle = "Family creation failed; no requested output file was written. "
                            + (closed ? "The family document was closed without saving to output_path. "
                                : familyDoc == null ? "No family document was opened or it was already closed. "
                                : "The family document was not closed; see the lifecycle detail. ")
                            + (cleanupError == null ? "No temporary or output file was left behind. " : cleanupError + " ")
                            + (closeError == null ? "" : closeError + " ") + "Failure stage: " + stage + ". " + ex.Message;
                        outcome = CommandResult.Fail(lifecycle);
                        // Keep the full diagnostic as structured data so FailureControl.Complete can
                        // preserve it when a native stop replaces result.Error with a bare stop code.
                        outcome.Data = new JObject { ["lifecycle_diagnostic"] = lifecycle };
                    }
                }
            }
            return outcome;
#endif
        }

#if REVIT2027

        private static bool IsActiveDocument(UIApplication app, Document document)
            => app.ActiveUIDocument?.Document != null && app.ActiveUIDocument.Document.Equals(document);

        private static FamilyType SelectType(FamilyManager manager, string typeName)
        {
            var existing = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == typeName);
            if (existing != null)
            {
                manager.CurrentType = existing;
                return existing;
            }
            var created = manager.NewType(typeName); // NewType also selects the type when CurrentType was null.
            if (created == null || manager.CurrentType == null)
                throw new InvalidOperationException("Revit could not create and select the requested family type.");
            manager.CurrentType = created;
            return created;
        }

        private static Dictionary<string, FamilyParameter> CreateParameters(FamilyManager manager)
        {
            var names = new[] { "Length", "Width", "Height" };
            var result = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++)
            {
                var existing = manager.GetParameters().FirstOrDefault(p => p.Definition.Name == names[i]);
                if (existing != null && (existing.IsInstance || existing.IsReporting || existing.StorageType != StorageType.Double
                    || existing.GetUnitTypeId() != SpecTypeId.Length
                    || existing.Formula != null || existing.AssociatedParameters.Size != 0))
                    throw new InvalidOperationException("The family template has an incompatible or already-used parameter named " + names[i] + ".");
                var parameter = existing ?? manager.AddParameter(names[i], GroupTypeId.Geometry, SpecTypeId.Length, false);
                if (parameter == null || manager.CurrentType.AsDouble(parameter) == null)
                    throw new InvalidOperationException("Family parameter " + names[i] + " did not expose a numeric length value on the selected type.");
                result.Add(names[i], parameter);
            }
            return result;
        }

        private static void SetValues(FamilyManager manager, IReadOnlyDictionary<string, FamilyParameter> parameters, double[] values)
        {
            manager.Set(parameters["Length"], ParametricFamilyInput.ToRevitFeet(values[0]));
            manager.Set(parameters["Width"], ParametricFamilyInput.ToRevitFeet(values[1]));
            manager.Set(parameters["Height"], ParametricFamilyInput.ToRevitFeet(values[2]));
        }

        private static ViewPlan SelectFamilyPlan(Document doc)
        {
            var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan
                    && v.GenLevel != null && Math.Abs(v.GenLevel.Elevation) <= 1e-8);
            if (plan == null)
                throw new InvalidOperationException("Use a standard 3D family template with a floor plan at zero elevation; this tool does not support annotation, massing or specialized family templates.");
            return plan;
        }

        private static SketchPlane CreateWorkPlane(Document doc)
        {
            // A Plane overload at the family origin avoids Level.Elevation changing the solid base.
            // NewExtrusion uses the plane's +Z normal; EXTRUSION_START remains zero.
            var plane = SketchPlane.Create(doc, Plane.CreateByOriginAndBasis(XYZ.Zero, XYZ.BasisX, XYZ.BasisY));
            if (plane == null) throw new InvalidOperationException("Could not create the extrusion work plane at the family origin.");
            return plane;
        }

        private static Extrusion CreateExtrusion(Document doc, SketchPlane plane, double[] dimensions)
        {
            // Centered on the family origin. The labelled dimensions preserve this insertion point.
            var halfLength = ParametricFamilyInput.ToRevitFeet(dimensions[0] * 0.5);
            var halfWidth = ParametricFamilyInput.ToRevitFeet(dimensions[1] * 0.5);
            var corners = new[]
            {
                new XYZ(-halfLength, -halfWidth, 0), new XYZ(halfLength, -halfWidth, 0),
                new XYZ(halfLength, halfWidth, 0), new XYZ(-halfLength, halfWidth, 0)
            };
            var loop = new CurveArray();
            for (var i = 0; i < 4; i++)
            {
                var line = Line.CreateBound(corners[i], corners[(i + 1) % 4]);
                loop.Append(line);
            }
            var loops = new CurveArrArray();
            loops.Append(loop);
            return doc.FamilyCreate.NewExtrusion(true, loops, plane, ParametricFamilyInput.ToRevitFeet(dimensions[2]));
        }

        private sealed class ConstraintSet
        {
            internal readonly List<Dimension> Dimensions = new List<Dimension>();
            internal readonly List<Dimension> Equalities = new List<Dimension>();
            internal readonly List<ElementId> Alignments = new List<ElementId>();
        }

        private static ConstraintSet AssociateExtrusionParameters(Document doc, FamilyManager manager,
            IReadOnlyDictionary<string, FamilyParameter> parameters, Extrusion extrusion, double[] dimensions, ref string stage)
        {
            stage = "associating extrusion Height";
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            if (end == null || end.IsReadOnly) throw new InvalidOperationException("The extrusion end parameter is unavailable or read-only.");
            manager.AssociateElementParameterToFamilyParameter(end, parameters["Height"]);

            // Align only after native model-curve references exist; the geometry is already aligned.
            var plan = SelectFamilyPlan(doc);
            stage = "creating labelled Length and Width dimensions and origin EQ constraints";
            var constraints = CreateNamedPlanes(doc, manager, plan, parameters, dimensions);
            stage = "locking native ModelCurve profile edges to reference planes";
            constraints.Alignments.AddRange(CreateExtrusionProfileConstraints(doc, plan, extrusion));
            return constraints;
        }

        private static ConstraintSet CreateNamedPlanes(Document doc, FamilyManager manager, ViewPlan plan,
            IReadOnlyDictionary<string, FamilyParameter> parameters, double[] dimensions)
        {
            var factory = doc.FamilyCreate;
            var halfLength = ParametricFamilyInput.ToRevitFeet(dimensions[0] * 0.5);
            var halfWidth = ParametricFamilyInput.ToRevitFeet(dimensions[1] * 0.5);
            var right = factory.NewReferencePlane(new XYZ(halfLength, -2, 0), new XYZ(halfLength, 2, 0), XYZ.BasisZ, plan);
            right.Name = "RvtMcp Right";
            var left = factory.NewReferencePlane(new XYZ(-halfLength, -2, 0), new XYZ(-halfLength, 2, 0), XYZ.BasisZ, plan);
            left.Name = "RvtMcp Left";
            var front = factory.NewReferencePlane(new XYZ(-2, halfWidth, 0), new XYZ(2, halfWidth, 0), XYZ.BasisZ, plan);
            front.Name = "RvtMcp Front";
            var back = factory.NewReferencePlane(new XYZ(-2, -halfWidth, 0), new XYZ(2, -halfWidth, 0), XYZ.BasisZ, plan);
            back.Name = "RvtMcp Back";
            // A newly created ReferencePlane does not yet expose a *geometric* reference that
            // ItemFactoryBase.NewDimension accepts ("The references are not geometric references").
            // Regenerate first so each plane's geometry exists, then re-read the elements and obtain
            // the references from the resolved geometry rather than from the just-created wrappers.
            doc.Regenerate();
            var dimension = factory.NewDimension(plan, Line.CreateBound(new XYZ(-halfLength, 0, 0), new XYZ(halfLength, 0, 0)),
                RefArray(PlaneReference(left, plan), PlaneReference(right, plan)));
            dimension.FamilyLabel = parameters["Length"];
            var width = factory.NewDimension(plan, Line.CreateBound(new XYZ(0, -halfWidth, 0), new XYZ(0, halfWidth, 0)),
                RefArray(PlaneReference(back, plan), PlaneReference(front, plan)));
            width.FamilyLabel = parameters["Width"];
            var constraints = new ConstraintSet();
            constraints.Dimensions.Add(dimension);
            constraints.Dimensions.Add(width);
            // Use two separate half-length dimensions + formula instead of a three-reference EQ
            // dimension. The three-reference EQ pattern (first/origin/second with AreSegmentsEqual)
            // is documented in the Autodesk forum, but real Revit 2027.4 rejects the references at
            // transaction.Commit() with "One or more dimension references are or have become invalid".
            // Two independent two-reference dimensions labeled to hidden half-parameters, with the
            // full Length/Width driven by formula, achieves the same origin-centering without the
            // multi-reference commit instability.
            var halfLengthParam = GetOrCreateHalfParameter(manager, "Length");
            var halfWidthParam = GetOrCreateHalfParameter(manager, "Width");
            manager.SetFormula(parameters["Length"], halfLengthParam.Definition.Name + " * 2");
            manager.SetFormula(parameters["Width"], halfWidthParam.Definition.Name + " * 2");
            constraints.Equalities.Add(CreateHalfDimension(doc, factory, plan, left, halfLengthParam,
                Line.CreateBound(new XYZ(-halfLength, 0, 0), new XYZ(0, 0, 0)), true));
            constraints.Equalities.Add(CreateHalfDimension(doc, factory, plan, right, halfLengthParam,
                Line.CreateBound(new XYZ(0, 0, 0), new XYZ(halfLength, 0, 0)), true));
            constraints.Equalities.Add(CreateHalfDimension(doc, factory, plan, back, halfWidthParam,
                Line.CreateBound(new XYZ(0, -halfWidth, 0), new XYZ(0, 0, 0)), false));
            constraints.Equalities.Add(CreateHalfDimension(doc, factory, plan, front, halfWidthParam,
                Line.CreateBound(new XYZ(0, 0, 0), new XYZ(0, halfWidth, 0)), false));
            return constraints;
        }

        // ReferencePlane.GetReference() returns the element-level reference, which may be
        // non-geometric for a freshly created or view-invisible plane. Resolve the plane's own
        // PlanarFace from its element geometry with ComputeReferences=true and take that face's
        // Reference — a genuine geometric reference accepted by both NewDimension and NewAlignment.
        private static Reference PlaneReference(ReferencePlane plane, View view = null)
        {
            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
            if (view != null) options.View = view;
            var geometry = plane.get_Geometry(options);
            if (geometry != null)
            {
                foreach (var face in geometry.OfType<PlanarFace>())
                    if (face.Reference != null) return face.Reference;
            }
            // Fallback to the element-level reference when no face is exposed.
            return plane.GetReference();
        }

        // Creates a two-reference dimension from a side plane to the origin center plane,
        // labeled to a hidden half-parameter (e.g. "_HalfLength"). The full Length/Width
        // parameters are driven by formula = half * 2, which centers the geometry on the origin.
        private static Dimension CreateHalfDimension(Document doc, FamilyItemFactory factory, ViewPlan plan,
            ReferencePlane side, FamilyParameter halfParam, Line line, bool vertical)
        {
            var origin = FindCenterPlane(doc, vertical);
            var references = new ReferenceArray();
            references.Append(PlaneReference(side, plan));
            references.Append(PlaneReference(origin, plan));
            var dimension = factory.NewDimension(plan, line, references);
            dimension.FamilyLabel = halfParam;
            return dimension;
        }

        private static ReferencePlane FindCenterPlane(Document doc, bool vertical)
        {
            var origin = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>().FirstOrDefault(p =>
                {
                    var plane = p.GetPlane();
                    var center = vertical ? plane.Origin.X : plane.Origin.Y;
                    var normal = vertical ? plane.Normal.Y : plane.Normal.X;
                    return Math.Abs(center) <= 1e-8 && Math.Abs(normal) <= 1e-8;
                });
            if (origin == null)
                throw new InvalidOperationException("The family template must contain both center reference planes at the origin, as in a standard 3D family template.");
            return origin;
        }

        private static FamilyParameter GetOrCreateHalfParameter(FamilyManager manager, string fullName)
        {
            var name = "_Half" + fullName;
            var existing = manager.GetParameters().FirstOrDefault(p => p.Definition.Name == name);
            if (existing != null)
            {
                if (existing.IsInstance || existing.IsReporting || existing.StorageType != StorageType.Double
                    || existing.GetUnitTypeId() != SpecTypeId.Length || existing.Formula != null
                    || existing.AssociatedParameters.Size != 0)
                    throw new InvalidOperationException("The family template has an incompatible parameter named " + name + ".");
                return existing;
            }
            var parameter = manager.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, false);
            if (parameter == null || manager.CurrentType.AsDouble(parameter) == null)
                throw new InvalidOperationException("Family parameter " + name + " did not expose a numeric length value.");
            return parameter;
        }

        private static ReferenceArray RefArray(Reference first, Reference second)
        {
            var result = new ReferenceArray();
            result.Append(first);
            result.Append(second);
            return result;
        }

        private static List<ElementId> CreateExtrusionProfileConstraints(Document doc, ViewPlan plan, Extrusion extrusion)
        {
            if (extrusion.Sketch == null) throw new InvalidOperationException("The native extrusion did not expose its editable sketch.");
            // GetAllElements identifies the native ModelCurves belonging to the sketch. Reading
            // GeometryCurve from each element avoids relying on Sketch.Profile wrapper references.
            var curves = extrusion.Sketch.GetAllElements().Select(doc.GetElement).OfType<ModelCurve>()
                .Select(c => new { Curve = c.GeometryCurve as Line, Reference = c.GeometryCurve.Reference })
                .Where(c => c.Curve != null && c.Reference != null).ToArray();
            if (curves.Length != 4) throw new InvalidOperationException("The regenerated native extrusion did not expose exactly four referenceable ModelCurve lines.");
            var planes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToArray();
            var alignments = new List<ElementId>();
            foreach (var edge in curves)
            {
                var direction = edge.Curve.Direction;
                var vertical = Math.Abs(direction.Y) > 0.9;
                var midpoint = edge.Curve.GetEndPoint(0).Add(edge.Curve.GetEndPoint(1)).Multiply(0.5);
                var name = vertical ? (midpoint.X > 0 ? "RvtMcp Right" : "RvtMcp Left")
                    : (midpoint.Y > 0 ? "RvtMcp Front" : "RvtMcp Back");
                var plane = planes.First(p => p.Name == name);
                var alignment = doc.FamilyCreate.NewAlignment(plan, PlaneReference(plane, plan), edge.Reference);
                if (alignment == null) throw new InvalidOperationException("Could not align the extrusion profile to " + name + ".");
                alignments.Add(alignment.Id); // NewAlignment returns a locked Dimension.
            }
            doc.Regenerate();
            return alignments;
        }

        private static bool IsFamilyParameter(FamilyParameter actual, FamilyParameter expected)
        {
            // Revit may return a new Parameter wrapper on every API access. Compare stable identity.
            return actual != null && expected != null
                && RevitCompat.GetId(actual.Id) == RevitCompat.GetId(expected.Id) && actual.Definition.Name == expected.Definition.Name;
        }

        private static object ReadBack(Document doc, FamilyManager manager,
            IReadOnlyDictionary<string, FamilyParameter> parameters, Extrusion extrusion,
            ConstraintSet constraints, string familyName, string typeName, double[] dimensions)
        {
            var names = new[] { "Length", "Width", "Height" };
            var read = new List<object>();
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            var associated = new[]
            {
                constraints.Dimensions.SingleOrDefault(d => d.FamilyLabel != null && IsFamilyParameter(d.FamilyLabel, parameters["Length"])) != null,
                constraints.Dimensions.SingleOrDefault(d => d.FamilyLabel != null && IsFamilyParameter(d.FamilyLabel, parameters["Width"])) != null,
                end != null && IsFamilyParameter(manager.GetAssociatedFamilyParameter(end), parameters["Height"])
            };
            for (var i = 0; i < names.Length; i++)
            {
                var parameter = parameters[names[i]];
                var value = manager.CurrentType.AsDouble(parameter);
                if (value == null) throw new InvalidOperationException("Family parameter " + names[i] + " has no numeric value.");
                if (!associated[i]) throw new InvalidOperationException("Family parameter " + names[i] + " is not associated with native geometry.");
                read.Add(new
                {
                    name = names[i], scope = Scope, spec = "Length", value_mm = ParametricFamilyInput.MillimetersFromFeet(value.Value),
                    requested_mm = dimensions[i], associated = associated[i]
                });
            }
            var alignmentCount = constraints.Alignments.Select(doc.GetElement).OfType<Dimension>().Count(a => a.IsLocked);
            if (constraints.Dimensions.Count != 2 || alignmentCount != 4
                || constraints.Equalities.Count != 4 || constraints.Equalities.Any(d => d.FamilyLabel == null))
                throw new InvalidOperationException("Native constraint read-back did not find two labelled dimensions, four origin half-dimensions and four locked alignments.");
            var solids = extrusion.get_Geometry(new Options())?.OfType<Solid>()
                .Where(s => s.IsElementGeometry && s.Volume > 0).ToArray();
            if (solids == null || solids.Length == 0)
                throw new InvalidOperationException("The regenerated Extrusion has no positive-volume solid in its own element geometry.");
            var solidCount = solids.Length;
            var namedPlanes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Count(p => p.Name != null && p.Name.StartsWith("RvtMcp ", StringComparison.Ordinal));
            if (namedPlanes != 4) throw new InvalidOperationException("Native reference-plane read-back did not find the four required planes.");
            var box = extrusion.get_BoundingBox(null);
            if (box == null) throw new InvalidOperationException("The extrusion has no geometry bounding box after regeneration.");
            var actual = new[] { box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z }
                .Select(ParametricFamilyInput.MillimetersFromFeet).ToArray();
            var matches = actual.Select((value, i) => Math.Abs(value - dimensions[i]) <= 0.01).ToArray();
            if (!matches.All(match => match))
                throw new InvalidOperationException("Regenerated extrusion geometry does not match the requested millimeter dimensions.");
            return new
            {
                family_name = familyName, document_title = doc.Title, family_type = typeName,
                native_editable = true, geometry_kind = "extrusion", parameters = read,
                geometry = new
                {
                    extrusion_id = RevitCompat.GetId(extrusion.Id).ToString(CultureInfo.InvariantCulture), solid_count = solidCount,
                    reference_plane_count = namedPlanes, labelled_dimension_count = constraints.Dimensions.Count,
                    origin_equality_count = constraints.Equalities.Count, locked_alignment_count = alignmentCount,
                    bounding_box_mm = new { length = actual[0], width = actual[1], height = actual[2] },
                    dimensions_match = matches.All(m => m)
                }
            };
        }
#endif
    }
}
