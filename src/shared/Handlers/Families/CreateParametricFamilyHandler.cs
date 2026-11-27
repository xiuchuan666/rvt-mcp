using System;
using System.IO;
#if REVIT2027
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FailureControl = RvtMcp.Plugin.Failures.FailureControl;
#endif
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;


namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Revit 2027 family-authoring slice: one native extrusion whose Length, Width and Height
    /// are type parameters driven by labelled dimensions and alignment locks.
    /// Order: create planes → label dimensions on sketch curves → alignment lock → Height association.
    /// Label-before-lock is required: Revit rejects FamilyLabel on locked sketch curves.
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

                            stage = "creating reference planes at sketch positions";
                            var halfLength = ParametricFamilyInput.ToRevitFeet(dimensions[0] * 0.5);
                            var halfWidth = ParametricFamilyInput.ToRevitFeet(dimensions[1] * 0.5);
                            var leftPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Left", new XYZ(-halfLength, -halfWidth * 2, 0), new XYZ(-halfLength, halfWidth * 2, 0));
                            var rightPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Right", new XYZ(halfLength, -halfWidth * 2, 0), new XYZ(halfLength, halfWidth * 2, 0));
                            var frontPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Front", new XYZ(-halfLength * 2, -halfWidth, 0), new XYZ(halfLength * 2, -halfWidth, 0));
                            var backPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Back", new XYZ(-halfLength * 2, halfWidth, 0), new XYZ(halfLength * 2, halfWidth, 0));
                            familyDoc.Regenerate();

                            stage = "creating labelled Length dimension from sketch curve references";
                            CreateLabelledDimensionFromCurves(familyDoc, plan, extrusion, isLength: true, parameters["Length"]);
                            stage = "creating labelled Width dimension from sketch curve references";
                            CreateLabelledDimensionFromCurves(familyDoc, plan, extrusion, isLength: false, parameters["Width"]);
                            familyDoc.Regenerate();

                            stage = "aligning and locking sketch curves to reference planes";
                            AlignSketchCurvesToPlanes(familyDoc, plan, extrusion, leftPlane, rightPlane, frontPlane, backPlane);
                            familyDoc.Regenerate();

                            stage = "associating extrusion Height";
                            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
                            if (end == null || end.IsReadOnly) throw new InvalidOperationException("The extrusion end parameter is unavailable or read-only.");
                            manager.AssociateElementParameterToFamilyParameter(end, parameters["Height"]);
                            stage = "regenerating and reading back native geometry";
                            familyDoc.Regenerate();
                            familyData = ReadBack(familyDoc, manager, parameters, extrusion, familyName, typeName, dimensions);
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

        // ── Native parameter-driven constraints ──

        private static ReferencePlane CreateNamedPlane(Document doc, ViewPlan plan, string name, XYZ bubble, XYZ free)
        {
            var plane = doc.FamilyCreate.NewReferencePlane(bubble, free, XYZ.BasisZ, plan);
            if (plane == null) throw new InvalidOperationException("Failed to create reference plane: " + name);
            plane.Name = name;
            return plane;
        }

        private static void AlignSketchCurvesToPlanes(Document doc, ViewPlan plan, Extrusion extrusion,
            ReferencePlane left, ReferencePlane right, ReferencePlane front, ReferencePlane back)
        {
            var sketchCurves = extrusion.Sketch.GetAllElements()
                .Select(id => doc.GetElement(id) as ModelCurve)
                .Where(mc => mc?.GeometryCurve is Line)
                .Select(mc => new { Curve = mc, Line = (Line)mc.GeometryCurve })
                .ToList();

            var verticalLines = sketchCurves
                .Where(mc => Math.Abs(mc.Line.Direction.Y) > 0.99)
                .OrderBy(mc => mc.Line.GetEndPoint(0).X)
                .ToList();
            var horizontalLines = sketchCurves
                .Where(mc => Math.Abs(mc.Line.Direction.X) > 0.99)
                .OrderBy(mc => mc.Line.GetEndPoint(0).Y)
                .ToList();

            if (verticalLines.Count < 2) throw new InvalidOperationException("Extrusion sketch has fewer than 2 vertical lines.");
            if (horizontalLines.Count < 2) throw new InvalidOperationException("Extrusion sketch has fewer than 2 horizontal lines.");

            // Align vertical lines to left/right planes
            AlignAndLock(doc, plan, verticalLines.First().Curve, left, "Left");
            doc.Regenerate();
            AlignAndLock(doc, plan, verticalLines.Last().Curve, right, "Right");
            doc.Regenerate();

            // Align horizontal lines to front/back planes
            AlignAndLock(doc, plan, horizontalLines.First().Curve, front, "Front");
            doc.Regenerate();
            AlignAndLock(doc, plan, horizontalLines.Last().Curve, back, "Back");
        }

        private static void AlignAndLock(Document doc, ViewPlan plan, ModelCurve sketchCurve, ReferencePlane plane, string label)
        {
            var sketchRef = sketchCurve.GeometryCurve?.Reference;
            var planeRef = plane.GetReference();
            if (sketchRef == null) throw new InvalidOperationException("Sketch curve reference is null for " + label + " alignment.");
            if (planeRef == null) throw new InvalidOperationException("Reference plane reference is null for " + label + " alignment.");

            var alignment = doc.FamilyCreate.NewAlignment(plan, sketchRef, planeRef);
            if (alignment == null) throw new InvalidOperationException("NewAlignment returned null for " + label + ".");

            alignment.IsLocked = true;
        }

        /// <summary>
        /// Creates a labelled dimension from sketch ModelCurve geometric references.
        /// FamilyLabel requires genuine geometric references (ModelCurve.GeometryCurve.Reference
        /// with ':0:LINEAR' suffix), not plain ReferencePlane references.
        /// </summary>
        private static Dimension CreateLabelledDimensionFromCurves(Document doc, ViewPlan plan,
            Extrusion extrusion, bool isLength, FamilyParameter labelParam)
        {
            var sketchCurves = extrusion.Sketch.GetAllElements()
                .Select(id => doc.GetElement(id) as ModelCurve)
                .Where(mc => mc?.GeometryCurve is Line)
                .Select(mc => new { Curve = mc, Line = (Line)mc.GeometryCurve })
                .ToList();

            // Length: vertical lines (Y-direction); Width: horizontal lines (X-direction)
            var axisLines = isLength
                ? sketchCurves.Where(mc => Math.Abs(mc.Line.Direction.Y) > 0.99).OrderBy(mc => mc.Line.GetEndPoint(0).X).ToList()
                : sketchCurves.Where(mc => Math.Abs(mc.Line.Direction.X) > 0.99).OrderBy(mc => mc.Line.GetEndPoint(0).Y).ToList();

            if (axisLines.Count < 2)
                throw new InvalidOperationException("Insufficient sketch lines for " + (isLength ? "Length" : "Width") + " dimension.");

            var firstLine = axisLines.First();
            var secondLine = axisLines.Last();
            var firstRef = firstLine.Curve.GeometryCurve?.Reference;
            var secondRef = secondLine.Curve.GeometryCurve?.Reference;
            if (firstRef == null || secondRef == null)
                throw new InvalidOperationException("Sketch curve geometric reference is null for dimension.");

            var dimStart = isLength
                ? new XYZ(firstLine.Line.GetEndPoint(0).X, 0, 0)
                : new XYZ(0, firstLine.Line.GetEndPoint(0).Y, 0);
            var dimEnd = isLength
                ? new XYZ(secondLine.Line.GetEndPoint(0).X, 0, 0)
                : new XYZ(0, secondLine.Line.GetEndPoint(0).Y, 0);

            var refs = new ReferenceArray();
            refs.Append(firstRef);
            refs.Append(secondRef);
            var dim = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(dimStart, dimEnd), refs);
            if (dim == null) throw new InvalidOperationException("NewDimension returned null.");
            dim.FamilyLabel = labelParam;
            if (dim.FamilyLabel == null || dim.FamilyLabel.Definition.Name != labelParam.Definition.Name)
                throw new InvalidOperationException("Dimension FamilyLabel did not accept parameter " + labelParam.Definition.Name + ".");
            return dim;
        }

        private static bool IsFamilyParameter(FamilyParameter actual, FamilyParameter expected)
        {
            // Revit may return a new Parameter wrapper on every API access. Compare stable identity.
            return actual != null && expected != null
                && RevitCompat.GetId(actual.Id) == RevitCompat.GetId(expected.Id) && actual.Definition.Name == expected.Definition.Name;
        }

        private static object ReadBack(Document doc, FamilyManager manager,
            IReadOnlyDictionary<string, FamilyParameter> parameters, Extrusion extrusion,
            string familyName, string typeName, double[] dimensions)
        {
            var names = new[] { "Length", "Width", "Height" };
            var read = new List<object>();
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);

            // Check dimension-based associations for Length and Width
            var dimensions_list = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().ToList();
            var lengthDim = dimensions_list.FirstOrDefault(d => d.FamilyLabel != null && d.FamilyLabel.Definition.Name == "Length");
            var widthDim = dimensions_list.FirstOrDefault(d => d.FamilyLabel != null && d.FamilyLabel.Definition.Name == "Width");

            var associated = new[]
            {
                lengthDim != null && IsFamilyParameter(lengthDim.FamilyLabel, parameters["Length"]),
                widthDim != null && IsFamilyParameter(widthDim.FamilyLabel, parameters["Width"]),
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
            var solids = extrusion.get_Geometry(new Options())?.OfType<Solid>()
                .Where(s => s.IsElementGeometry && s.Volume > 0).ToArray();
            if (solids == null || solids.Length == 0)
                throw new InvalidOperationException("The regenerated Extrusion has no positive-volume solid in its own element geometry.");
            var solidCount = solids.Length;
            var box = extrusion.get_BoundingBox(null);
            if (box == null) throw new InvalidOperationException("The extrusion has no geometry bounding box after regeneration.");
            var actual = new[] { box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z }
                .Select(ParametricFamilyInput.MillimetersFromFeet).ToArray();
            var matches = actual.Select((value, i) => Math.Abs(value - dimensions[i]) <= 0.01).ToArray();
            if (!matches.All(match => match))
                throw new InvalidOperationException("Regenerated extrusion geometry does not match the requested millimeter dimensions.");

            // Count alignment locks
            var alignmentCount = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>()
                .Count(d => d.IsLocked);

            return new
            {
                family_name = familyName, document_title = doc.Title, family_type = typeName,
                native_editable = true, geometry_kind = "extrusion", parameters = read,
                geometry = new
                {
                    extrusion_id = RevitCompat.GetId(extrusion.Id).ToString(CultureInfo.InvariantCulture), solid_count = solidCount,
                    reference_plane_count = 4, labelled_dimension_count = 2,
                    origin_equality_count = 0, locked_alignment_count = alignmentCount,
                    bounding_box_mm = new { length = actual[0], width = actual[1], height = actual[2] },
                    dimensions_match = matches.All(m => m)
                }
            };
        }
#endif
    }
}
