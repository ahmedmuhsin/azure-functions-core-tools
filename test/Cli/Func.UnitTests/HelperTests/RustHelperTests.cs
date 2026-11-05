// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    [Collection("NativeWorkerRuntimeTests")]
    public class RustHelperTests : IDisposable
    {
        private readonly string _originalDirectory = Environment.CurrentDirectory;
        private readonly string _projectDirectory;

        public RustHelperTests()
        {
            _projectDirectory = Path.Combine(Path.GetTempPath(), "func-rust-helpers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectDirectory);
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = _originalDirectory;
            try
            {
                Directory.Delete(_projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a scanner may still hold a handle on a freshly written file.
            }
        }

        [Theory]
        [InlineData("my-app", "my-app")]
        [InlineData("MyApp", "myapp")]
        [InlineData("My Function App", "my-function-app")]
        [InlineData("my.app", "my-app")]
        [InlineData("my--app", "my-app")]
        [InlineData("a__b", "a__b")]
        [InlineData("_hidden", "_hidden")]
        [InlineData("123abc", "app-123abc")]
        [InlineData("-leading", "leading")]
        [InlineData("trailing-", "trailing")]
        [InlineData("\u00fcber", "ber")]
        [InlineData("", "app")]
        [InlineData("---", "app")]
        [InlineData("app", "app")]
        public void SanitizePackageName_ProducesAValidCargoPackageName(string folderName, string expected)
        {
            RustHelpers.SanitizePackageName(folderName).Should().Be(expected);
        }

        [Theory]
        [InlineData("test")]
        [InlineData("fn")]
        [InlineData("core")]
        [InlineData("std")]
        [InlineData("build")]
        [InlineData("deps")]
        [InlineData("proc-macro")]
        [InlineData("con")]
        [InlineData("nul")]
        [InlineData("com1")]
        [InlineData("TEST")]
        public void SanitizePackageName_ReservedName_GetsASuffix(string folderName)
        {
            RustHelpers.SanitizePackageName(folderName).Should().Be(folderName.ToLowerInvariant() + "-app");
        }

        [Fact]
        public void SanitizePackageName_Null_FallsBackToApp()
        {
            RustHelpers.SanitizePackageName(null).Should().Be("app");
        }

        [Fact]
        public void ProjectMarker_IsCargoToml()
        {
            RustHelpers.CargoTomlFileName.Should().Be("Cargo.toml");
        }

        [Fact]
        public async Task SetupProject_WritesManifestSourceAndFuncIgnore()
        {
            Environment.CurrentDirectory = _projectDirectory;

            await RustHelpers.SetupProject("My Rust App");

            // The dependency lines and the sample are placeholders in their own template files, so only
            // their shape is pinned here. The package name is filled in by Core Tools.
            var manifest = File.ReadAllText(Path.Combine(_projectDirectory, "Cargo.toml"));
            manifest.Should().Contain("[package]")
                .And.Contain("name = \"my-rust-app\"")
                .And.NotContain("{PackageName}");
            AssertLooksLikeToml(manifest);

            var source = File.ReadAllText(Path.Combine(_projectDirectory, "src", "main.rs"));
            source.Should().Contain("fn main");

            File.Exists(Path.Combine(_projectDirectory, Constants.FuncIgnoreFile)).Should().BeTrue();
        }

        [Fact]
        public async Task SetupProject_FilesEndWithANewLine()
        {
            // rustfmt and most editors expect a final newline, and the embedded resources do not carry one.
            Environment.CurrentDirectory = _projectDirectory;

            await RustHelpers.SetupProject("app");

            File.ReadAllText(Path.Combine(_projectDirectory, "Cargo.toml")).Should().EndWith(Environment.NewLine);
            File.ReadAllText(Path.Combine(_projectDirectory, "src", "main.rs")).Should().EndWith(Environment.NewLine);
        }

        [Fact]
        public async Task SetupProject_DoesNotOverwriteExistingFiles()
        {
            Environment.CurrentDirectory = _projectDirectory;
            Directory.CreateDirectory(Path.Combine(_projectDirectory, "src"));
            File.WriteAllText(Path.Combine(_projectDirectory, "Cargo.toml"), "# mine\n");
            File.WriteAllText(Path.Combine(_projectDirectory, "src", "main.rs"), "// mine\n");
            File.WriteAllText(Path.Combine(_projectDirectory, ".funcignore"), "mine\n");

            await RustHelpers.SetupProject("app");

            File.ReadAllText(Path.Combine(_projectDirectory, "Cargo.toml")).Should().Be("# mine\n");
            File.ReadAllText(Path.Combine(_projectDirectory, "src", "main.rs")).Should().Be("// mine\n");
            File.ReadAllText(Path.Combine(_projectDirectory, ".funcignore")).Should().Be("mine\n");
        }

        [Theory]
        [InlineData("bin/\nobj\n", "\n")]
        [InlineData("bin/\r\nobj\r\n", "\r\n")]
        [InlineData("bin/\nobj", "\n")]
        [InlineData("", "\n")]
        public void IgnoreTargetDirectory_AppendsTargetAndKeepsTheSharedContent(string shared, string newLine)
        {
            var result = RustHelpers.IgnoreTargetDirectory(shared);

            result.Should().StartWith(shared);
            result.Split('\n').Select(l => l.Trim()).Should().Contain("target/");
            result.Should().EndWith("target/" + newLine);
        }

        [Theory]
        [InlineData("bin/\ntarget/\n")]
        [InlineData("bin/\n/target\n")]
        [InlineData("target\n")]
        public void IgnoreTargetDirectory_AlreadyIgnored_IsUnchanged(string shared)
        {
            RustHelpers.IgnoreTargetDirectory(shared).Should().Be(shared);
        }

        // Cargo builds extra binaries from src/bin, and the shared rules bin/ and **/bin/ ignore every folder with that name.
        [Theory]
        [InlineData("bin/\n**/bin/\nobj\n", "\n")]
        [InlineData("bin/\r\n**/bin/\r\nobj\r\n", "\r\n")]
        [InlineData("bin/\n**/bin/", "\n")]
        [InlineData("", "\n")]
        public void KeepCargoBinFolderTracked_AppendsTheNegationAfterTheSharedRules(string shared, string newLine)
        {
            var result = RustHelpers.KeepCargoBinFolderTracked(shared);

            result.Should().StartWith(shared);
            result.Split('\n').Select(l => l.Trim()).Should().Contain("!src/bin/");
            result.Should().EndWith("!src/bin/" + newLine);
        }

        [Theory]
        [InlineData("bin/\n!src/bin/\n")]
        [InlineData("bin/\r\n  !src/bin/  \r\n")]
        public void KeepCargoBinFolderTracked_AlreadyThere_IsUnchanged(string shared)
        {
            RustHelpers.KeepCargoBinFolderTracked(shared).Should().Be(shared);
        }

        [Fact]
        public void GitIgnoreAdditions_AppliedTwice_ChangeNothingTheSecondTime()
        {
            var once = RustHelpers.KeepCargoBinFolderTracked(RustHelpers.IgnoreTargetDirectory("bin/\n**/bin/\n"));

            RustHelpers.KeepCargoBinFolderTracked(RustHelpers.IgnoreTargetDirectory(once)).Should().Be(once);
        }

        // A shape check, not a parser. Every line is blank, a comment, a [table] header or a key = value pair.
        // The real validity of the manifest is checked by cargo itself when a project is built.
        private static void AssertLooksLikeToml(string manifest)
        {
            var header = new System.Text.RegularExpressions.Regex(@"^\[{1,2}[A-Za-z0-9_.\-""]+\]{1,2}$");
            var pair = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_\-""]+\s*=\s*\S.*$");

            foreach (var line in manifest.Split('\n').Select(l => l.Trim()))
            {
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                (header.IsMatch(line) || pair.IsMatch(line)).Should().BeTrue($"'{line}' should be a table header or a key = value pair");
            }
        }
    }
}
