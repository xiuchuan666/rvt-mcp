using System;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Host-independent input and analytic geometry for an arc-extrusion segment.</summary>
    internal static class SegmentFamilyInput
    {
        internal const double DegreesPerRadian = 180.0 / Math.PI;

        internal static string Validate(string familyName, string typeName,
            double outerDiameterMm, double thicknessMm, double ringWidthMm, double segmentAngleDeg)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return "family_name is required and must not be blank.";
            if (string.IsNullOrWhiteSpace(typeName)) return "type_name is required and must not be blank.";
            var error = ValidateLength("outer_diameter_mm", outerDiameterMm)
                ?? ValidateLength("thickness_mm", thicknessMm)
                ?? ValidateLength("ring_width_mm", ringWidthMm);
            if (error != null) return error;
            if (double.IsNaN(segmentAngleDeg) || double.IsInfinity(segmentAngleDeg)
                || segmentAngleDeg <= 0 || segmentAngleDeg >= 360)
                return "segment_angle_deg must be finite and strictly between 0 and 360 degrees.";
            if (thicknessMm >= outerDiameterMm * 0.5)
                return "thickness_mm must be less than half of outer_diameter_mm so the inner radius is positive.";
            return null;
        }

        internal static string ValidateFiles(string templatePath, string outputPath)
            // Reuse the already accepted family template/output and local-file safety rules.
            => ParametricFamilyInput.Validate("segment", templatePath, outputPath,
                100, 100, 100, "Length,Width,Height", "type", "Standard");

        internal static double HalfAngleRadians(double angleDegrees)
            => angleDegrees * Math.PI / 360.0;

        internal static double[] ExpectedBoundingBoxMm(double outerDiameterMm, double thicknessMm, double ringWidthMm, double angleDegrees)
        {
            var outer = outerDiameterMm * 0.5;
            var inner = outer - thicknessMm;
            var half = HalfAngleRadians(angleDegrees);
            var sin = Math.Sin(half);
            var cos = Math.Cos(half);
            // The annular sector is symmetric about +X. The outer arc apex sits at X=outer.
            // X minimum: for half<=90deg the inner arc endpoints are the leftmost points
            // (inner*cos); for half>90deg the outer arc wraps past the Y axis to X=-outer.
            // X span therefore must include the inner arc — using outer*(1-cos) alone would
            // describe only the outer arc sagitta and wrongly reject the correct native geometry.
            var minX = half > Math.PI / 2 ? -outer : inner * cos;
            var x = outer - minX;
            // Y span: chord width of the outer arc for half<=90deg, full diameter beyond that.
            var y = half > Math.PI / 2 ? 2 * outer : 2 * outer * sin;
            return new[] { x, y, ringWidthMm };
        }

        internal static double MillimetersFromFeet(double feet) => ParametricFamilyInput.MillimetersFromFeet(feet);

        /// <summary>Exact annular-sector volume in cubic millimetres (outer sector minus inner sector, times ring width).
        /// Unlike the bounding box, this does not depend on Revit's chordal arc discretisation.</summary>
        internal static double ExpectedVolumeMm3(double outerDiameterMm, double thicknessMm, double ringWidthMm, double angleDegrees)
        {
            var outer = outerDiameterMm * 0.5;
            var inner = outer - thicknessMm;
            var sweep = angleDegrees * Math.PI / 180.0; // full central angle in radians
            var area = sweep * 0.5 * (outer * outer - inner * inner);
            return area * ringWidthMm;
        }

        internal static double DegreesFromRadians(double radians) => radians * DegreesPerRadian;
        internal static double ToRevitFeet(double mm) => ParametricFamilyInput.ToRevitFeet(mm);
        internal static string TemporaryOutputPath(string path) => ParametricFamilyInput.TemporaryOutputPath(path);
        internal static string DeleteTemporaryFile(string path) => ParametricFamilyInput.DeleteTemporaryFile(path);

        private static string ValidateLength(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return name + " must be a finite positive length in millimeters.";
            var feet = ToRevitFeet(value);
            if (double.IsNaN(feet) || double.IsInfinity(feet)) return name + " is outside the supported Revit length range.";
            return null;
        }
    }
}
