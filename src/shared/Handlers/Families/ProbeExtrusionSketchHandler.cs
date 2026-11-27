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

            // Probe 6: planes + dimension + alignment lock + parameter flex test (OLD ORDER — dimension before alignment)
            probes.Add(CommitProbeAlignmentFlex(doc, plan, "alignment-lock two-ref", useEq: false));
            probes.Add(CommitProbeAlignmentFlex(doc, plan, "alignment-lock EQ", useEq: true));

            // Probe 7: CORRECT ORDER — align-first then dimension, planes at actual sketch positions
            probes.Add(CommitProbeAlignFirst(doc, plan, "align-first two-ref", useEq: false));
            probes.Add(CommitProbeAlignFirst(doc, plan, "align-first EQ", useEq: true));
            // Width axis variants
            probes.Add(CommitProbeAlignFirstWidth(doc, plan, "align-first width two-ref"));
            probes.Add(CommitProbeAlignFirstWidth(doc, plan, "align-first width EQ"));

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

        // ── Probe 7: align-first strategy — planes at exact sketch positions, lock before dimension ──

        /// <summary>
        /// Length-axis probe: reads actual sketch geometry, creates planes at exact sketch line
        /// positions, aligns and locks sketch curves to planes FIRST, then creates labelled
        /// dimension, commits, and flexes the Length parameter.
        /// </summary>
        private static JObject CommitProbeAlignFirst(Document doc, ViewPlan plan, string label, bool useEq)
            => AlignFirstCore(doc, plan, label, useEq, axis: "Length");

        /// <summary>
        /// Width-axis probe: same align-first strategy but drives the Width parameter
        /// using front/back horizontal sketch lines.
        /// </summary>
        private static JObject CommitProbeAlignFirstWidth(Document doc, ViewPlan plan, string label)
            => AlignFirstCore(doc, plan, label, useEq: false, axis: "Width");

        private static JObject AlignFirstCore(Document doc, ViewPlan plan, string label, bool useEq, string axis)
        {
            var result = new JObject { ["probe"] = label, ["axis"] = axis };
            var manager = doc.FamilyManager;
            if (manager == null) { result["error"] = "No FamilyManager"; return result; }

            bool isLength = string.Equals(axis, "Length", StringComparison.OrdinalIgnoreCase);

            using (var transaction = new Transaction(doc, "RvtMcp align-first: " + label))
            {
                transaction.Start();
                try
                {
                    // Step 1: Read actual sketch geometry to find exact positions
                    var extr = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().FirstOrDefault();
                    if (extr?.Sketch == null) throw new InvalidOperationException("No extrusion sketch");

                    var sketchCurves = extr.Sketch.GetAllElements()
                        .Select(id => doc.GetElement(id) as ModelCurve)
                        .Where(mc => mc?.GeometryCurve is Line)
                        .Select(mc => new { Curve = mc, Line = (Line)mc.GeometryCurve })
                        .ToList();

                    // Separate vertical (Y-direction) and horizontal (X-direction) lines
                    var verticalLines = sketchCurves
                        .Where(mc => Math.Abs(mc.Line.Direction.Y) > 0.99)
                        .OrderBy(mc => mc.Line.GetEndPoint(0).X)
                        .ToList();
                    var horizontalLines = sketchCurves
                        .Where(mc => Math.Abs(mc.Line.Direction.X) > 0.99)
                        .OrderBy(mc => mc.Line.GetEndPoint(0).Y)
                        .ToList();

                    if (verticalLines.Count < 2 || horizontalLines.Count < 2)
                        throw new InvalidOperationException("Insufficient sketch lines");

                    // Choose which lines to align based on axis
                    dynamic firstLine, secondLine;
                    double firstCoord, secondCoord;
                    if (isLength)
                    {
                        firstLine = verticalLines.First();
                        secondLine = verticalLines.Last();
                        firstCoord = firstLine.Line.GetEndPoint(0).X;
                        secondCoord = secondLine.Line.GetEndPoint(0).X;
                    }
                    else
                    {
                        firstLine = horizontalLines.First();
                        secondLine = horizontalLines.Last();
                        firstCoord = firstLine.Line.GetEndPoint(0).Y;
                        secondCoord = secondLine.Line.GetEndPoint(0).Y;
                    }
                    result["first_coord"] = firstCoord;
                    result["second_coord"] = secondCoord;

                    // Step 2: Create reference planes at EXACT sketch line positions
                    string firstName = isLength ? "RvtMcp Left" : "RvtMcp Front";
                    string secondName = isLength ? "RvtMcp Right" : "RvtMcp Back";

                    if (isLength)
                    {
                        EnsurePlane(doc, plan, firstName,
                            new XYZ(firstCoord, -2, 0), new XYZ(firstCoord, 2, 0));
                        EnsurePlane(doc, plan, secondName,
                            new XYZ(secondCoord, -2, 0), new XYZ(secondCoord, 2, 0));
                    }
                    else
                    {
                        EnsurePlane(doc, plan, firstName,
                            new XYZ(-3, firstCoord, 0), new XYZ(3, firstCoord, 0));
                        EnsurePlane(doc, plan, secondName,
                            new XYZ(-3, secondCoord, 0), new XYZ(3, secondCoord, 0));
                    }
                    doc.Regenerate();

                    var firstPlane = FindPlane(doc, firstName);
                    var secondPlane = FindPlane(doc, secondName);
                    if (firstPlane == null || secondPlane == null)
                        throw new InvalidOperationException("Failed to create reference planes");

                    // Step 3: ALIGN AND LOCK sketch curves to planes (BEFORE dimension)
                    var view = FindZeroLevelFloorPlan(doc);
                    var alignResult = new JArray();

                    // Align first line
                    try
                    {
                        var alignment = doc.FamilyCreate.NewAlignment(view,
                            firstLine.Curve.GeometryCurve.Reference, firstPlane.GetReference());
                        TrySetLocked(alignment, alignResult, "first");
                    }
                    catch (Exception ex)
                    {
                        alignResult.Add(new JObject { ["side"] = "first", ["error"] = ex.Message });
                    }

                    doc.Regenerate();

                    // Align second line
                    try
                    {
                        var alignment = doc.FamilyCreate.NewAlignment(view,
                            secondLine.Curve.GeometryCurve.Reference, secondPlane.GetReference());
                        TrySetLocked(alignment, alignResult, "second");
                    }
                    catch (Exception ex)
                    {
                        alignResult.Add(new JObject { ["side"] = "second", ["error"] = ex.Message });
                    }

                    result["alignments"] = alignResult;
                    doc.Regenerate();

                    // Step 4: Create dimension on the aligned planes
                    var dimStart = isLength ? new XYZ(firstCoord, 0, 0) : new XYZ(0, firstCoord, 0);
                    var dimEnd = isLength ? new XYZ(secondCoord, 0, 0) : new XYZ(0, secondCoord, 0);

                    Dimension dim;
                    if (useEq && isLength)
                    {
                        var center = FindPlane(doc, "Center (Left/Right)");
                        if (center == null) throw new InvalidOperationException("Center plane not found for EQ");
                        var refs = new ReferenceArray();
                        refs.Append(firstPlane.GetReference());
                        refs.Append(center.GetReference());
                        refs.Append(secondPlane.GetReference());
                        dim = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(dimStart, dimEnd), refs);
                        dim.AreSegmentsEqual = true;
                    }
                    else
                    {
                        var refs = new ReferenceArray();
                        refs.Append(firstPlane.GetReference());
                        refs.Append(secondPlane.GetReference());
                        dim = doc.FamilyCreate.NewDimension(plan, Line.CreateBound(dimStart, dimEnd), refs);
                    }
                    result["dimension_id"] = RevitCompat.GetId(dim.Id);
                    result["dimension_value_ft"] = dim.Value;
                    TryLabelDimension(doc, dim, axis, result);

                    doc.Regenerate();

                    var commitStatus = transaction.Commit();
                    result["commit_status"] = commitStatus.ToString();

                    // Read back post-commit
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
                    return result;
                }
            }

            // Step 5: Flex test — change the parameter and verify geometry follows
            var param = manager.GetParameters()
                .FirstOrDefault(p => string.Equals(p.Definition.Name, axis, StringComparison.OrdinalIgnoreCase));
            if (param == null) { result["flex_error"] = axis + " parameter not found"; return result; }

            var originalValue = manager.CurrentType.AsDouble(param);
            if (originalValue == null) { result["flex_error"] = axis + " value is null"; return result; }
            var origVal = originalValue.Value;
            result["original_value_ft"] = origVal;

            using (var flexTx = new Transaction(doc, "RvtMcp flex: " + label))
            {
                flexTx.Start();
                try
                {
                    var newVal = origVal * 0.75;
                    manager.Set(param, newVal);
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
                    result["flex_new_value_ft"] = newVal;

                    // Read sketch curves after flex
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

                    // Restore
                    manager.Set(param, origVal);
                    doc.Regenerate();
                    flexTx.Commit();
                }
                catch (Exception ex)
                {
                    result["flex_error"] = ex.Message;
                    if (flexTx.GetStatus() == TransactionStatus.Started)
                        flexTx.RollBack();
                }
            }

            return result;
        }

        // ── Probe 6: full pipeline with alignment lock + flex (OLD ORDER — kept for comparison) ──

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
