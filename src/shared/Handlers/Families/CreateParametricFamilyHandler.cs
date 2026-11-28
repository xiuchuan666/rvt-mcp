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
    /// are type parameters driven by labelled dimensions and 3D face alignment locks.
    /// Three sequential transactions (bones→muscle→skin):
    ///   Tx1: parameters and type values committed first
    ///   Tx2: reference planes + EQ + labelled dimensions (no extrusion yet)
    ///   Tx3: extrusion + Height association + 3D face alignment locks
    /// Follows the Heron-AI verified pattern.
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
                    familyDoc = McpDocument.TrackOpened(app.Application.NewFamilyDocument(templatePath));
                    if (familyDoc == null || !familyDoc.IsFamilyDocument)
                        throw new InvalidOperationException("Revit did not open the supplied template as a family document.");
                    if (IsActiveDocument(app, familyDoc))
                    {
                        closeAttempted = true;
                        throw new InvalidOperationException("Revit opened the new family as the active document, which cannot be closed through Document.Close. The family remains open for user review; no file was written.");
                    }

                    var dimensions = new[] { lengthMm, widthMm, heightMm };
                    var halfLength = ParametricFamilyInput.ToRevitFeet(lengthMm * 0.5);
                    var halfWidth = ParametricFamilyInput.ToRevitFeet(widthMm * 0.5);
                    var heightFt = ParametricFamilyInput.ToRevitFeet(heightMm);
                    var manager = familyDoc.FamilyManager;
                    if (manager == null) throw new InvalidOperationException("The family template does not expose a FamilyManager.");
                    var plan = SelectFamilyPlan(familyDoc);
                    var z0 = plan.GenLevel.Elevation;

                    // ── Tx1: parameters and type values ──
                    stage = "Tx1: creating family type and parameters";
                    Dictionary<string, FamilyParameter> parameters;
                    using (var tx1 = new Transaction(familyDoc, "RvtMcp Tx1: type and parameters"))
                    {
                        tx1.Start();
                        try
                        {
                            var familyType = SelectType(manager, typeName);
                            parameters = CreateParameters(manager);
                            SetValues(manager, parameters, dimensions);
                            if (tx1.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Tx1 failed to commit.");
                        }
                        catch
                        {
                            if (tx1.GetStatus() == TransactionStatus.Started) tx1.RollBack();
                            throw;
                        }
                    }

                    // ── Tx2: reference planes + EQ + labelled dimensions ──
                    stage = "Tx2: creating reference planes and labelled dimensions";
                    ReferencePlane leftPlane, rightPlane, frontPlane, backPlane;
                    int eqCount;
                    using (var tx2 = new Transaction(familyDoc, "RvtMcp Tx2: planes and dimensions"))
                    {
                        tx2.Start();
                        try
                        {

                        // Find or create center planes
                        var centerLR = FindOrCreateCenterPlane(familyDoc, plan, "Center (Left/Right)", XYZ.BasisX, z0);
                        var centerFB = FindOrCreateCenterPlane(familyDoc, plan, "Center (Front/Back)", XYZ.BasisY, z0);

                        // Create 4 outer planes at exact positions
                        var reach = Math.Max(halfLength, halfWidth) * 2 + 1.0;
                        leftPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Left",
                            new XYZ(-halfLength, -reach, z0), new XYZ(-halfLength, reach, z0));
                        rightPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Right",
                            new XYZ(halfLength, -reach, z0), new XYZ(halfLength, reach, z0));
                        frontPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Front",
                            new XYZ(-reach, -halfWidth, z0), new XYZ(reach, -halfWidth, z0));
                        backPlane = CreateNamedPlane(familyDoc, plan, "RvtMcp Back",
                            new XYZ(-reach, halfWidth, z0), new XYZ(reach, halfWidth, z0));
                        familyDoc.Regenerate();

                        // Length EQ: [left, centerLR, right], offset below the rectangle
                        var yEq = -(halfWidth + 1.0);
                        var yDim = -(halfWidth + 1.5);
                        var eqLength = CreateEQDimension(familyDoc, plan, leftPlane, centerLR, rightPlane,
                            new XYZ(-halfLength, yEq, z0), new XYZ(halfLength, yEq, z0));

                        // Length labelled dimension: [left, right], further offset
                        var lengthLabel = CreateLabelledDimensionOnPlanes(familyDoc, plan, leftPlane, rightPlane,
                            new XYZ(-halfLength, yDim, z0), new XYZ(halfLength, yDim, z0));

                        // Width EQ: [front, centerFB, back], offset to the left
                        var xEq = -(halfLength + 1.0);
                        var xDim = -(halfLength + 1.5);
                        var eqWidth = CreateEQDimension(familyDoc, plan, frontPlane, centerFB, backPlane,
                            new XYZ(xEq, -halfWidth, z0), new XYZ(xEq, halfWidth, z0));

                        // Width labelled dimension: [front, back], further offset
                        var widthLabel = CreateLabelledDimensionOnPlanes(familyDoc, plan, frontPlane, backPlane,
                            new XYZ(xDim, -halfWidth, z0), new XYZ(xDim, halfWidth, z0));

                        familyDoc.Regenerate();

                        // Set FamilyLabel after regeneration
                        stage = "Tx2: setting FamilyLabel on Length dimension";
                        lengthLabel.FamilyLabel = parameters["Length"];
                        if (lengthLabel.FamilyLabel == null || lengthLabel.FamilyLabel.Definition.Name != "Length")
                        {
                            // Diagnostic dump on failure
                            var diag = $"DimensionShape={lengthLabel.DimensionShape}, Segments={lengthLabel.NumberOfSegments}, "
                                + $"IsLocked={lengthLabel.IsLocked}, Value={(lengthLabel.NumberOfSegments <= 1 ? Convert.ToString(lengthLabel.Value, CultureInfo.InvariantCulture) : "n/a (multi-segment)")}, Refs={lengthLabel.References.Size}, "
                                + $"DIM_LABEL_RO={lengthLabel.get_Parameter(BuiltInParameter.DIM_LABEL)?.IsReadOnly}, "
                                + $"IsFamily={familyDoc.IsFamilyDocument}, IsReporting={parameters["Length"].IsReporting}, "
                                + $"DataType={parameters["Length"].Definition.GetDataType()?.TypeId}";
                            throw new InvalidOperationException("Length dimension FamilyLabel rejected. " + diag);
                        }

                        stage = "Tx2: setting FamilyLabel on Width dimension";
                        widthLabel.FamilyLabel = parameters["Width"];
                        if (widthLabel.FamilyLabel == null || widthLabel.FamilyLabel.Definition.Name != "Width")
                        {
                            var diag = $"DimensionShape={widthLabel.DimensionShape}, Segments={widthLabel.NumberOfSegments}, "
                                + $"IsLocked={widthLabel.IsLocked}, Value={(widthLabel.NumberOfSegments <= 1 ? Convert.ToString(widthLabel.Value, CultureInfo.InvariantCulture) : "n/a (multi-segment)")}, Refs={widthLabel.References.Size}, "
                                + $"DIM_LABEL_RO={widthLabel.get_Parameter(BuiltInParameter.DIM_LABEL)?.IsReadOnly}, "
                                + $"IsFamily={familyDoc.IsFamilyDocument}, IsReporting={parameters["Width"].IsReporting}, "
                                + $"DataType={parameters["Width"].Definition.GetDataType()?.TypeId}";
                            throw new InvalidOperationException("Width dimension FamilyLabel rejected. " + diag);
                        }

                        eqCount = 2; // Length EQ + Width EQ
                        if (tx2.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Tx2 failed to commit.");
                        }
                        catch
                        {
                            if (tx2.GetStatus() == TransactionStatus.Started) tx2.RollBack();
                            throw;
                        }
                    }

                    // ── Tx3: extrusion + Height + 3D face alignment locks ──
                    stage = "Tx3: creating extrusion and locking faces";
                    object familyData;
                    using (var tx3 = new Transaction(familyDoc, "RvtMcp Tx3: extrusion and face locks"))
                    {
                        tx3.Start();
                        try
                        {

                        // Create extrusion on level-bound sketch plane
                        var sketchPlane = SketchPlane.Create(familyDoc, plan.GenLevel.Id);
                        var extrusion = CreateExtrusion(familyDoc, sketchPlane, halfLength, halfWidth, heightFt, z0);
                        if (extrusion == null) throw new InvalidOperationException("Revit did not create the extrusion.");

                        // Associate Height
                        stage = "Tx3: associating extrusion Height";
                        var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
                        if (end == null || end.IsReadOnly)
                            throw new InvalidOperationException("The extrusion end parameter is unavailable or read-only.");
                        manager.AssociateElementParameterToFamilyParameter(end, parameters["Height"]);
                        familyDoc.Regenerate();

                        // Lock 3D solid faces to reference planes (not sketch curves)
                        stage = "Tx3: locking 3D faces to reference planes";
                        var lockCount = LockSolidFacesToPlanes(familyDoc, plan, extrusion,
                            new[] { leftPlane, rightPlane, frontPlane, backPlane },
                            new[] { XYZ.BasisX.Negate(), XYZ.BasisX, XYZ.BasisY.Negate(), XYZ.BasisY },
                            new[] { -halfLength, halfLength, -halfWidth, halfWidth });
                        familyDoc.Regenerate();

                        stage = "Tx3: reading back native geometry";
                        familyData = ReadBack(familyDoc, manager, parameters, extrusion, familyName, typeName, dimensions, eqCount, lockCount);

                        if (tx3.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Tx3 failed to commit.");
                        }
                        catch
                        {
                            if (tx3.GetStatus() == TransactionStatus.Started) tx3.RollBack();
                            throw;
                        }
                    }

                    // Save
                    if (File.Exists(outputPath) || Directory.Exists(outputPath))
                        throw new IOException("output_path already exists. This tool never overwrites an existing file or directory.");
                    stage = "saving the temporary family";
                    FailureControl.ThrowIfStopped();
                    using (FailureControl.AuthorizeFamilySave(familyDoc, temporaryPath))
                        familyDoc.SaveAs(temporaryPath, new SaveAsOptions { OverwriteExistingFile = false });
                    FailureControl.ThrowIfStopped();
                    if (!File.Exists(temporaryPath)) throw new IOException("Revit did not create the temporary family file.");
                    if (IsActiveDocument(app, familyDoc))
                    {
                        closeAttempted = true;
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
            var created = manager.NewType(typeName);
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
                    || !existing.Definition.GetDataType().NameEquals(SpecTypeId.Length)
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

        // ── Tx2 helpers: planes, EQ, labelled dimensions ──

        private static ReferencePlane FindOrCreateCenterPlane(Document doc, ViewPlan plan, string name, XYZ normal, double z0)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .FirstOrDefault(p => p.Name == name);
            if (existing != null) return existing;
            // Create through origin with the specified normal
            var run = new XYZ(normal.Y, -normal.X, 0).Multiply(10); // perpendicular to normal
            var plane = doc.FamilyCreate.NewReferencePlane(run.Negate(), run, XYZ.BasisZ, plan);
            plane.Name = name;
            plane.Pinned = true;
            return plane;
        }

        private static ReferencePlane CreateNamedPlane(Document doc, ViewPlan plan, string name, XYZ bubble, XYZ free)
        {
            var plane = doc.FamilyCreate.NewReferencePlane(bubble, free, XYZ.BasisZ, plan);
            if (plane == null) throw new InvalidOperationException("Failed to create reference plane: " + name);
            plane.Name = name;
            return plane;
        }

        private static Dimension CreateEQDimension(Document doc, ViewPlan plan,
            ReferencePlane first, ReferencePlane center, ReferencePlane second, XYZ dimStart, XYZ dimEnd)
        {
            var refs = new ReferenceArray();
            refs.Append(first.GetReference());
            refs.Append(center.GetReference());
            refs.Append(second.GetReference());
            var dim = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(dimStart, dimEnd), refs);
            if (dim == null) throw new InvalidOperationException("EQ NewDimension returned null.");
            dim.AreSegmentsEqual = true;
            return dim;
        }

        private static Dimension CreateLabelledDimensionOnPlanes(Document doc, ViewPlan plan,
            ReferencePlane first, ReferencePlane second, XYZ dimStart, XYZ dimEnd)
        {
            var refs = new ReferenceArray();
            refs.Append(first.GetReference());
            refs.Append(second.GetReference());
            var dim = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(dimStart, dimEnd), refs);
            if (dim == null) throw new InvalidOperationException("Labelled NewDimension returned null.");
            return dim;
        }

        // ── Tx3 helpers: extrusion, Height, 3D face locks ──

        private static Extrusion CreateExtrusion(Document doc, SketchPlane plane,
            double halfLength, double halfWidth, double heightFt, double z0)
        {
            var corners = new[]
            {
                new XYZ(-halfLength, -halfWidth, z0), new XYZ(halfLength, -halfWidth, z0),
                new XYZ(halfLength, halfWidth, z0), new XYZ(-halfLength, halfWidth, z0)
            };
            var loop = new CurveArray();
            for (var i = 0; i < 4; i++)
                loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % 4]));
            var loops = new CurveArrArray();
            loops.Append(loop);
            return doc.FamilyCreate.NewExtrusion(true, loops, plane, heightFt);
        }

        /// <summary>
        /// Lock 3D solid faces to reference planes via NewAlignment.
        /// Matches each plane to the solid's vertical PlanarFace by normal direction and proximity.
        /// </summary>
        private static int LockSolidFacesToPlanes(Document doc, ViewPlan plan, Extrusion extrusion,
            ReferencePlane[] planes, XYZ[] faceNormals, double[] faceCoords)
        {
            var faces = new List<PlanarFace>();
            var geometry = extrusion.get_Geometry(new Options { ComputeReferences = true });
            if (geometry != null)
            {
                foreach (GeometryObject piece in geometry)
                {
                    if (piece is Solid solid)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            if (face is PlanarFace flat && flat.Reference != null)
                                faces.Add(flat);
                        }
                    }
                }
            }

            var lockCount = 0;
            var usedFaces = new HashSet<PlanarFace>();
            for (int i = 0; i < planes.Length; i++)
            {
                var planeRef = planes[i].GetReference();
                var planeGeom = planes[i].GetPlane();
                var expectedNormal = faceNormals[i];

                var match = faces.Where(f => !usedFaces.Contains(f))
                    .Where(f => Math.Abs(f.FaceNormal.DotProduct(expectedNormal)) > 0.9999
                        || Math.Abs(f.FaceNormal.DotProduct(expectedNormal.Negate())) > 0.9999)
                    .Where(f => Math.Abs((f.Origin - planeGeom.Origin).DotProduct(planeGeom.Normal)) < 0.0016) // ~0.5mm
                    .ToList();

                if (match.Count != 1)
                    throw new InvalidOperationException(
                        $"Face lock: expected 1 match for plane '{planes[i].Name}', found {match.Count}. "
                        + $"Normal={expectedNormal}, planeOrigin={planeGeom.Origin}, candidateFaces={faces.Count}");

                var face = match[0];
                usedFaces.Add(face);

                var alignment = doc.FamilyCreate.NewAlignment(plan, planeRef, face.Reference);
                if (alignment == null)
                    throw new InvalidOperationException("NewAlignment returned null for face lock on " + planes[i].Name);

                // A linear multi-segment alignment is already constrained by the EQ skeleton.
                // Revit forbids setting IsLocked on it, so leave that native constraint intact.
                var alignmentDescription = $"plane={planes[i].Name}, DimensionShape={alignment.DimensionShape}, Segments={alignment.NumberOfSegments}";
                if (alignment.NumberOfSegments > 1) continue;
                try
                {
                    if (!alignment.IsLocked) alignment.IsLocked = true;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Failed to lock alignment (" + alignmentDescription + "): " + ex.Message, ex);
                }
                if (!alignment.IsLocked)
                    throw new InvalidOperationException("Alignment was not locked after setting IsLocked (" + alignmentDescription + ").");
                lockCount++;
            }
            return lockCount;
        }

        private static bool IsFamilyParameter(FamilyParameter actual, FamilyParameter expected)
        {
            return actual != null && expected != null
                && RevitCompat.GetId(actual.Id) == RevitCompat.GetId(expected.Id) && actual.Definition.Name == expected.Definition.Name;
        }

        private static FamilyParameter SafeFamilyLabel(Dimension dimension)
        {
            // Revit throws for dimensions that cannot be labelled (for example, locked alignments).
            try { return dimension.FamilyLabel; }
            catch { return null; }
        }

        private static bool SafeIsLocked(Dimension dimension)
        {
            // Reading IsLocked on a multi-segment linear dimension (such as the EQ skeleton
            // dimensions) can throw on native Revit. Count only dimensions whose lock state is
            // readable so locked_alignment_count aggregation never aborts the readback.
            try { return dimension.IsLocked; }
            catch { return false; }
        }

        private static object ReadBack(Document doc, FamilyManager manager,
            IReadOnlyDictionary<string, FamilyParameter> parameters, Extrusion extrusion,
            string familyName, string typeName, double[] dimensions, int eqCount, int lockCount)
        {
            var names = new[] { "Length", "Width", "Height" };
            var read = new List<object>();
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);

            var allDims = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().ToList();
            var lengthDim = allDims.FirstOrDefault(d =>
            {
                var label = SafeFamilyLabel(d);
                return label != null && label.Definition.Name == "Length";
            });
            var widthDim = allDims.FirstOrDefault(d =>
            {
                var label = SafeFamilyLabel(d);
                return label != null && label.Definition.Name == "Width";
            });

            var associated = new[]
            {
                lengthDim != null && IsFamilyParameter(SafeFamilyLabel(lengthDim), parameters["Length"]),
                widthDim != null && IsFamilyParameter(SafeFamilyLabel(widthDim), parameters["Width"]),
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

            var alignmentCount = allDims.Count(SafeIsLocked);

            return new
            {
                family_name = familyName, document_title = doc.Title, family_type = typeName,
                native_editable = true, geometry_kind = "extrusion", parameters = read,
                geometry = new
                {
                    extrusion_id = RevitCompat.GetId(extrusion.Id).ToString(CultureInfo.InvariantCulture), solid_count = solidCount,
                    reference_plane_count = 4, labelled_dimension_count = 2,
                    origin_equality_count = eqCount, locked_alignment_count = alignmentCount,
                    bounding_box_mm = new { length = actual[0], width = actual[1], height = actual[2] },
                    dimensions_match = matches.All(m => m)
                }
            };
        }
#endif
    }
}
