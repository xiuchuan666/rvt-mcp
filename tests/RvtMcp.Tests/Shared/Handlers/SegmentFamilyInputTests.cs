using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests;

public sealed class SegmentFamilyInputTests
{
    private static readonly Lazy<Func<string, JObject>> GeometryProbe = new(() => HandlerProbe.Compile(
        "public static class Probe { public static string Run(string scenario) { var v = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(scenario.Split(','), s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))); return new Newtonsoft.Json.Linq.JObject { [\"box\"] = new Newtonsoft.Json.Linq.JArray(RvtMcp.Plugin.Handlers.SegmentFamilyInput.ExpectedBoundingBoxMm(v[0], v[1], v[2])), [\"error\"] = RvtMcp.Plugin.Handlers.SegmentFamilyInput.Validate(\"segment\", \"Standard\", v[0], v[3], v[1], v[2]) }.ToString(); } }",
        new[] { "src/shared/Handlers/Families/SegmentFamilyInput.cs", "src/shared/Handlers/Families/ParametricFamilyInput.cs", "src/shared/Handlers/Documents/OpenModelLocalPolicy.cs" }));

    [Theory]
    [InlineData(14000, 2000, 36, 342.604386, 4326.237921)]
    [InlineData(14000, 2000, 90, 2050.252532, 9899.494937)]
    // Arc midpoint remains at +X: x extent never exceeds R. Opposite edge reaches -Y.
    [InlineData(14000, 2000, 180, 7000, 14000)]
    [InlineData(14000, 2000, 240, 10500, 14000)]
    public void Analytic_annular_sector_bounding_box_handles_short_and_major_arcs(
        double diameter, double width, double angle, double x, double y)
    {
        var box = GeometryProbe.Value($"{diameter},{width},{angle},600")["box"]!.Select(v => v.Value<double>()).ToArray();
        Assert.Equal(x, box[0], 6);
        Assert.Equal(y, box[1], 6);
        Assert.Equal(width, box[2], 6);
    }

    [Theory]
    [InlineData(14000, 600, 36, null)]
    [InlineData(14000, 7000, 36, "thickness_mm")]
    [InlineData(14000, 600, 0, "segment_angle_deg")]
    [InlineData(14000, 600, 360, "segment_angle_deg")]
    [InlineData(14000, 600, double.NaN, "segment_angle_deg")]
    public void Rejects_degenerate_annular_sector_and_angle(double diameter, double thickness, double angle, string error)
    {
        var result = GeometryProbe.Value($"{diameter},2000,{angle},{thickness}").Value<string>("error");
        Assert.Equal(error == null, result == null);
        if (error != null) Assert.Contains(error, result);
    }
}
