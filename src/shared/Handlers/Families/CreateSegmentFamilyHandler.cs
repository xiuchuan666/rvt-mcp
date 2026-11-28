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
    /// Revit 2027 tunnel-segment family. Bones: reference arcs/radial lines with native
    /// radial/angular labels. Muscle: formula-derived radii. Skin: the four-sided extrusion
    /// locked to that skeleton. Native hosted flex verification remains an acceptance gate.
    /// </summary>
    public sealed class CreateSegmentFamilyHandler : IRevitCommand
    {
        private static readonly string[] Names = { "OuterDiameter", "Thickness", "RingWidth", "SegmentAngle" };
        public string Name => "create_segment_family";
        public string Description => "Revit 2027: create a native arc-extrusion tunnel segment driven by four type parameters; never overwrites a file.";
        public string ParametersSchema
        {
            get
            {
#if REVIT2027
                return @"{
  ""type"": ""object"",
  ""required"": [""family_name"", ""template_path"", ""output_path"", ""outer_diameter_mm"", ""thickness_mm"", ""ring_width_mm"", ""segment_angle_deg""],
  ""properties"": {
    ""family_name"": {""type"": ""string""},
    ""template_path"": {""type"": ""string"", ""description"": ""Absolute path of a Revit family template (.rft).""},
    ""output_path"": {""type"": ""string"", ""description"": ""New absolute local .rfa path. Existing files are never overwritten.""},
    ""type_name"": {""type"": ""string"", ""default"": ""Standard""},
    ""outer_diameter_mm"": {""type"": ""number"", ""exclusiveMinimum"": 0},
    ""thickness_mm"": {""type"": ""number"", ""exclusiveMinimum"": 0},
    ""ring_width_mm"": {""type"": ""number"", ""exclusiveMinimum"": 0},
    ""segment_angle_deg"": {""type"": ""number"", ""exclusiveMinimum"": 0, ""exclusiveMaximum"": 360},
    ""timeout_seconds"": {""type"": ""integer"", ""minimum"": 1, ""maximum"": 900, ""default"": 300}
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
            return CommandResult.Fail("UNSUPPORTED_VERSION: create_segment_family requires Revit 2027. This Revit version was not changed and no file was written.");
#else
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            var familyName = request.Value<string>("family_name")?.Trim();
            var typeName = request.Value<string>("type_name") ?? "Standard";
            var templatePath = request.Value<string>("template_path");
            var outputPath = request.Value<string>("output_path");
            var outerDiameter = request.Value<double?>("outer_diameter_mm") ?? double.NaN;
            var thickness = request.Value<double?>("thickness_mm") ?? double.NaN;
            var ringWidth = request.Value<double?>("ring_width_mm") ?? double.NaN;
            var angle = request.Value<double?>("segment_angle_deg") ?? double.NaN;
            var error = SegmentFamilyInput.Validate(familyName, typeName, outerDiameter, thickness, ringWidth, angle)
                ?? SegmentFamilyInput.ValidateFiles(templatePath, outputPath);
            if (error != null) return CommandResult.Fail(error);
            templatePath = Path.GetFullPath(templatePath);
            outputPath = Path.GetFullPath(outputPath);
            var temporaryPath = SegmentFamilyInput.TemporaryOutputPath(outputPath);
            Document familyDoc = null;
            var closed = false;
            var closeAttempted = false;
            var saved = false;
            var closeError = (string)null;
            var stage = "opening the family template";
            CommandResult outcome;
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
                    var plan = SelectFamilyPlan(familyDoc);
                    var z0 = plan.GenLevel.Elevation;
                    var outerFt = SegmentFamilyInput.ToRevitFeet(outerDiameter * 0.5);
                    var innerFt = SegmentFamilyInput.ToRevitFeet(outerDiameter * 0.5 - thickness);
                    var widthFt = SegmentFamilyInput.ToRevitFeet(ringWidth);
                    var halfAngle = SegmentFamilyInput.HalfAngleRadians(angle);
                    var center = new XYZ(0, 0, z0);
                    var manager = familyDoc.FamilyManager;
                    if (manager == null) throw new InvalidOperationException("The family template does not expose a FamilyManager.");
                    Dictionary<string, FamilyParameter> parameters;

                    stage = "Tx1: creating family type, four parameters and derived radii";
                    using (var tx = new Transaction(familyDoc, "RvtMcp segment Tx1: type and parameters"))
                    {
                        tx.Start();
                        try
                        {
                            SelectType(manager, typeName);
                            parameters = CreateParameters(manager);
                            manager.Set(parameters["OuterDiameter"], SegmentFamilyInput.ToRevitFeet(outerDiameter));
                            manager.Set(parameters["Thickness"], SegmentFamilyInput.ToRevitFeet(thickness));
                            manager.Set(parameters["RingWidth"], widthFt);
                            manager.Set(parameters["SegmentAngle"], angle / SegmentFamilyInput.DegreesPerRadian);
                            RequireCommit(tx, "Tx1");
                        }
                        catch { RollBack(tx); throw; }
                    }

                    ModelCurve outerReference, innerReference;
                    Dimension outerRadius, innerRadius, angular;
                    ModelCurve negativeLine, positiveLine;
                    stage = "Tx2: creating arc skeleton and native driving dimensions";
                    using (var tx = new Transaction(familyDoc, "RvtMcp segment Tx2: arc skeleton and dimensions"))
                    {
                        tx.Start();
                        try
                        {
                            var referencePlane = SketchPlane.Create(familyDoc, plan.GenLevel.Id);
                            // Negative angles put start/end at -halfAngle/+halfAngle on the XY plane.
                            var outer = Arc.Create(center, outerFt, -halfAngle, halfAngle, XYZ.BasisX, XYZ.BasisY);
                            var inner = Arc.Create(center, innerFt, -halfAngle, halfAngle, XYZ.BasisX, XYZ.BasisY);
                            outerReference = familyDoc.FamilyCreate.NewModelCurve(outer, referencePlane);
                            innerReference = familyDoc.FamilyCreate.NewModelCurve(inner, referencePlane);
                            var negative = Point(outerFt, -halfAngle, z0);
                            var positive = Point(outerFt, halfAngle, z0);
                            negativeLine = familyDoc.FamilyCreate.NewModelCurve(Line.CreateBound(center, negative), referencePlane);
                            positiveLine = familyDoc.FamilyCreate.NewModelCurve(Line.CreateBound(center, positive), referencePlane);
                            familyDoc.Regenerate();
                            if (outerReference?.GeometryCurve?.Reference == null || innerReference?.GeometryCurve?.Reference == null)
                                throw new InvalidOperationException("The reference arcs did not expose stable geometric references.");
                            // Plan A: do not manufacture an arc-center reference. Curve endpoints only
                            // expose indices 0 and 1. Radius/angular labels and the radial profile locks
                            // are the candidate constraint scheme; hosted flex must verify center stability.
                            outerRadius = familyDoc.FamilyCreate.NewRadialDimension(plan, outerReference.GeometryCurve.Reference, new XYZ(outerFt, 0, z0));
                            innerRadius = familyDoc.FamilyCreate.NewRadialDimension(plan, innerReference.GeometryCurve.Reference, new XYZ(innerFt, 0, z0));
                            familyDoc.Regenerate();
                            SetLabel(outerRadius, parameters["OuterRadius"], "OuterRadius");
                            SetLabel(innerRadius, parameters["InnerRadius"], "InnerRadius");
                            var extension = Arc.Create(center, (innerFt + outerFt) * 0.5, -halfAngle, halfAngle, XYZ.BasisX, XYZ.BasisY);
                            angular = familyDoc.FamilyCreate.NewAngularDimension(plan, extension,
                                negativeLine.GeometryCurve.Reference, positiveLine.GeometryCurve.Reference);
                            familyDoc.Regenerate();
                            SetLabel(angular, parameters["SegmentAngle"], "SegmentAngle");
                            if (angular.NumberOfSegments > 1) throw new InvalidOperationException("The angular driving dimension unexpectedly has multiple segments.");
                            RequireCommit(tx, "Tx2");
                        }
                        catch { RollBack(tx); throw; }
                    }

                    object familyData;
                    stage = "Tx3: creating extrusion, locking the profile and reading native geometry";
                    using (var tx = new Transaction(familyDoc, "RvtMcp segment Tx3: skin and profile locks"))
                    {
                        tx.Start();
                        try
                        {
                            var sketchPlane = SketchPlane.Create(familyDoc, plan.GenLevel.Id);
                            var extrusion = familyDoc.FamilyCreate.NewExtrusion(true, CreateProfile(center, outerFt, innerFt, halfAngle, z0), sketchPlane, widthFt);
                            if (extrusion == null) throw new InvalidOperationException("Revit did not create the segment extrusion.");
                            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
                            if (end == null || end.IsReadOnly) throw new InvalidOperationException("The extrusion end parameter is unavailable or read-only.");
                            manager.AssociateElementParameterToFamilyParameter(end, parameters["RingWidth"]);
                            familyDoc.Regenerate();
                            var profileAlignments = LockProfile(familyDoc, plan, extrusion, outerReference, innerReference, negativeLine, positiveLine);
                            familyDoc.Regenerate();
                            familyData = ReadBack(familyDoc, manager, parameters, extrusion, familyName, typeName,
                                new[] { outerDiameter, thickness, ringWidth, angle },
                                new[] { outerReference.Id, innerReference.Id }, new[] { negativeLine.Id, positiveLine.Id }, profileAlignments);
                            RequireCommit(tx, "Tx3");
                        }
                        catch { RollBack(tx); throw; }
                    }

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
                    result["host_verification"] = "pending_revit_2027_native_flex";
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
                            if (IsActiveDocument(app, familyDoc)) closeError = "The new family is Revit's active document, which Document.Close cannot close. Close or save it manually in Revit before retrying.";
                            else
                            {
                                try { closed = McpDocument.Close(familyDoc, false); if (!closed) closeError = "Revit returned false when closing the family document."; }
                                catch (Exception closeException) { closeError = closeException.Message; }
                            }
                        }
                        var cleanupError = closed || familyDoc == null ? SegmentFamilyInput.DeleteTemporaryFile(temporaryPath)
                            : "The owned family document remains open; its temporary file, if created, was retained for inspection at " + temporaryPath + ".";
                        if ((closed || familyDoc == null) && cleanupError == null && FailureControl.Active != null)
                            FailureControl.Active.MarkPrivateFamilyDiscarded();
                        var lifecycle = "Family creation failed; no requested output file was written. "
                            + (closed ? "The family document was closed without saving to output_path. " : familyDoc == null ? "No family document was opened or it was already closed. " : "The family document was not closed; see the lifecycle detail. ")
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
        private static bool IsActiveDocument(UIApplication app, Document document) => app.ActiveUIDocument?.Document != null && app.ActiveUIDocument.Document.Equals(document);
        private static void RequireCommit(Transaction tx, string name) { if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException(name + " failed to commit."); }
        private static void RollBack(Transaction tx) { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); }
        private static XYZ Point(double radius, double angle, double z) => new XYZ(radius * Math.Cos(angle), radius * Math.Sin(angle), z);

        private static ViewPlan SelectFamilyPlan(Document doc)
        {
            var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null && Math.Abs(v.GenLevel.Elevation) <= 1e-8);
            if (plan == null) throw new InvalidOperationException("Use a standard 3D family template with a floor plan at zero elevation; this tool does not support annotation, massing or specialized family templates.");
            return plan;
        }

        private static void SelectType(FamilyManager manager, string typeName)
        {
            var existing = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == typeName);
            if (existing != null) { manager.CurrentType = existing; return; }
            var created = manager.NewType(typeName);
            if (created == null || manager.CurrentType == null) throw new InvalidOperationException("Revit could not create and select the requested family type.");
            manager.CurrentType = created;
        }

        private static Dictionary<string, FamilyParameter> CreateParameters(FamilyManager manager)
        {
            var definitions = new[] {
                new { Name = "OuterDiameter", Spec = SpecTypeId.Length }, new { Name = "Thickness", Spec = SpecTypeId.Length },
                new { Name = "RingWidth", Spec = SpecTypeId.Length }, new { Name = "SegmentAngle", Spec = SpecTypeId.Angle },
                new { Name = "OuterRadius", Spec = SpecTypeId.Length }, new { Name = "InnerRadius", Spec = SpecTypeId.Length }
            };
            var result = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                var existing = manager.GetParameters().FirstOrDefault(p => p.Definition.Name == definition.Name);
                var derived = definition.Name == "OuterRadius" || definition.Name == "InnerRadius";
                if (existing != null && (existing.IsInstance || existing.IsReporting || existing.StorageType != StorageType.Double
                    || !existing.Definition.GetDataType().NameEquals(definition.Spec) || existing.Formula != null || existing.AssociatedParameters.Size != 0))
                    throw new InvalidOperationException("The family template has an incompatible or already-used parameter named " + definition.Name + ".");
                var parameter = existing ?? manager.AddParameter(definition.Name, GroupTypeId.Geometry, definition.Spec, false);
                if (parameter == null || manager.CurrentType.AsDouble(parameter) == null) throw new InvalidOperationException("Family parameter " + definition.Name + " has no numeric value.");
                if (derived) manager.SetFormula(parameter, definition.Name == "OuterRadius" ? "OuterDiameter / 2" : "OuterDiameter / 2 - Thickness");
                result.Add(definition.Name, parameter);
            }
            return result;
        }

        private static void SetLabel(Dimension dimension, FamilyParameter parameter, string name)
        {
            if (dimension == null) throw new InvalidOperationException("Revit returned no native driving dimension for " + name + ".");
            dimension.FamilyLabel = parameter;
            var actual = SafeFamilyLabel(dimension);
            if (!SameParameter(actual, parameter)) throw new InvalidOperationException(name + " dimension FamilyLabel was not retained by Revit.");
        }

        private static CurveArrArray CreateProfile(XYZ center, double outer, double inner, double halfAngle, double z)
        {
            var p1 = Point(outer, -halfAngle, z); var p2 = Point(outer, halfAngle, z);
            var p3 = Point(inner, halfAngle, z); var p4 = Point(inner, -halfAngle, z);
            var loop = new CurveArray();
            loop.Append(Arc.Create(p1, p2, new XYZ(outer, 0, z)));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Arc.Create(p3, p4, new XYZ(inner, 0, z)));
            loop.Append(Line.CreateBound(p4, p1));
            var loops = new CurveArrArray(); loops.Append(loop); return loops;
        }

        private static List<Dimension> LockProfile(Document doc, ViewPlan plan, Extrusion extrusion, ModelCurve outer, ModelCurve inner, ModelCurve negativeLine, ModelCurve positiveLine)
        {
            var referenceCurves = new List<Curve>();
            foreach (var id in new[] { outer.Id, inner.Id })
            {
                var model = doc.GetElement(id) as ModelCurve;
                if (model?.GeometryCurve?.Reference == null) throw new InvalidOperationException("The skeleton arc lost its stable geometric reference.");
                referenceCurves.Add(model.GeometryCurve);
            }
            var sketchCurves = extrusion.Sketch.GetAllElements().Select(id => doc.GetElement(id) as ModelCurve).Where(c => c?.GeometryCurve?.Reference != null).Select(c => c.GeometryCurve).ToList();
            var alignments = new List<Dimension>();
            foreach (var arc in sketchCurves.OfType<Arc>())
            {
                var expected = referenceCurves.OfType<Arc>().Where(r => Math.Abs(r.Radius - arc.Radius) < 1e-6 && r.Center.DistanceTo(arc.Center) < 1e-6).ToList();
                if (expected.Count != 1) throw new InvalidOperationException("The extrusion arc does not coincide with exactly one skeleton arc.");
                alignments.Add(LockAlignment(doc, plan, expected[0].Reference, arc.Reference, "arc profile"));
            }
            // Each radial side is constrained to the radial reference line nearest its midpoint.
            var radialLines = new[] { negativeLine.GeometryCurve, positiveLine.GeometryCurve };
            foreach (var line in sketchCurves.OfType<Line>())
            {
                var midpoint = line.Evaluate(0.5, true);
                var candidates = radialLines.Where(reference => reference.Project(midpoint) != null
                    && reference.Project(midpoint).XYZPoint.DistanceTo(midpoint) <= 1e-6).ToList();
                if (candidates.Count != 1) throw new InvalidOperationException("Expected exactly one collinear radial reference for an extrusion profile side; found " + candidates.Count + ".");
                alignments.Add(LockAlignment(doc, plan, candidates[0].Reference, line.Reference, "radial profile"));
            }
            if (alignments.Count != 4) throw new InvalidOperationException("Expected four native profile alignments; found " + alignments.Count + ".");
            return alignments;
        }

        private static Dimension LockAlignment(Document doc, ViewPlan plan, Reference first, Reference second, string purpose)
        {
            var alignment = doc.FamilyCreate.NewAlignment(plan, first, second);
            if (alignment == null) throw new InvalidOperationException("NewAlignment returned null for " + purpose + ".");
            if (alignment.NumberOfSegments > 1) throw new InvalidOperationException("A multi-segment alignment cannot be safely locked for " + purpose + ".");
            try { if (!alignment.IsLocked) alignment.IsLocked = true; }
            catch (Exception ex) { throw new InvalidOperationException("Failed to lock " + purpose + ": " + ex.Message, ex); }
            if (!alignment.IsLocked) throw new InvalidOperationException("Alignment was not locked for " + purpose + ".");
            return alignment;
        }

        private static bool SameParameter(FamilyParameter actual, FamilyParameter expected) => actual != null && expected != null && RevitCompat.GetId(actual.Id) == RevitCompat.GetId(expected.Id) && actual.Definition.Name == expected.Definition.Name;
        private static FamilyParameter SafeFamilyLabel(Dimension dimension) { try { return dimension.FamilyLabel; } catch { return null; } }
        private static bool SafeIsLocked(Dimension dimension) { try { return dimension.IsLocked; } catch { return false; } }

        private static T[] LiveCurves<T>(Document doc, ElementId[] ids, string purpose) where T : Curve
        {
            var curves = ids.Select(id => (doc.GetElement(id) as ModelCurve)?.GeometryCurve as T).ToArray();
            if (curves.Any(c => c == null)) throw new InvalidOperationException("A native " + purpose + " disappeared before read-back.");
            return curves;
        }

        private static Dimension[] LiveDimensions(IReadOnlyList<Dimension> dimensions, ElementId[] ids, string purpose)
        {
            var found = ids.Select(id => dimensions.SingleOrDefault(d => RevitCompat.GetId(d.Id) == RevitCompat.GetId(id))).ToArray();
            if (found.Any(d => d == null)) throw new InvalidOperationException("A native " + purpose + " dimension disappeared before read-back.");
            return found;
        }

        private static object ReadBack(Document doc, FamilyManager manager, IReadOnlyDictionary<string, FamilyParameter> parameters,
            Extrusion extrusion, string familyName, string typeName, double[] requested,
            ElementId[] arcIds, ElementId[] radialIds, IReadOnlyList<Dimension> profileAlignments)
        {
            var dimensions = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().ToList();
            var referenceArcs = LiveCurves<Arc>(doc, arcIds, "reference arc");
            var radialReferences = LiveCurves<Line>(doc, radialIds, "radial reference");
            var alignments = LiveDimensions(dimensions, profileAlignments.Select(d => d.Id).ToArray(), "profile alignment");
            var associated = new[] { "OuterRadius", "InnerRadius", "SegmentAngle" }.Select(n => dimensions.Count(d => SameParameter(SafeFamilyLabel(d), parameters[n])) == 1).ToArray();
            if (associated.Any(a => !a)) throw new InvalidOperationException("A native radius or angle dimension is not associated with its derived family parameter.");
            if (parameters["OuterRadius"].Formula != "OuterDiameter / 2"
                || parameters["InnerRadius"].Formula != "OuterDiameter / 2 - Thickness")
                throw new InvalidOperationException("The native derived-radius formulas no longer match OuterDiameter and Thickness.");
            var diameterDriven = associated[0] && parameters["OuterRadius"].Formula == "OuterDiameter / 2";
            var thicknessDriven = diameterDriven && associated[1] && parameters["InnerRadius"].Formula == "OuterDiameter / 2 - Thickness";
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            var widthAssociated = end != null && SameParameter(manager.GetAssociatedFamilyParameter(end), parameters["RingWidth"]);
            if (!widthAssociated) throw new InvalidOperationException("RingWidth is not associated with the extrusion end parameter.");
            var read = Names.Select((name, i) =>
            {
                var parameter = parameters[name]; var value = manager.CurrentType.AsDouble(parameter);
                if (value == null) throw new InvalidOperationException("Family parameter " + name + " has no numeric value.");
                var isAngle = name == "SegmentAngle";
                var actual = isAngle ? SegmentFamilyInput.DegreesFromRadians(value.Value) : SegmentFamilyInput.MillimetersFromFeet(value.Value);
                if (Math.Abs(actual - requested[i]) > 0.01) throw new InvalidOperationException("Family parameter " + name + " differs from its requested value.");
                return new
                {
                    name, scope = "type", spec = isAngle ? "Angle" : "Length",
                    value = Math.Round(actual, 6, MidpointRounding.AwayFromZero),
                    value_mm = isAngle ? (double?)null : Math.Round(actual, 6, MidpointRounding.AwayFromZero),
                    value_deg = isAngle ? Math.Round(actual, 6, MidpointRounding.AwayFromZero) : (double?)null,
                    unit = isAngle ? "deg" : "mm",
                    associated = isAngle ? associated[2] : name == "RingWidth" ? widthAssociated : name == "OuterDiameter" ? diameterDriven : thicknessDriven,
                    driving_path = isAngle ? "SegmentAngle -> angular FamilyLabel" : name == "RingWidth" ? "RingWidth -> EXTRUSION_END_PARAM" : name == "OuterDiameter" ? "OuterDiameter -> OuterRadius formula -> radial FamilyLabel" : "Thickness -> InnerRadius formula -> radial FamilyLabel"
                };
            }).ToArray();
            // Derived labels prove the actual native driving path for diameter and thickness.
            var outerValue = manager.CurrentType.AsDouble(parameters["OuterRadius"]); var innerValue = manager.CurrentType.AsDouble(parameters["InnerRadius"]);
            if (outerValue == null || innerValue == null || Math.Abs((outerValue.Value - innerValue.Value) * 304.8 - requested[1]) > 0.01)
                throw new InvalidOperationException("OuterRadius/InnerRadius formulas do not produce the requested thickness.");
            var solids = extrusion.get_Geometry(new Options())?.OfType<Solid>().Where(s => s.IsElementGeometry && s.Volume > 0).ToArray();
            if (solids == null || solids.Length == 0) throw new InvalidOperationException("The regenerated extrusion has no positive-volume solid in its own element geometry.");
            var box = extrusion.get_BoundingBox(null);
            if (box == null) throw new InvalidOperationException("The extrusion has no geometry bounding box after regeneration.");
            var actual = new[] { box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z }.Select(SegmentFamilyInput.MillimetersFromFeet).ToArray();
            var expected = SegmentFamilyInput.ExpectedBoundingBoxMm(requested[0], requested[1], requested[2], requested[3]);
            // Precise geometry proof: total solid volume matches the exact annular-sector volume.
            // Solid.Volume is exact (cubic feet) and, unlike the bounding box, is not affected by
            // Revit's chordal discretisation of arcs (which under-reports the arc-apex X extent).
            var volumeMm3 = solids.Sum(s => s.Volume) * Math.Pow(304.8, 3);
            var expectedVolumeMm3 = SegmentFamilyInput.ExpectedVolumeMm3(requested[0], requested[1], requested[2], requested[3]);
            var volumeMatches = Math.Abs(volumeMm3 - expectedVolumeMm3) <= expectedVolumeMm3 * 0.005; // 0.5% — absorbs feet rounding on a ~5m³ segment
            if (!volumeMatches) throw new InvalidOperationException(
                $"Regenerated arc extrusion volume does not match the analytic annular-sector volume within 0.5%. actual={volumeMm3:F0} mm³, expected={expectedVolumeMm3:F0} mm³.");
            // Bounding box: Y (chord) and Z (ring width) use exact endpoints and match to 1mm.
            // X spans the arc apex, which Revit's bbox under-reports by its chord discretisation, so
            // X is only sanity-checked to a chord-aware tolerance rather than an exact match.
            const double flatToleranceMm = 1.0;
            var arcApexAllowanceMm = requested[0] * 0.5 * 0.0003 + 1.0; // ~R*(1-cos(seg)) for the default ~2.4° arc segment
            var matches = new[]
            {
                Math.Abs(actual[0] - expected[0]) <= arcApexAllowanceMm,
                Math.Abs(actual[1] - expected[1]) <= flatToleranceMm,
                Math.Abs(actual[2] - expected[2]) <= flatToleranceMm
            };
            if (matches.Any(m => !m)) throw new InvalidOperationException(
                "Regenerated arc extrusion bounding box is inconsistent with the annular-sector geometry. "
                + $"actual_mm=[{actual[0]:F3}, {actual[1]:F3}, {actual[2]:F3}] (Min=({box.Min.X:F4},{box.Min.Y:F4},{box.Min.Z:F4}) Max=({box.Max.X:F4},{box.Max.Y:F4},{box.Max.Z:F4})) "
                + $"expected_mm=[{expected[0]:F3}, {expected[1]:F3}, {expected[2]:F3}].");
            return new
            {
                family_name = familyName, document_title = doc.Title, family_type = typeName, native_editable = true, geometry_kind = "arc_extrusion", parameters = read,
                geometry = new { extrusion_id = RevitCompat.GetId(extrusion.Id).ToString(CultureInfo.InvariantCulture), solid_count = solids.Length, reference_arc_count = referenceArcs.Length,
                    radial_reference_count = radialReferences.Length, origin_dimension_count = 0, centre_constraint = "pending_hosted_plan_a_flex", labelled_dimension_count = associated.Count(a => a), profile_alignment_count = alignments.Length,
                    locked_alignment_count = alignments.Count(SafeIsLocked), bounding_box_mm = new { x = actual[0], y = actual[1], z = actual[2] },
                    expected_bounding_box_mm = new { x = expected[0], y = expected[1], z = expected[2] }, dimensions_match = matches.All(m => m),
                    volume_mm3 = Math.Round(volumeMm3, 0), expected_volume_mm3 = Math.Round(expectedVolumeMm3, 0), volume_matches = volumeMatches }
            };
        }
#endif
    }
}
