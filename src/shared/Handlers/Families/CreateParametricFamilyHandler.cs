#if REVIT2027
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public string ParametersSchema => @"{
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

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
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
            var saved = false;
            var closeError = (string)null;
            CommandResult outcome;
            try
            {
                // NewFamilyDocument must be called by the add-in on Revit's UI thread.
                familyDoc = app.Application.NewFamilyDocument(templatePath);
                if (familyDoc == null || !familyDoc.IsFamilyDocument)
                    throw new InvalidOperationException("Revit did not open the supplied template as a family document.");

                var dimensions = new[] { lengthMm, widthMm, heightMm };
                object familyData;
                using (var transaction = new Transaction(familyDoc, "RvtMcp: create parameterized extrusion"))
                {
                    if (transaction.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Could not start the family creation transaction.");
                    try
                    {
                        var manager = familyDoc.FamilyManager;
                        var parameters = CreateParameters(manager);
                        var familyType = manager.NewType(typeName);
                        if (familyType == null) throw new InvalidOperationException("Revit could not create the requested family type.");
                        manager.CurrentType = familyType;
                        SetValues(manager, parameters, dimensions);

                        // Ref. Level is the native XY work plane in a generic-model family template.
                        var level = new FilteredElementCollector(familyDoc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                        if (level == null) throw new InvalidOperationException("The family template has no reference level for the extrusion sketch.");
                        var plane = CreateWorkPlane(familyDoc, level);
                        var extrusion = CreateExtrusion(familyDoc, plane, dimensions);
                        if (extrusion == null) throw new InvalidOperationException("Revit did not create the extrusion.");

                        var constraints = AssociateExtrusionParameters(familyDoc, manager, parameters, extrusion, dimensions);
                        familyDoc.Regenerate();
                        familyData = ReadBack(familyDoc, manager, parameters, extrusion, constraints, familyName, typeName, dimensions);
                        if (transaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit did not commit the family creation transaction.");
                    }
                    catch
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                        throw;
                    }
                }

                if (File.Exists(outputPath)) // A second guard immediately before the native save.
                    throw new IOException("output_path already exists. This tool never overwrites an existing .rfa file.");
                // Save only a uniquely named temporary .rfa. The final same-volume rename is
                // atomic and non-overwriting on Windows; failures can safely delete our own temp file.
                familyDoc.SaveAs(temporaryPath, new SaveAsOptions { OverwriteExistingFile = false });
                if (!File.Exists(temporaryPath)) throw new IOException("Revit did not create the temporary family file.");
                File.Move(temporaryPath, outputPath);
                saved = true;
                var result = JObject.FromObject(familyData);
                result["created"] = true;
                result["saved"] = true;
                result["output_path"] = outputPath;
                result["file_size_bytes"] = new FileInfo(outputPath).Length;
                result["host_verification"] = "in_revit_2027";
                outcome = CommandResult.Ok(result);
                familyDoc.Close(false);
                familyDoc = null;
            }
            catch (Exception ex)
            {
                if (saved)
                {
                    // Native SaveAs succeeded but a later read failed: do not misreport it as no file.
                    outcome = CommandResult.Fail("The family file was saved, but its response could not be completed. Inspect output_path before retrying: " + ex.Message);
                }
                else if (File.Exists(temporaryPath))
                {
                    // Native SaveAs succeeded but the final non-overwriting rename did not.
                    // Close first: Windows can retain a document handle on the temporary file.
                    try { familyDoc.Close(false); }
                    catch (Exception closeException)
                    {
                        return CommandResult.Fail("Family creation failed and the family document could not be closed. "
                            + "The temporary file may remain at " + temporaryPath + "; the requested output was not written. " + closeException.Message);
                    }
                    familyDoc = null;
                    var cleanupError = ParametricFamilyInput.DeleteTemporaryFile(temporaryPath);
                    outcome = CommandResult.Fail("Family creation failed; the family document was closed without saving to output_path. "
                        + (cleanupError == null ? "No output file was left behind. " : cleanupError + " ") + ex.Message);
                }
                else
                {
                    outcome = CommandResult.Fail("Family creation failed; the family document was closed without saving to output_path. " + ex.Message);
                }
            }
            if (familyDoc != null)
            {
                try { familyDoc.Close(false); }
                catch (Exception closeException) { closeError = closeException.Message; }
            }
            if (closeError != null)
                return CommandResult.Fail((saved
                    ? "The family was saved, but the temporary family document could not be closed. Inspect Revit and output_path before retrying. "
                    : "Family creation failed; the family document could not be closed without saving to output_path. Inspect output_path and the reported temporary file, if any. ")
                    + closeError);
            return outcome;
        }

        private static Dictionary<string, FamilyParameter> CreateParameters(FamilyManager manager)
        {
            var names = new[] { "Length", "Width", "Height" };
            var result = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++)
            {
                var parameter = manager.AddParameter(names[i], GroupTypeId.Geometry, SpecTypeId.Length, false);
                if (parameter == null) throw new InvalidOperationException("Could not add family parameter " + names[i] + ".");
                if (manager.CurrentType.AsDouble(parameter) == null)
                    throw new InvalidOperationException("Family parameter " + names[i] + " did not expose a numeric length value.");
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

        private static SketchPlane CreateWorkPlane(Document doc, Level level)
        {
            var sketchPlane = SketchPlane.Create(doc, level.Id);
            if (sketchPlane == null) throw new InvalidOperationException("Could not create the extrusion work plane on the reference level.");
            return sketchPlane;
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
            internal readonly List<Dimension> Alignments = new List<Dimension>();
        }

        private static ConstraintSet AssociateExtrusionParameters(Document doc, FamilyManager manager,
            IReadOnlyDictionary<string, FamilyParameter> parameters, Extrusion extrusion, double[] dimensions)
        {
            var end = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            if (end == null || end.IsReadOnly) throw new InvalidOperationException("The extrusion end parameter is unavailable or read-only.");
            manager.AssociateElementParameterToFamilyParameter(end, parameters["Height"]);

            // Align each profile edge to a named reference plane, then lock the alignment.
            // The dimensions labelled Length/Width constrain the opposing reference planes.
            var views = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan).ToArray();
            var plan = views.FirstOrDefault() ?? throw new InvalidOperationException("The family template has no floor-plan view for dimensional constraints.");
            var constraints = CreateNamedPlanes(doc, plan, parameters, dimensions);
            constraints.Alignments.AddRange(CreateExtrusionProfileConstraints(doc, plan, extrusion));
            return constraints;
        }

        private static ConstraintSet CreateNamedPlanes(Document doc, ViewPlan plan,
            IReadOnlyDictionary<string, FamilyParameter> parameters, double[] dimensions)
        {
            var factory = doc.FamilyCreate;
            var halfLength = ParametricFamilyInput.ToRevitFeet(dimensions[0] * 0.5);
            var halfWidth = ParametricFamilyInput.ToRevitFeet(dimensions[1] * 0.5);
            var right = factory.NewReferencePlane(new XYZ(halfLength, -2, 0), new XYZ(halfLength, 2, 0), XYZ.BasisZ, plan);
            right.Name = "RvtMcp Right";
            var left = factory.NewReferencePlane(new XYZ(-halfLength, -2, 0), new XYZ(-halfLength, 2, 0), XYZ.BasisZ, plan);
            left.Name = "RvtMcp Left";
            var dimension = factory.NewDimension(plan, Line.CreateBound(new XYZ(-halfLength, 0, 0), new XYZ(halfLength, 0, 0)), RefArray(left, right));
            dimension.FamilyLabel = parameters["Length"];
            var front = factory.NewReferencePlane(new XYZ(-2, halfWidth, 0), new XYZ(2, halfWidth, 0), XYZ.BasisZ, plan);
            front.Name = "RvtMcp Front";
            var back = factory.NewReferencePlane(new XYZ(-2, -halfWidth, 0), new XYZ(2, -halfWidth, 0), XYZ.BasisZ, plan);
            back.Name = "RvtMcp Back";
            var width = factory.NewDimension(plan, Line.CreateBound(new XYZ(0, -halfWidth, 0), new XYZ(0, halfWidth, 0)), RefArray(back, front));
            width.FamilyLabel = parameters["Width"];
            var constraints = new ConstraintSet();
            constraints.Dimensions.Add(dimension);
            constraints.Dimensions.Add(width);
            return constraints;
        }

        private static ReferenceArray RefArray(ReferencePlane first, ReferencePlane second)
        {
            var result = new ReferenceArray();
            result.Append(first.GetReference());
            result.Append(second.GetReference());
            return result;
        }

        private static List<Dimension> CreateExtrusionProfileConstraints(Document doc, ViewPlan plan, Extrusion extrusion)
        {
            // Native profile curve references are used rather than coincident XYZ coordinates.
            if (extrusion.Sketch == null) throw new InvalidOperationException("The native extrusion did not expose its editable sketch.");
            var edges = extrusion.Sketch.Profile.Cast<CurveArray>().SelectMany(loop => loop.Cast<Curve>())
                .OfType<Line>().Where(line => line.Reference != null).ToArray();
            if (edges.Length != 4) throw new InvalidOperationException("The native extrusion profile must contain exactly four line references.");
            var planes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToArray();
            var alignments = new List<Dimension>();
            foreach (var edge in edges)
            {
                var direction = edge.Direction;
                var vertical = Math.Abs(direction.Y) > 0.9;
                var midpoint = edge.GetEndPoint(0).Add(edge.GetEndPoint(1)).Multiply(0.5);
                var name = vertical ? (midpoint.X > 0 ? "RvtMcp Right" : "RvtMcp Left")
                    : (midpoint.Y > 0 ? "RvtMcp Front" : "RvtMcp Back");
                var plane = planes.First(p => p.Name == name);
                var alignment = doc.FamilyCreate.NewAlignment(plan, plane.GetReference(), edge.Reference);
                if (alignment == null) throw new InvalidOperationException("Could not align the extrusion profile to " + name + ".");
                if (!alignment.IsLocked) alignment.IsLocked = true;
                alignments.Add(alignment);
            }
            return alignments;
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
                constraints.Dimensions.SingleOrDefault(d => d.FamilyLabel != null && d.FamilyLabel.Id == parameters["Length"].Id) != null,
                constraints.Dimensions.SingleOrDefault(d => d.FamilyLabel != null && d.FamilyLabel.Id == parameters["Width"].Id) != null,
                end != null && parameters["Height"].AssociatedParameters.Cast<Parameter>().Any(p => ReferenceEquals(p, end))
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
            if (constraints.Dimensions.Count != 2 || constraints.Alignments.Count != 4 || constraints.Alignments.Any(a => !a.IsLocked))
                throw new InvalidOperationException("Native constraint read-back did not find two labelled dimensions and four locked alignments.");
            var solidCount = extrusion.get_Geometry(new Options()).OfType<Solid>().Count(s => s.Volume > 0);
            if (solidCount == 0) throw new InvalidOperationException("The regenerated extrusion has no positive-volume solid.");
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
                    locked_alignment_count = constraints.Alignments.Count(a => a.IsLocked),
                    bounding_box_mm = new { length = actual[0], width = actual[1], height = actual[2] },
                    dimensions_match = matches.All(m => m)
                }
            };
        }
    }
}
#endif
