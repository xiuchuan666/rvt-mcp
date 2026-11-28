using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class NativeSqliteDeploymentTests
    {
        [Theory]
        [InlineData(25)]
        [InlineData(26)]
        [InlineData(27)]
        public void Deploy_copies_native_SQLite_to_both_package_locations(int shell)
        {
            var project = Path.Combine(GetRepoRoot(), "src", $"plugin-r{shell}", $"RvtMcp.Plugin.R{shell}.csproj");
            var target = XDocument.Load(project).Root!.Element("Target");
            Assert.NotNull(target);
            Assert.Equal("Deploy", target!.Attribute("Name")!.Value);

            var nativeItem = Assert.Single(target.Elements("ItemGroup").Elements("DeployNativeSqlite"));
            var source = nativeItem.Attribute("Include")!.Value;
            Assert.Equal(@"$(OutputPath)runtimes\win-x64\native\e_sqlite3.dll", source);

            var copies = target.Elements("Copy");
            AssertRootCopy(copies, @"$(DeployDir)e_sqlite3.dll");
            AssertRootCopy(copies, @"$(DeployDir)runtimes\win-x64\native\");
            Assert.Contains(target.Elements("Error"), error =>
                error.Attribute("Condition")!.Value.Contains("DeployNativeSqlite", StringComparison.Ordinal));
        }

        [Fact]
        public void Deploy_target_materializes_both_native_SQLite_locations_in_an_isolated_profile()
        {
            var root = GetRepoRoot();
            var output = Path.Combine(Path.GetTempPath(), $"rvtmcp-native-deploy-{Guid.NewGuid():N}");
            var bin = Path.Combine(output, "bin");
            var native = Path.Combine(bin, "runtimes", "win-x64", "native");
            Directory.CreateDirectory(native);
            File.WriteAllBytes(Path.Combine(native, "e_sqlite3.dll"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bin, "RvtMcp.Plugin.dll"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(bin, "Microsoft.Data.Sqlite.dll"), new byte[] { 7, 8, 9 });
            File.WriteAllBytes(Path.Combine(bin, "Newtonsoft.Json.dll"), new byte[] { 10, 11, 12 });

            try
            {
                var args = new[]
                {
                    "msbuild",
                    Path.Combine(root, "src", "plugin-r27", "RvtMcp.Plugin.R27.csproj"),
                    "-nologo",
                    "-target:Deploy",
                    "-p:Configuration=Release",
                    "-p:RvtMcpSkipDeploy=false",
                    $"-p:OutputPath={bin}{Path.DirectorySeparatorChar}",
                    $"-p:APPDATA={output}",
                };
                var (exitCode, log) = RunDotnet(args);
                Assert.True(exitCode == 0, log);

                var deployed = Path.Combine(output, "Autodesk", "Revit", "Addins", "2027", "RvtMcp");
                Assert.True(File.Exists(Path.Combine(deployed, "e_sqlite3.dll")), log);
                Assert.True(File.Exists(Path.Combine(deployed, "runtimes", "win-x64", "native", "e_sqlite3.dll")), log);
            }
            finally
            {
                if (Directory.Exists(output)) Directory.Delete(output, true);
            }
        }

        private static void AssertRootCopy(System.Collections.Generic.IEnumerable<XElement> copies, string destination)
        {
            if (destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Single(copies, copy =>
                    copy.Attribute("SourceFiles")?.Value == "@(DeployNativeSqlite)" &&
                    copy.Attribute("DestinationFiles")?.Value == destination);
            }
            else
            {
                Assert.Single(copies, copy =>
                    copy.Attribute("SourceFiles")?.Value == "@(DeployNativeSqlite)" &&
                    copy.Attribute("DestinationFolder")?.Value == destination);
            }
        }

        private static (int ExitCode, string Log) RunDotnet(string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "dotnet msbuild timed out");
            return (process.ExitCode, stdout.Result + stderr.Result);
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", ".."));
        }
    }
}
