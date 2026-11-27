using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Revit 2027 read-only probe: inspect an Extrusion's native sketch ModelCurve references,
    /// reference-plane candidates, and the exact geometry information needed to build stable
    /// driving dimensions. Returns stable reference strings, directions, and view geometry data.
    /// </summary>
    public sealed class ProbeExtrusionSketchHandler : IRevitCommand
    {
        public string Name => "probe_extrusion_sketch";
        public string Description => "Revit 2027: inspect an Extrusion's native sketch references and geometry for parametric dimension design. Read-only (rollback mode) or commit-then-discard (commit_probe mode).";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""model_path""],
  ""properties"": {
    ""model_path"": {""type"": ""string"", ""description"": ""Absolute path of the .rfa to inspect.""},
    ""extrusion_id"": {""type"": ""integer"", ""description"": ""Optional ElementId of the Extrusion; otherwise the first Extrusion is used.""},
    ""create_reference_plane"": {""type"": ""boolean"", ""default"": false},
    ""create_dimension"": {""type"": ""boolean"", ""default"": false},
    ""commit_probe"": {""type"": ""boolean"", ""default"": false, ""description"": ""When true, each dimension attempt commits in its own transaction, reads back state, then the doc is closed without saving. Use to find which reference combinations survive Commit().""}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var modelPath = request.Value<string>("model_path");
            var extrusionIdValue = request.Value<long?>("extrusion_id");
            var createReferencePlane = request.Value<bool?>("create_reference_plane") ?? false;
            var createDimension = request.Value<bool?>("create_dimension") ?? false;
            var commitProbe = request.Value<bool?>("commit_probe") ?? false;
            if (string.IsNullOrWhiteSpace(modelPath))
                return CommandResult.Fail("model_path is required.");
            if (!System.IO.File.Exists(modelPath))
                return CommandResult.Fail("model_path does not exist: " + modelPath);

            Document doc = null;
            using (McpDocument.Begin(app))
            {
                try
                {
                    doc = McpDocument.TrackOpened(app.Application.OpenDocumentFile(modelPath));
                    if (doc == null || !doc.IsFamilyDocument)
                        return CommandResult.Fail("model_path did not open as a family document.");
                    var result = new JObject();
                    result["title"] = doc.Title;
                    result["family_manager"] = doc.FamilyManager != null;
                    if (doc.FamilyManager != null)
                    {
                        var parameters = new JArray();
                        foreach (FamilyParameter p in doc.FamilyManager.GetParameters())
                        {
                            parameters.Add(new JObject
                            {
                                ["name"] = p.Definition.Name,
                                ["id"] = RevitCompat.GetId(p.Id),
                                ["formula"] = p.Formula,
                                ["is_reporting"] = p.IsReporting,
                                ["is_instance"] = p.IsInstance,
                                ["associated_count"] = p.AssociatedParameters.Size
                            });
                        }
                        result["parameters"] = parameters;
                    }
                    var extrusion = extrusionIdValue.HasValue
                        ? doc.GetElement(RevitCompat.ToElementId(extrusionIdValue.Value)) as Extrusion
                        : new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
                    if (extrusion == null)
                        return CommandResult.Fail("No Extrusion found in " + modelPath);
                    result["extrusion_id"] = RevitCompat.GetId(extrusion.Id);
                    result["extrusion_height"] = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM)?.AsDouble();
                    var sketch = extrusion.Sketch;
                    if (sketch == null) return CommandResult.Fail("Extrusion has no Sketch.");
                    var sketchPlane = sketch.SketchPlane;
                    result["sketch_plane_id"] = sketchPlane != null ? RevitCompat.GetId(sketchPlane.Id) : -1;
                    if (sketchPlane != null)
                    {
                        var plane = sketchPlane.GetPlane();
                        result["sketch_plane_origin"] = Point(plane.Origin);
                        result["sketch_plane_normal"] = Point(plane.Normal);
                        result["sketch_plane_x"] = Point(plane.XVec);
                        result["sketch_plane_y"] = Point(plane.YVec);
                    }
                    var curves = new JArray();
                    foreach (var id in sketch.GetAllElements())
                    {
                        var curveElement = doc.GetElement(id) as ModelCurve;
                        if (curveElement == null) continue;
                        var curveInfo = new JObject();
                        curveInfo["element_id"] = RevitCompat.GetId(curveElement.Id);
                        curveInfo["geometry_curve_type"] = curveElement.GeometryCurve?.GetType().FullName;
                        var reference = curveElement.GeometryCurve?.Reference;
                        if (reference != null)
                        {
                            curveInfo["reference_element_id"] = reference.ElementId != null ? RevitCompat.GetId(reference.ElementId) : -1;
                            curveInfo["reference_stable"] = SafeStableRepresentation(reference, doc);
                        }
                        if (curveElement.GeometryCurve is Line line)
                        {
                            curveInfo["start"] = Point(line.GetEndPoint(0));
                            curveInfo["end"] = Point(line.GetEndPoint(1));
                            curveInfo["direction"] = Point(line.Direction);
                            curveInfo["length_ft"] = line.Length;
                        }
                        curves.Add(curveInfo);
                    }
                    result["sketch_curves"] = curves;
                    var planes = new JArray();
                    foreach (var plane in new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                    {
                        var planeInfo = new JObject();
                        planeInfo["id"] = RevitCompat.GetId(plane.Id);
                        planeInfo["name"] = plane.Name;
                        var p = plane.GetPlane();
                        planeInfo["origin"] = Point(p.Origin);
                        planeInfo["normal"] = Point(p.Normal);
                        planeInfo["direction"] = Point(plane.Direction);
                        var reference = plane.GetReference();
                        if (reference != null)
                        {
                            planeInfo["reference_stable"] = SafeStableRepresentation(reference, doc);
                        }
                        planeInfo["geometry_faces"] = ReadFaces(doc, plane, null);
                        planes.Add(planeInfo);
                    }
                    result["reference_planes"] = planes;
                    var views = new JArray();
                    foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate))
                    {
                        views.Add(new JObject
                        {
                            ["id"] = RevitCompat.GetId(view.Id),
                            ["name"] = view.Name,
                            ["type"] = view.ViewType.ToString(),
                            ["level_id"] = view.GenLevel != null ? RevitCompat.GetId(view.GenLevel.Id) : -1,
                            ["level_elevation"] = view.GenLevel?.Elevation,
                            ["faces_left_plane"] = ReadFaces(doc, FindPlane(doc, "RvtMcp Left"), view),
                            ["faces_center_lr"] = ReadFaces(doc, FindPlane(doc, "Center (Left/Right)"), view)
                        });
                    }
                    result["view_plans"] = views;

                    if (commitProbe)
                    {
                        result["commit_probes"] = RunCommitProbes(doc, extrusion, modelPath);
                    }
                    else if (createReferencePlane || createDimension)
                    {
                        using (var transaction = new Transaction(doc, "RvtMcp probe: create reference plane / dimension"))
                        {
                            transaction.Start();
                            try
                            {
                                var plan = FindZeroLevelFloorPlan(doc);
                                if (plan == null) throw new InvalidOperationException("No zero-level floor plan.");
                                doc.Regenerate();
                                var attempts = new JArray();
                                if (createReferencePlane || createDimension)
                                {
                                    EnsurePlane(doc, plan, "RvtMcp Left", new XYZ(-2, -2, 0), new XYZ(-2, 2, 0));
                                    EnsurePlane(doc, plan, "RvtMcp Right", new XYZ(2, -2, 0), new XYZ(2, 2, 0));
                                    doc.Regenerate();
                                }
                                if (createDimension)
                                {
                                    var left = FindPlane(doc, "RvtMcp Left");
                                    var right = FindPlane(doc, "RvtMcp Right");
                                    var center = FindPlane(doc, "Center (Left/Right)");
                                    attempts.Add(TryDimension(doc, plan, left, right, new XYZ(-2, 0, 0), new XYZ(2, 0, 0), "two-reference length"));
                                    attempts.Add(TryDimension(doc, plan, left, center, new XYZ(-2, 0, 0), new XYZ(0, 0, 0), "two-reference left-center"));
                                    attempts.Add(TryDimension(doc, plan, center, right, new XYZ(0, 0, 0), new XYZ(2, 0, 0), "two-reference center-right"));
                                    attempts.Add(TryThreeReferenceDimension(doc, plan, left, center, right));
                                }
                                result["dimension_attempts"] = attempts;
                                transaction.RollBack();
                            }
                            catch
                            {
                                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                                throw;
                            }
                        }
                    }
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Probe failed: " + ex);
                }
                finally
                {
                    if (doc != null) McpDocument.Close(doc, false);
                }
            }
        }

        // ── Commit-probe: each attempt commits independently, then reads back ──

        private static JArray RunCommitProbes(Document doc, Extrusion extrusion, string modelPath)
        {
            var probes = new JArray();
            var plan = FindZeroLevelFloorPlan(doc);
            if (plan == null)
            {
                probes.Add(new JObject { ["error"] = "No zero-level floor plan." });
                return probes;
            }

            // Probe 1: two-reference length dimension (left ↔ right)
            probes.Add(CommitProbeAttempt(doc, plan, "two-reference length", (d, p) =>
            {
                EnsurePlane(d, p, "RvtMcp Left", new XYZ(-2, -2, 0), new XYZ(-2, 2, 0));
                EnsurePlane(d, p, "RvtMcp Right", new XYZ(2, -2, 0), new XYZ(2, 2, 0));
                d.Regenerate();
                var left = FindPlane(d, "RvtMcp Left");
                var right = FindPlane(d, "RvtMcp Right");
                return CreateDimensionAndLabel(d, p, left, right, new XYZ(-2, 0, 0), new XYZ(2, 0, 0), "Length");
            }));

            // Probe 2: two-reference left↔center
            probes.Add(CommitProbeAttempt(doc, plan, "two-reference left-center", (d, p) =>
            {
                EnsurePlane(d, p, "RvtMcp Left", new XYZ(-2, -2, 0), new XYZ(-2, 2, 0));
                d.Regenerate();
                var left = FindPlane(d, "RvtMcp Left");
                var center = FindPlane(d, "Center (Left/Right)");
                return CreateDimensionAndLabel(d, p, left, center, new XYZ(-2, 0, 0), new XYZ(0, 0, 0), "Length");
            }));

            // Probe 3: two-reference center↔right
            probes.Add(CommitProbeAttempt(doc, plan, "two-reference center-right", (d, p) =>
            {
                EnsurePlane(d, p, "RvtMcp Right", new XYZ(2, -2, 0), new XYZ(2, 2, 0));
                d.Regenerate();
                var right = FindPlane(d, "RvtMcp Right");
                var center = FindPlane(d, "Center (Left/Right)");
                return CreateDimensionAndLabel(d, p, center, right, new XYZ(0, 0, 0), new XYZ(2, 0, 0), "Length");
            }));

            // Probe 4: three-reference EQ
            probes.Add(CommitProbeAttempt(doc, plan, "three-reference EQ", (d, p) =>
            {
                EnsurePlane(d, p, "RvtMcp Left", new XYZ(-2, -2, 0), new XYZ(-2, 2, 0));
                EnsurePlane(d, p, "RvtMcp Right", new XYZ(2, -2, 0), new XYZ(2, 2, 0));
                d.Regenerate();
                var left = FindPlane(d, "RvtMcp Left");
                var right = FindPlane(d, "RvtMcp Right");
                var center = FindPlane(d, "Center (Left/Right)");
                return CreateThreeReferenceEQAndLabel(d, p, left, center, right, "Length");
            }));

            // Probe 5: dimension directly on sketch ModelCurve references
            probes.Add(CommitProbeAttempt(doc, plan, "sketch-curve two-reference", (d, p) =>
            {
                var extr = new FilteredElementCollector(d).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
                if (extr?.Sketch == null) return new JObject { ["error"] = "No extrusion sketch" };
                var modelCurves = extr.Sketch.GetAllElements()
                    .Select(id => d.GetElement(id) as ModelCurve)
                    .Where(mc => mc?.GeometryCurve is Line)
                    .Select(mc => new { Curve = mc, Line = (Line)mc.GeometryCurve })
                    .ToList();
                // Find leftmost and rightmost vertical lines by X coordinate
                var verticalLines = modelCurves
                    .Where(mc => Math.Abs(mc.Line.Direction.Y) > 0.99) // vertical in plan
                    .OrderBy(mc => mc.Line.GetEndPoint(0).X)
                    .ToList();
                if (verticalLines.Count < 2)
                    return new JObject { ["error"] = "Fewer than 2 vertical sketch lines found" };
                var leftCurve = verticalLines.First().Curve;
                var rightCurve = verticalLines.Last().Curve;
                if (leftCurve.GeometryCurve?.Reference == null || rightCurve.GeometryCurve?.Reference == null)
                    return new JObject { ["error"] = "Sketch curve references not available" };
                var leftX = verticalLines.First().Line.GetEndPoint(0).X;
                var rightX = verticalLines.Last().Line.GetEndPoint(0).X;
                var refs = new ReferenceArray();
                refs.Append(leftCurve.GeometryCurve.Reference);
                refs.Append(rightCurve.GeometryCurve.Reference);
                var dim = d.FamilyCreate.NewDimension(p, Line.CreateBound(new XYZ(leftX, 0, 0), new XYZ(rightX, 0, 0)), refs);
                var result = new JObject
                {
                    ["created_id"] = RevitCompat.GetId(dim.Id),
                    ["reference_count"] = dim.References.Size
                };
                TryLabelDimension(d, dim, "Length", result);
                return result;
            }));

            // Probe 6: planes + dimension + alignment lock + parameter flex test
            probes.Add(CommitProbeAlignmentFlex(doc, plan, "alignment-lock two-ref", useEq: false));
            probes.Add(CommitProbeAlignmentFlex(doc, plan, "alignment-lock EQ", useEq: true));

            return probes;
        }

        private static JObject CommitProbeAttempt(Document doc, ViewPlan plan, string label,
            Func<Document, ViewPlan, JObject> attemptFunc)
        {
            var result = new JObject { ["probe"] = label };
            using (var transaction = new Transaction(doc, "RvtMcp commit-probe: " + label))
            {
                transaction.Start();
                try
                {
                    var attemptResult = attemptFunc(doc, plan);
                    result["attempt"] = attemptResult;

                    var commitStatus = transaction.Commit();
                    result["commit_status"] = commitStatus.ToString();

                    // Read back — no Regenerate outside transaction; read-only access is fine.
                    result["post_commit_extrusion_valid"] = new FilteredElementCollector(doc)
                        .OfClass(typeof(Extrusion)).Cast<Extrusion>().Any();

                    var postExtr = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
                    if (postExtr?.Sketch != null)
                    {
                        var bbox = postExtr.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            result["post_commit_bbox_min"] = Point(bbox.Min);
                            result["post_commit_bbox_max"] = Point(bbox.Max);
                        }
                    }

                    var dims = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().ToList();
                    var dimInfos = new JArray();
                    foreach (var dim in dims)
                    {
                        var di = new JObject
                        {
                            ["id"] = RevitCompat.GetId(dim.Id),
                            ["value"] = dim.Value,
                            ["references_count"] = dim.References.Size
                        };
                        var label_param = dim.FamilyLabel;
                        di["family_label"] = label_param?.Definition?.Name;
                        dimInfos.Add(di);
                    }
                    result["post_commit_dimensions"] = dimInfos;
                }
                catch (Exception ex)
                {
                    result["error"] = ex.Message;
                    result["error_type"] = ex.GetType().FullName;
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();
                }
            }
            return result;
        }

        // ── Probe 6: full pipeline with alignment lock + flex ──

        private static JObject CommitProbeAlignmentFlex(Document doc, ViewPlan plan, string label, bool useEq)
        {
            var result = new JObject { ["probe"] = label };
            var manager = doc.FamilyManager;
            if (manager == null) { result["error"] = "No FamilyManager"; return result; }

            // Step 1: commit planes + dimension + alignment
            using (var transaction = new Transaction(doc, "RvtMcp alignment-probe: " + label))
            {
                transaction.Start();
                try
                {
                    EnsurePlane(doc, plan, "RvtMcp Left", new XYZ(-2, -2, 0), new XYZ(-2, 2, 0));
                    EnsurePlane(doc, plan, "RvtMcp Right", new XYZ(2, -2, 0), new XYZ(2, 2, 0));
                    doc.Regenerate();

                    var left = FindPlane(doc, "RvtMcp Left");
                    var right = FindPlane(doc, "RvtMcp Right");
                    var center = FindPlane(doc, "Center (Left/Right)");

                    // Create dimension
                    Dimension dim;
                    if (useEq)
                    {
                        var refs = new ReferenceArray();
                        refs.Append(left.GetReference());
                        refs.Append(center.GetReference());
                        refs.Append(right.GetReference());
                        dim = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(new XYZ(-2, 0, 0), new XYZ(2, 0, 0)), refs);
                        dim.AreSegmentsEqual = true;
                    }
                    else
                    {
                        var refs = new ReferenceArray();
                        refs.Append(left.GetReference());
                        refs.Append(right.GetReference());
                        dim = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(new XYZ(-2, 0, 0), new XYZ(2, 0, 0)), refs);
                    }
                    result["dimension_id"] = RevitCompat.GetId(dim.Id);
                    TryLabelDimension(doc, dim, "Length", result);

                    // Alignment lock: align sketch lines to reference planes
                    var extr = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
                    if (extr?.Sketch == null) throw new InvalidOperationException("No extrusion sketch for alignment");

                    var sketchCurves = extr.Sketch.GetAllElements()
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

                    if (verticalLines.Count < 2) throw new InvalidOperationException("Fewer than 2 vertical sketch lines");
                    if (horizontalLines.Count < 2) throw new InvalidOperationException("Fewer than 2 horizontal sketch lines");

                    // Align left vertical sketch line to left plane
                    var leftSketchLine = verticalLines.First();
                    var rightSketchLine = verticalLines.Last();

                    doc.Regenerate();
                    var leftView = FindZeroLevelFloorPlan(doc);
                    var alignResult = new JArray();

                    // Try NewAlignment: sketch curve → reference plane
                    try
                    {
                        var alignment = doc.FamilyCreate.NewAlignment(
                            leftView,
                            leftSketchLine.Curve.GeometryCurve.Reference,
                            left.GetReference());
                        TrySetLocked(alignment, alignResult, "left");
                    }
                    catch (Exception ex)
                    {
                        alignResult.Add(new JObject { ["side"] = "left", ["error"] = ex.Message });
                    }

                    doc.Regenerate();

                    try
                    {
                        var alignment = doc.FamilyCreate.NewAlignment(
                            leftView,
                            rightSketchLine.Curve.GeometryCurve.Reference,
                            right.GetReference());
                        TrySetLocked(alignment, alignResult, "right");
                    }
                    catch (Exception ex)
                    {
                        alignResult.Add(new JObject { ["side"] = "right", ["error"] = ex.Message });
                    }

                    result["alignments"] = alignResult;
                    doc.Regenerate();

                    var commitStatus = transaction.Commit();
                    result["commit_status"] = commitStatus.ToString();

                    // Read back after commit (no Regenerate outside transaction)
                    var postExtr = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
                    if (postExtr != null)
                    {
                        var bbox = postExtr.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            result["post_commit_bbox_min"] = Point(bbox.Min);
                            result["post_commit_bbox_max"] = Point(bbox.Max);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result["commit_error"] = ex.Message;
                    result["commit_error_type"] = ex.GetType().FullName;
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();
                    return result; // Cannot flex if commit failed
                }
            }

            // Step 2: flex test — change Length parameter and see if geometry follows
            var lengthParam = manager.GetParameters()
                .FirstOrDefault(p => string.Equals(p.Definition.Name, "Length", StringComparison.OrdinalIgnoreCase));
            if (lengthParam == null) { result["flex_error"] = "Length parameter not found"; return result; }

            var originalLengthValue = manager.CurrentType.AsDouble(lengthParam);
            if (originalLengthValue == null) { result["flex_error"] = "Length parameter value is null"; return result; }
            var originalLength = originalLengthValue.Value;
            result["original_length_ft"] = originalLength;

            using (var flexTransaction = new Transaction(doc, "RvtMcp flex: " + label))
            {
                flexTransaction.Start();
                try
                {
                    var newLength = originalLength * 0.75; // shrink to 75%
                    manager.Set(lengthParam, newLength);
                    doc.Regenerate();

                    var flexExtr = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
                    if (flexExtr != null)
                    {
                        var bbox = flexExtr.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            result["flex_bbox_min"] = Point(bbox.Min);
                            result["flex_bbox_max"] = Point(bbox.Max);
                        }
                    }
                    result["flex_new_length_ft"] = newLength;

                    // Check sketch curve endpoints
                    if (flexExtr?.Sketch != null)
                    {
                        var flexCurves = new JArray();
                        foreach (var id in flexExtr.Sketch.GetAllElements())
                        {
                            var mc = doc.GetElement(id) as ModelCurve;
                            if (mc?.GeometryCurve is Line line)
                                flexCurves.Add(new JObject { ["start"] = Point(line.GetEndPoint(0)), ["end"] = Point(line.GetEndPoint(1)) });
                        }
                        result["flex_sketch_curves"] = flexCurves;
                    }

                    // Restore original
                    manager.Set(lengthParam, originalLength);
                    doc.Regenerate();
                    flexTransaction.Commit(); // commit the restore; we discard anyway on Close(false)
                }
                catch (Exception ex)
                {
                    result["flex_error"] = ex.Message;
                    if (flexTransaction.GetStatus() == TransactionStatus.Started)
                        flexTransaction.RollBack();
                }
            }

            return result;
        }

        // ── Dimension creation helpers ──

        private static JObject CreateDimensionAndLabel(Document doc, ViewPlan plan,
            ReferencePlane first, ReferencePlane second, XYZ start, XYZ end, string paramName)
        {
            if (first == null || second == null)
                return new JObject { ["error"] = "missing plane: " + (first == null ? "first" : "second") };

            var references = new ReferenceArray();
            references.Append(first.GetReference());
            references.Append(second.GetReference());
            var dimension = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(start, end), references);
            var result = new JObject
            {
                ["created_id"] = RevitCompat.GetId(dimension.Id),
                ["reference_count"] = dimension.References.Size,
                ["value_ft"] = dimension.Value
            };
            TryLabelDimension(doc, dimension, paramName, result);
            return result;
        }

        private static JObject CreateThreeReferenceEQAndLabel(Document doc, ViewPlan plan,
            ReferencePlane first, ReferencePlane center, ReferencePlane second, string paramName)
        {
            if (first == null || center == null || second == null)
                return new JObject { ["error"] = "missing plane" };

            var references = new ReferenceArray();
            references.Append(first.GetReference());
            references.Append(center.GetReference());
            references.Append(second.GetReference());
            var dimension = doc.FamilyCreate.NewDimension(plan,
                Line.CreateBound(new XYZ(-2, 0, 0), new XYZ(2, 0, 0)), references);
            var result = new JObject
            {
                ["created_id"] = RevitCompat.GetId(dimension.Id),
                ["reference_count"] = dimension.References.Size
            };
            dimension.AreSegmentsEqual = true;
            result["are_segments_equal"] = dimension.AreSegmentsEqual;
            TryLabelDimension(doc, dimension, paramName, result);
            return result;
        }

        private static void TrySetLocked(Dimension dimension, JArray alignResult, string side)
        {
            try
            {
                // Dimension.Locked is available on Alignment (subclass) but the compile-time type
                // is Dimension in some Revit API versions. Use reflection to set it safely.
                var lockedProp = dimension.GetType().GetProperty("Locked");
                if (lockedProp != null && lockedProp.CanWrite)
                {
                    lockedProp.SetValue(dimension, true);
                    alignResult.Add(new JObject { ["side"] = side, ["locked"] = true });
                }
                else
                {
                    alignResult.Add(new JObject { ["side"] = side, ["error"] = "Locked property not available on " + dimension.GetType().Name });
                }
            }
            catch (Exception ex)
            {
                alignResult.Add(new JObject { ["side"] = side, ["error"] = ex.Message });
            }
        }

        private static void TryLabelDimension(Document doc, Dimension dimension, string paramName, JObject result)        {
            try
            {
                var param = doc.FamilyManager.GetParameters()
                    .FirstOrDefault(p => string.Equals(p.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase));
                if (param == null) { result["label_error"] = "Parameter not found: " + paramName; return; }
                dimension.FamilyLabel = param;
                result["label_associated"] = dimension.FamilyLabel?.Definition?.Name;
            }
            catch (Exception ex)
            {
                result["label_error"] = ex.Message;
            }
        }

        // ── Rollback-mode helpers (unchanged) ──

        private static JObject TryDimension(Document doc, ViewPlan plan, ReferencePlane first, ReferencePlane second, XYZ start, XYZ end, string label)
        {
            var result = new JObject { ["attempt"] = label };
            if (first == null || second == null) { result["error"] = "missing plane"; return result; }
            try
            {
                var references = new ReferenceArray();
                references.Append(first.GetReference());
                references.Append(second.GetReference());
                var dimension = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(start, end), references);
                result["created_id"] = RevitCompat.GetId(dimension.Id);
                result["reference_count"] = dimension.References.Size;
                result["segments"] = dimension.NumberOfSegments;
                result["value"] = dimension.Value;
                result["are_segments_equal"] = dimension.NumberOfSegments > 1 ? dimension.AreSegmentsEqual : false;
            }
            catch (Exception ex) { result["error"] = ex.Message; }
            return result;
        }

        private static JObject TryThreeReferenceDimension(Document doc, ViewPlan plan, ReferencePlane first, ReferencePlane center, ReferencePlane second)
        {
            var result = new JObject { ["attempt"] = "three-reference EQ" };
            if (first == null || center == null || second == null) { result["error"] = "missing plane"; return result; }
            try
            {
                var references = new ReferenceArray();
                references.Append(first.GetReference());
                references.Append(center.GetReference());
                references.Append(second.GetReference());
                var dimension = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(new XYZ(-2, 0, 0), new XYZ(2, 0, 0)), references);
                result["created_id"] = RevitCompat.GetId(dimension.Id);
                result["reference_count"] = dimension.References.Size;
                result["segments"] = dimension.NumberOfSegments;
                dimension.AreSegmentsEqual = true;
                result["are_segments_equal"] = dimension.AreSegmentsEqual;
            }
            catch (Exception ex) { result["error"] = ex.Message; }
            return result;
        }

        // ── Shared helpers ──

        private static ViewPlan FindZeroLevelFloorPlan(Document doc)
            => new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null && Math.Abs(v.GenLevel.Elevation) <= 1e-8);

        private static ReferencePlane EnsurePlane(Document doc, ViewPlan plan, string name, XYZ bubble, XYZ free)
        {
            var existing = FindPlane(doc, name);
            if (existing != null) return existing;
            var plane = doc.FamilyCreate.NewReferencePlane(bubble, free, XYZ.BasisZ, plan);
            plane.Name = name;
            return plane;
        }

        private static JArray ReadFaces(Document doc, ReferencePlane plane, View view)
        {
            var faces = new JArray();
            if (plane == null) return faces;
            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
            if (view != null) options.View = view;
            var geometry = plane.get_Geometry(options);
            if (geometry != null)
            {
                foreach (var face in geometry.OfType<PlanarFace>())
                {
                    faces.Add(new JObject
                    {
                        ["area"] = face.Area,
                        ["normal"] = Point(face.FaceNormal),
                        ["origin"] = Point(face.Origin),
                        ["reference_stable"] = face.Reference != null ? SafeStableRepresentation(face.Reference, doc) : null
                    });
                }
            }
            return faces;
        }

        private static ReferencePlane FindPlane(Document doc, string name)
            => new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().FirstOrDefault(p => p.Name == name);

        private static string SafeStableRepresentation(Reference reference, Document doc)
        {
            try { return reference.ConvertToStableRepresentation(doc); }
            catch (Exception ex) { return "ERROR: " + ex.Message; }
        }

        private static JArray Point(XYZ point) => new JArray(point.X, point.Y, point.Z);
    }
}
