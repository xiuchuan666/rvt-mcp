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
        public string Description => "Revit 2027: inspect an Extrusion's native sketch references and geometry for parametric dimension design. Read-only.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""model_path""],
  ""properties"": {
    ""model_path"": {""type"": ""string"", ""description"": ""Absolute path of the .rfa to inspect.""},
    ""extrusion_id"": {""type"": ""integer"", ""description"": ""Optional ElementId of the Extrusion; otherwise the first Extrusion is used.""},
    ""create_reference_plane"": {""type"": ""boolean"", ""default"": false},
    ""create_dimension"": {""type"": ""boolean"", ""default"": false}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            var modelPath = request.Value<string>("model_path");
            var extrusionIdValue = request.Value<long?>("extrusion_id");
            var createReferencePlane = request.Value<bool?>("create_reference_plane") ?? false;
            var createDimension = request.Value<bool?>("create_dimension") ?? false;
            if (string.IsNullOrWhiteSpace(modelPath))
                return CommandResult.Fail("model_path is required.");
            if (!System.IO.File.Exists(modelPath))
                return CommandResult.Fail("model_path does not exist: " + modelPath);

            Document doc = null;
            try
            {
                doc = app.Application.OpenDocumentFile(modelPath);
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
                if (createReferencePlane || createDimension)
                {
                    using (var transaction = new Transaction(doc, "RvtMcp probe: create reference plane / dimension"))
                    {
                        transaction.Start();
                        try
                        {
                            var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null && Math.Abs(v.GenLevel.Elevation) <= 1e-8);
                            if (plan == null) throw new InvalidOperationException("No zero-level floor plan.");
                            doc.Regenerate();
                            var attempts = new JArray();
                            if (createReferencePlane)
                            {
                                var right = doc.FamilyCreate.NewReferencePlane(new XYZ(2, -2, 0), new XYZ(2, 2, 0), XYZ.BasisZ, plan);
                                right.Name = "RvtMcp Probe Right";
                                attempts.Add(new JObject { ["created_plane"] = RevitCompat.GetId(right.Id), ["stable"] = SafeStableRepresentation(right.GetReference(), doc) });
                                doc.Regenerate();
                            }
                            if (createDimension)
                            {
                                var left = FindPlane(doc, "RvtMcp Left");
                                var right = FindPlane(doc, "RvtMcp Probe Right") ?? FindPlane(doc, "RvtMcp Right");
                                var center = FindPlane(doc, "Center (Left/Right)");
                                attempts.Add(TryDimension(doc, plan, left, right, new XYZ(-2, 0, 0), new XYZ(2, 0, 0), "two-reference length"));
                                attempts.Add(TryDimension(doc, plan, left, center, new XYZ(-2, 0, 0), new XYZ(0, 0, 0), "two-reference left-center"));
                                attempts.Add(TryDimension(doc, plan, center, right, new XYZ(0, 0, 0), new XYZ(2, 0, 0), "two-reference center-right"));
                                attempts.Add(TryThreeReferenceDimension(doc, plan, left, center, right));
                            }
                            result["dimension_attempts"] = attempts;
                            transaction.RollBack(); // probe is read-only; never commit probe geometry
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
                if (doc != null) doc.Close(false);
            }
        }

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
