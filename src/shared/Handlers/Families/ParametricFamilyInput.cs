using System;
using System.IO;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Input and file-safety rules for creating a single parameterized extrusion family.
    /// This class deliberately has no Revit API dependency: all decisions happen before
    /// a family document is opened and are host-independent unit tested.
    /// </summary>
    internal static class ParametricFamilyInput
    {
        internal const double MillimetersPerFoot = 304.8;

        internal static string Validate(string familyName, string templatePath, string outputPath,
            double lengthMm, double widthMm, double heightMm, string parameterNames,
            string parameterScope, string typeName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return "family_name is required and must not be blank.";
            if (string.IsNullOrWhiteSpace(typeName))
                return "type_name is required and must not be blank.";
            if (string.IsNullOrWhiteSpace(parameterNames)
                || !string.Equals(parameterNames, "Length,Width,Height", StringComparison.Ordinal))
                return "This first family-authoring slice exposes exactly the type parameters Length, Width and Height.";

            if (!string.Equals(parameterScope, "type", StringComparison.OrdinalIgnoreCase))
                return "Length, Width and Height are type parameters; parameter_scope must be 'type'.";

            var dimensionError = ValidateDimension("length_mm", lengthMm)
                ?? ValidateDimension("width_mm", widthMm)
                ?? ValidateDimension("height_mm", heightMm);
            if (dimensionError != null) return dimensionError;

            var templateError = ValidatePath(templatePath, ".rft", "template_path", mustExist: true);
            if (templateError != null) return templateError;
            var outputError = ValidatePath(outputPath, ".rfa", "output_path", mustExist: false);
            if (outputError != null) return outputError;
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
                return "The parent directory of output_path must already exist; it is not created automatically.";
            if (File.Exists(outputPath))
                return "output_path already exists. This tool never overwrites an existing .rfa file; choose a new path.";
            return null;
        }

        internal static string TemporaryOutputPath(string outputPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            return Path.Combine(directory, ".rvtmcp-" + Guid.NewGuid().ToString("N") + ".rfa");
        }

        internal static string DeleteTemporaryFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                File.Delete(path);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "The temporary file could not be removed: " + path + ". " + ex.Message;
            }
        }

        internal static double ToRevitFeet(double millimeters) => millimeters / MillimetersPerFoot;

        internal static double ToMillimeters(double feet) => feet * MillimetersPerFoot;

        internal static double MillimetersFromFeet(double feet)
            => Math.Round(ToMillimeters(feet), 6, MidpointRounding.AwayFromZero);

        private static string ValidateDimension(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return name + " must be a finite positive length in millimeters.";
            if (double.IsNaN(ToRevitFeet(value)) || double.IsInfinity(ToRevitFeet(value)))
                return name + " is outside the supported Revit length range.";
            return null;
        }

        private static bool IsAbsoluteLocalPath(string path)
        {
            // Path.IsPathFullyQualified is unavailable on the .NET Framework 4.8 shells.
            return !string.IsNullOrEmpty(path) && path.Length >= 3 && char.IsLetter(path[0])
                && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        }

        private static string ValidatePath(string path, string extension, string parameter, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(path))
                return parameter + " is required and must be an absolute local path.";
            try
            {
                // FullPath rejects drive-relative paths such as C:family.rfa. IsPathFullyQualified
                // additionally rejects a bare \family.rfa, which depends on the process drive.
                if (!IsAbsoluteLocalPath(path))
                    return parameter + " must be a full local path beginning with a drive letter.";
                var full = Path.GetFullPath(path);
                if (!IsAbsoluteLocalPath(full))
                    return parameter + " must be a full local path beginning with a drive letter.";
                if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
                    return parameter + " must have the " + extension + " extension.";
                if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
                    return parameter + " must be on a local drive; network paths are not written or read.";
                if (mustExist && !File.Exists(full))
                    return "The family template does not exist. Supply the absolute path of an installed Revit family template (.rft).";
                return null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return parameter + " is not a valid local file path: " + ex.Message;
            }
        }
    }
}
