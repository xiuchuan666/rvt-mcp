using System;
using System.IO;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests;

public sealed class ParametricFamilyInputTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rvtmcp-family-input-" + Guid.NewGuid().ToString("N"));
    private readonly string _template;
    private readonly string _output;

    public ParametricFamilyInputTests()
    {
        Directory.CreateDirectory(_directory);
        _template = Path.Combine(_directory, "Generic Model.rft");
        _output = Path.Combine(_directory, "Cabinet.rfa");
        File.WriteAllText(_template, "test placeholder; never passed to Revit");
    }

    [Fact]
    public void Valid_new_local_paths_pass_all_preflight_checks()
    {
        Assert.Null(Validate());
    }

    [Theory]
    [InlineData("", 1000, "family_name")]
    [InlineData(null, 1000, "family_name")]
    [InlineData("Cabinet", 0, "length_mm")]
    [InlineData("Cabinet", -1, "length_mm")]
    [InlineData("Cabinet", double.NaN, "length_mm")]
    [InlineData("Cabinet", double.PositiveInfinity, "length_mm")]
    public void Blank_names_and_nonpositive_or_nonfinite_dimensions_are_rejected(string name, double length, string field)
    {
        Assert.Contains(field, Validate(name, length));
    }

    [Fact]
    public void Instance_scope_is_not_silently_substituted_for_type_parameters()
    {
        Assert.Contains("parameter_scope", ParametricFamilyInput.Validate("Cabinet", _template, _output,
            1000, 500, 800, "Length,Width,Height", "instance", "Standard"));
    }

    [Fact]
    public void Missing_template_is_reported_before_opening_revit()
    {
        var missing = Path.Combine(_directory, "missing.rft");
        Assert.Contains("template does not exist", ParametricFamilyInput.Validate("Cabinet", missing, _output,
            1000, 500, 800, "Length,Width,Height", "type", "Standard"));
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public void Existing_output_is_never_overwritten()
    {
        File.WriteAllText(_output, "existing family");
        Assert.Contains("never overwrites", Validate());
        Assert.Equal("existing family", File.ReadAllText(_output));
    }

    [Theory]
    [InlineData("Cabinet.rvt", ".rfa")]
    [InlineData(@"C:relative.rfa", "full local path")]
    [InlineData(@"\\server\share\Cabinet.rfa", "local path")]
    public void Invalid_or_network_output_paths_are_rejected(string output, string message)
    {
        Assert.Contains(message, ParametricFamilyInput.Validate("Cabinet", _template, Path.Combine(_directory, output),
            1000, 500, 800, "Length,Width,Height", "type", "Standard"));
    }

    [Fact]
    public void Missing_output_directory_is_not_created()
    {
        var output = Path.Combine(_directory, "new-directory", "Cabinet.rfa");
        Assert.Contains("parent directory", ParametricFamilyInput.Validate("Cabinet", _template, output,
            1000, 500, 800, "Length,Width,Height", "type", "Standard"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    [Theory]
    [InlineData(304.8, 1.0)]
    [InlineData(1000, 1000 / 304.8)]
    [InlineData(1200.5, 1200.5 / 304.8)]
    public void Millimeters_convert_to_internal_feet_and_back(double mm, double feet)
    {
        Assert.Equal(feet, ParametricFamilyInput.ToRevitFeet(mm), 12);
        Assert.Equal(mm, ParametricFamilyInput.ToMillimeters(feet), 10);
        Assert.Equal(mm, ParametricFamilyInput.MillimetersFromFeet(ParametricFamilyInput.ToRevitFeet(mm)), 6);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string Validate(string name = "Cabinet", double length = 1000)
        => ParametricFamilyInput.Validate(name, _template, _output, length, 500, 800,
            "Length,Width,Height", "type", "Standard");
}
