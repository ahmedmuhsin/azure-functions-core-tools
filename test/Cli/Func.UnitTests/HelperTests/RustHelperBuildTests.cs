// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    [Collection("NativeWorkerRuntimeTests")]
    public class RustHelperBuildTests
    {
        private const string OneBinaryManifest = "/scratch/facts/one/Cargo.toml";
        private const string MultiBinaryManifest = "/scratch/facts/multi/Cargo.toml";
        private const string DependencyManifest = "/scratch/facts/dep/Cargo.toml";

        [Theory]
        [InlineData("cargo 1.90.0 (840b83a10 2025-07-30)", "1.90.0")]
        [InlineData("cargo 1.92.0-nightly (a1b2c3d4e 2025-10-01)", "1.92.0")]
        [InlineData("cargo 1.90 (840b83a10 2025-07-30)", "1.90")]
        [InlineData("warning: something\ncargo 1.91.1 (abc 2025-11-01)", "1.91.1")]
        public void ParseCargoVersion_ReadsTheVersion(string output, string expected)
        {
            RustHelpers.ParseCargoVersion(output).Should().Be(expected);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("")]
        [InlineData(null)]
        public void ParseCargoVersion_Unparseable_ReturnsNull(string output)
        {
            RustHelpers.ParseCargoVersion(output).Should().BeNull();
        }

        [Theory]
        [InlineData("1.90.0", false)]
        [InlineData("1.90.5", false)]
        [InlineData("1.91.0", false)]
        [InlineData("2.0.0", false)]
        [InlineData("1.89.9", true)]
        [InlineData("1.85.0", true)]
        [InlineData("1.0.0", true)]
        [InlineData("0.9.0", true)]
        public void AssertCargoVersion_ValidatesMinimumVersion(string cargoVersion, bool expectException)
        {
            var worker = new WorkerLanguageVersionInfo(WorkerRuntime.Rust, cargoVersion, "cargo");

            var action = () => RustHelpers.AssertCargoVersion(worker);

            if (expectException)
            {
                action.Should().Throw<CliException>();
            }
            else
            {
                action.Should().NotThrow();
            }
        }

        [Fact]
        public void AssertCargoVersion_TooOld_SaysWhatWasTested()
        {
            var worker = new WorkerLanguageVersionInfo(WorkerRuntime.Rust, "1.85.0", "cargo");

            var action = () => RustHelpers.AssertCargoVersion(worker);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("1.85.0")
                .And.Contain("1.90")
                .And.Contain("tested with Rust 1.90.0");
        }

        [Fact]
        public void AssertCargoVersion_NullVersion_ThrowsCliException()
        {
            var action = () => RustHelpers.AssertCargoVersion(null);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("Could not find a Rust toolchain")
                .And.Contain("cargo")
                .And.Contain("1.90");
        }

        [Theory]
        [InlineData("beta1")]
        [InlineData("notaversion")]
        public void AssertCargoVersion_UnparseableVersion_ThrowsCliException(string version)
        {
            var worker = new WorkerLanguageVersionInfo(WorkerRuntime.Rust, version, "cargo");

            var action = () => RustHelpers.AssertCargoVersion(worker);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("Unable to parse the Rust version");
        }

        [SkipIfCargoNonExistFact]
        public async Task GetEnvironmentCargoVersion_ReportsTheRustRuntime()
        {
            WorkerLanguageVersionInfo worker = await RustHelpers.GetEnvironmentCargoVersion();

            worker.Should().NotBeNull();
            worker.Runtime.Should().Be(WorkerRuntime.Rust);
            worker.ExecutablePath.Should().NotBeNullOrEmpty();
            worker.Major.Should().BeGreaterThanOrEqualTo(1);
        }

        // The debug binary that 'func start' builds must never be a file that 'func pack --no-build' or 'func publish --no-build'
        // reads. bin/app is the binary of the package. 'func pack' writes it and the two commands read it. So the binary of 'func start'
        // lives in a folder of its own under target/, where Rust developers expect build output and git ignores it.
        [Fact]
        public void LocalBinaryPath_IsInTheFuncFolderUnderTarget()
        {
            RustHelpers.LocalBinaryPath.Should().Be(Path.Combine("target", "func", OperatingSystem.IsWindows() ? "app.exe" : "app"));
        }

        [Fact]
        public void LocalBinaryPath_IsNeitherOfTheFilesThatPackReads()
        {
            // 'func pack' reads bin/app. On Windows the binary of 'func start' used to be bin\app.exe, next to it.
            RustHelpers.LocalBinaryPath.Should().NotBe(Path.Combine("bin", "app"));
            RustHelpers.LocalBinaryPath.Should().NotBe(Path.Combine("bin", "app.exe"));
            RustHelpers.LocalBinaryPath.Should().NotStartWith("bin" + Path.DirectorySeparatorChar);
        }

        // Go keeps the layout that the shipped native worker config names, on every runtime that shares it.
        [Fact]
        public void GoLocalBinaryPath_StaysTheSharedNativeLayout()
        {
            GoHelpers.LocalBinaryPath.Should().Be(Path.Combine("bin", OperatingSystem.IsWindows() ? "app.exe" : "app"));
        }

        [Fact]
        public void AssertBinaryExists_BinaryPresent_DoesNotThrow()
        {
            var dir = Path.Combine(Path.GetTempPath(), "func-rust-test-" + Guid.NewGuid().ToString("N"));
            var binary = Path.Combine(dir, RustHelpers.LocalBinaryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(binary));
            try
            {
                File.WriteAllText(binary, "stub");

                var action = () => RustHelpers.AssertBinaryExists(dir);

                action.Should().NotThrow();
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void AssertBinaryExists_BinaryMissing_ThrowsActionableCliException()
        {
            var dir = Path.Combine(Path.GetTempPath(), "func-rust-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var action = () => RustHelpers.AssertBinaryExists(dir);

                action.Should().Throw<CliException>()
                    .Which.Message.Should().Contain("Could not find a built Rust binary")
                    .And.Contain("--no-build")
                    .And.Contain("cargo build")
                    .And.Contain(Path.Combine("target", "func"), "the message names the folder that 'func start' runs the binary from");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // bin/app is what 'func pack' writes, and it is a Linux binary for the package. 'func start' does not run it, so it is no
        // binary to start with, and the message says that there is none.
        [Fact]
        public void AssertBinaryExists_OnlyTheBinaryThatPackWrites_IsNoBinaryToStart()
        {
            using var folder = new StartFolder(withBinary: false);
            Directory.CreateDirectory(Path.Combine(folder.Path, "bin"));
            File.WriteAllText(Path.Combine(folder.Path, "bin", "app"), "stub");
            File.WriteAllText(Path.Combine(folder.Path, "bin", "app.exe"), "stub");

            var action = () => RustHelpers.AssertBinaryExists(folder.Path);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("Could not find a built Rust binary");
        }

        // A symbolic link that points nowhere still exists as a file. The user should hear that there is no binary,
        // and not get the raw error of the file system, or a host that fails to start the worker.
        [SkippableFact]
        public void AssertBinaryExists_ADanglingSymbolicLink_SaysThereIsNoBuiltBinary()
        {
            using var folder = new StartFolder(withBinary: false);
            SymbolicLinkTestHelper.CreateOrSkip(Path.Combine(folder.Path, RustHelpers.LocalBinaryPath), Path.Combine(folder.Path, "gone"));

            var action = () => RustHelpers.AssertBinaryExists(folder.Path);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("Could not find a built Rust binary")
                .And.Contain("--no-build");
        }

        [SkippableFact]
        public void AssertBinaryExists_ASymbolicLinkToABinary_DoesNotThrow()
        {
            using var folder = new StartFolder(withBinary: false);
            var target = Path.Combine(folder.Path, "built");
            File.WriteAllText(target, "stub");
            SymbolicLinkTestHelper.CreateOrSkip(Path.Combine(folder.Path, RustHelpers.LocalBinaryPath), target);

            var action = () => RustHelpers.AssertBinaryExists(folder.Path);

            action.Should().NotThrow("the host follows the link to the binary");
        }

        // 'func start --no-build' runs a binary that is already built, so it must not need cargo. Asking cargo for its version
        // is also not free: with a rust-toolchain.toml that names a toolchain which is not installed, rustup downloads
        // it while 'cargo --version' runs. The probe and the build are stand-ins here, and each test says which of them ran.
        [Fact]
        public async Task PrepareForStart_NoBuild_NeedsNeitherTheToolchainNorABuild()
        {
            using var folder = new StartFolder(withBinary: true);
            var steps = new StartSteps(version: null);

            await RustHelpers.PrepareForStart(noBuild: true, folder.Path, steps.GetCargoVersion, steps.Build);

            steps.Calls.Should().BeEmpty("a prebuilt binary runs without cargo");
        }

        [Fact]
        public async Task PrepareForStart_NoBuild_MissingBinary_SaysSoWithoutAskingCargo()
        {
            using var folder = new StartFolder(withBinary: false);
            var steps = new StartSteps(version: null);

            Func<Task> act = () => RustHelpers.PrepareForStart(noBuild: true, folder.Path, steps.GetCargoVersion, steps.Build);

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Could not find a built Rust binary");
            steps.Calls.Should().BeEmpty();
        }

        [Fact]
        public async Task PrepareForStart_Build_ChecksTheToolchainThenBuildsInTheSameFolder()
        {
            using var folder = new StartFolder(withBinary: false);
            var steps = new StartSteps(new WorkerLanguageVersionInfo(WorkerRuntime.Rust, "1.90.0", "cargo"));

            await RustHelpers.PrepareForStart(noBuild: false, folder.Path, steps.GetCargoVersion, steps.Build);

            steps.Calls.Should().Equal($"toolchain probe in {folder.Path}", $"build in {folder.Path}");
        }

        [Fact]
        public async Task PrepareForStart_Build_OldToolchain_StopsBeforeTheBuild()
        {
            using var folder = new StartFolder(withBinary: false);
            var steps = new StartSteps(new WorkerLanguageVersionInfo(WorkerRuntime.Rust, "1.80.0", "cargo"));

            Func<Task> act = () => RustHelpers.PrepareForStart(noBuild: false, folder.Path, steps.GetCargoVersion, steps.Build);

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Rust version 1.80.0 is not supported");
            steps.Calls.Should().Equal($"toolchain probe in {folder.Path}");
        }

        [Fact]
        public async Task PrepareForStart_Build_MissingToolchain_StopsBeforeTheBuild()
        {
            using var folder = new StartFolder(withBinary: false);
            var steps = new StartSteps(version: null);

            Func<Task> act = () => RustHelpers.PrepareForStart(noBuild: false, folder.Path, steps.GetCargoVersion, steps.Build);

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Could not find a Rust toolchain");
            steps.Calls.Should().Equal($"toolchain probe in {folder.Path}");
        }

        // The cargo JSON in the tests below is real output of cargo 1.90.0, captured on Linux.
        [Fact]
        public void ReadBinaryArtifacts_OneBinary_ReturnsIt()
        {
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.OneBinary, OneBinaryManifest);

            binaries.Should().ContainSingle()
                .Which.Should().Be(new RustHelpers.CargoBinary("one-bin", "/ct-target-facts/debug/one-bin"));
        }

        [Fact]
        public void ReadBinaryArtifacts_IgnoresLibrariesAndDependencies()
        {
            // The dependency crate builds a library, and its artifact has no executable.
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.BinaryWithDependency, DependencyManifest);

            binaries.Should().ContainSingle()
                .Which.Name.Should().Be("withdep");
            binaries[0].Path.Should().Be("/ct-target-facts/x86_64-unknown-linux-gnu/release/withdep");
        }

        [Fact]
        public void ReadBinaryArtifacts_IgnoresBinariesOfOtherPackages()
        {
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.OneBinary, "/somewhere/else/Cargo.toml");

            binaries.Should().BeEmpty();
        }

        [Fact]
        public void ReadBinaryArtifacts_IgnoresExecutablesThatAreNotBinaryTargets()
        {
            // The fixture holds three executables of one package: a build script, an example and the binary.
            CargoJson.BinaryWithOtherExecutableTargets.Count(line => line.Contains("\"executable\":\"/")).Should().Be(3);
            CargoJson.BinaryWithOtherExecutableTargets.Should().ContainMatch("*\"kind\":[\"custom-build\"]*").And.ContainMatch("*\"kind\":[\"example\"]*");

            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.BinaryWithOtherExecutableTargets, OneBinaryManifest);

            binaries.Should().ContainSingle().Which.Should().Be(new RustHelpers.CargoBinary("one-bin", "/ct-target-facts/debug/one-bin"));
        }

        [Fact]
        public void ReadBinaryArtifacts_IgnoresLinesThatAreNotJson()
        {
            var lines = new[] { "   Compiling one-bin v0.1.0", string.Empty, "not { json" }.Concat(CargoJson.OneBinary);

            var binaries = RustHelpers.ReadBinaryArtifacts(lines, OneBinaryManifest);

            binaries.Should().ContainSingle().Which.Name.Should().Be("one-bin");
        }

        [Fact]
        public void ReadBinaryArtifacts_TwoBinaries_ReturnsBoth()
        {
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.TwoBinaries, MultiBinaryManifest);

            binaries.Select(b => b.Name).Should().BeEquivalentTo("multi", "second");
        }

        [Fact]
        public void ReadBinaryArtifacts_SameExecutableTwice_IsReturnedOnce()
        {
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.OneBinary.Concat(CargoJson.OneBinary), OneBinaryManifest);

            binaries.Should().ContainSingle();
        }

        [SkippableFact]
        public void ReadBinaryArtifacts_ManifestPathIgnoresCaseOnWindows()
        {
            Skip.IfNot(OperatingSystem.IsWindows());
            var line = """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Me/App#app@0.1.0","manifest_path":"C:\\Users\\Me\\App\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"app"},"filenames":["C:\\Users\\Me\\App\\target\\debug\\app.exe"],"executable":"C:\\Users\\Me\\App\\target\\debug\\app.exe","fresh":false}""";

            var binaries = RustHelpers.ReadBinaryArtifacts(new[] { line }, @"c:\users\me\app\Cargo.toml");

            binaries.Should().ContainSingle().Which.Path.Should().Be(@"C:\Users\Me\App\target\debug\app.exe");
        }

        [Fact]
        public void SelectBinary_SingleBinary_ReturnsIt()
        {
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.OneBinary, OneBinaryManifest);

            RustHelpers.SelectBinary(binaries, defaultRun: null, OneBinaryManifest)
                .Should().Be("/ct-target-facts/debug/one-bin");
        }

        [Fact]
        public void SelectBinary_TwoBinariesWithDefaultRun_ReturnsTheDefault()
        {
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.TwoBinaries, MultiBinaryManifest);

            RustHelpers.SelectBinary(binaries, defaultRun: "second", MultiBinaryManifest)
                .Should().Be("/ct-target-facts/debug/second");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-binary")]
        public void SelectBinary_TwoBinariesWithoutAMatchingDefaultRun_ThrowsActionableCliException(string defaultRun)
        {
            var binaries = RustHelpers.ReadBinaryArtifacts(CargoJson.TwoBinaries, MultiBinaryManifest);

            var action = () => RustHelpers.SelectBinary(binaries, defaultRun, MultiBinaryManifest);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("more than one binary")
                .And.Contain("multi")
                .And.Contain("second")
                .And.Contain("default-run");
        }

        [Fact]
        public void SelectBinary_NoBinary_ThrowsActionableCliException()
        {
            var action = () => RustHelpers.SelectBinary(Array.Empty<RustHelpers.CargoBinary>(), defaultRun: null, OneBinaryManifest);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("did not build a binary")
                .And.Contain(OneBinaryManifest);
        }

        [Fact]
        public void ReadOtherBinaryArtifacts_ReturnsTheBinariesOfOtherPackages()
        {
            var others = RustHelpers.ReadOtherBinaryArtifacts(CargoJson.OneBinary, "/scratch/facts/Cargo.toml");

            others.Should().ContainSingle().Which.Name.Should().Be("one-bin");
        }

        [Fact]
        public void ReadOtherBinaryArtifacts_LeavesOutThePackageItself()
        {
            RustHelpers.ReadOtherBinaryArtifacts(CargoJson.OneBinary, OneBinaryManifest).Should().BeEmpty();
        }

        [Fact]
        public void ReadOtherBinaryArtifacts_LeavesOutLibrariesAndLinesThatAreNotJson()
        {
            var lines = new[] { "   Compiling helper v0.1.0", "not { json" }.Concat(CargoJson.BinaryWithDependency);

            var others = RustHelpers.ReadOtherBinaryArtifacts(lines, "/scratch/facts/Cargo.toml");

            others.Should().ContainSingle().Which.Name.Should().Be("withdep");
        }

        // A workspace root that is no package builds its members. Cargo did build a binary then, only not for the manifest of the
        // folder, so the text for a package without a binary target would be wrong.
        [Fact]
        public void SelectBinary_NoBinaryForThePackageButOtherPackagesBuiltOne_NamesThemAndSaysWhereToRun()
        {
            const string workspaceManifest = "/scratch/facts/Cargo.toml";
            var own = RustHelpers.ReadBinaryArtifacts(CargoJson.OneBinary, workspaceManifest);
            var others = RustHelpers.ReadOtherBinaryArtifacts(CargoJson.OneBinary, workspaceManifest);

            var action = () => RustHelpers.SelectBinary(own, defaultRun: null, workspaceManifest, others);

            var message = action.Should().Throw<CliException>().Which.Message;
            message.Should().Contain(workspaceManifest).And.Contain("one-bin").And.Contain("other packages");
            message.Should().Contain("in the folder of the package", "the user has to run func where the worker package is");
            message.Should().NotContain("did not build a binary", "cargo did build a binary, for another package");
        }

        [Fact]
        public void SelectBinary_NoBinaryAnywhere_KeepsTheTextForAPackageWithoutABinaryTarget()
        {
            var action = () => RustHelpers.SelectBinary(Array.Empty<RustHelpers.CargoBinary>(), defaultRun: null, OneBinaryManifest, Array.Empty<RustHelpers.CargoBinary>());

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("did not build a binary")
                .And.Contain("src/main.rs");
        }

        [Fact]
        public void ReadDefaultRun_ReadsThePackageOfTheManifest()
        {
            RustHelpers.ReadDefaultRun(CargoJson.Metadata, MultiBinaryManifest).Should().Be("second");
        }

        [Theory]
        [InlineData("/somewhere/else/Cargo.toml")]
        [InlineData("")]
        public void ReadDefaultRun_OtherManifest_ReturnsNull(string manifest)
        {
            RustHelpers.ReadDefaultRun(CargoJson.Metadata, manifest).Should().BeNull();
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{}")]
        public void ReadDefaultRun_UnusableMetadata_ReturnsNull(string metadata)
        {
            RustHelpers.ReadDefaultRun(metadata, MultiBinaryManifest).Should().BeNull();
        }

        [SkipIfCargoNonExistFact]
        public async Task BuildProject_ValidProject_CopiesTheBinaryToItsOwnFolder()
        {
            using var project = new CargoProject("one-bin", "fn main() { println!(\"hello from one-bin\"); }\n");

            await RustHelpers.BuildProject(project.Path);

            var binary = Path.Combine(project.Path, RustHelpers.LocalBinaryPath);
            File.Exists(binary).Should().BeTrue("BuildProject should produce the worker binary in target/func");
            (await RunAsync(binary)).Should().Be("hello from one-bin");
        }

        // Nothing that 'func start' builds may be a file that 'func pack --no-build' or 'func publish --no-build' can pack.
        [SkipIfCargoNonExistFact]
        public async Task BuildProject_ValidProject_LeavesNothingInBinForPack()
        {
            using var project = new CargoProject("one-bin", "fn main() {}\n");

            await RustHelpers.BuildProject(project.Path);

            Directory.Exists(Path.Combine(project.Path, "bin")).Should().BeFalse("only 'func pack' writes in bin");
            RustHelpers.GetPackFiles(project.Path).Where(File.Exists).Should().BeEmpty("no file of the package exists after 'func start' built");
        }

        // The accident that the separate folder prevents, as far as a unit test can show it. On an x86_64 Linux machine the debug binary of 'func start'
        // is a valid x86_64 ELF file, so the check that 'func pack --no-build' makes accepted it as the binary of the package.
        [SkipIfCargoNonExistFact]
        public async Task BuildProject_ThenTheCheckOfPackNoBuild_FindsNoBinaryToPack()
        {
            using var project = new CargoProject("one-bin", "fn main() {}\n");
            await RustHelpers.BuildProject(project.Path);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(project.Path);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("Could not find a built Rust binary");
        }

        [SkipIfCargoNonExistFact]
        public async Task BuildProject_RunTwice_ReplacesTheBinary()
        {
            using var project = new CargoProject("one-bin", "fn main() { println!(\"first\"); }\n");
            await RustHelpers.BuildProject(project.Path);

            File.WriteAllText(Path.Combine(project.Path, "src", "main.rs"), "fn main() { println!(\"second\"); }\n");
            await RustHelpers.BuildProject(project.Path);

            (await RunAsync(Path.Combine(project.Path, RustHelpers.LocalBinaryPath))).Should().Be("second");
        }

        [SkipIfCargoNonExistFact]
        public async Task BuildProject_InvalidProject_ThrowsCliException()
        {
            using var project = new CargoProject("broken", "fn main() { let x: i32 = \"no\"; }\n");

            Func<Task> act = () => RustHelpers.BuildProject(project.Path);

            var ex = await Assert.ThrowsAsync<CliException>(act);
            ex.Message.Should().Contain("Rust build failed");
            File.Exists(Path.Combine(project.Path, RustHelpers.LocalBinaryPath)).Should().BeFalse();
        }

        [SkipIfCargoNonExistFact]
        public async Task BuildProject_TwoBinariesAndNoDefaultRun_ThrowsActionableCliException()
        {
            using var project = new CargoProject("multi", "fn main() {}\n");
            Directory.CreateDirectory(Path.Combine(project.Path, "src", "bin"));
            File.WriteAllText(Path.Combine(project.Path, "src", "bin", "second.rs"), "fn main() {}\n");

            Func<Task> act = () => RustHelpers.BuildProject(project.Path);

            var ex = await Assert.ThrowsAsync<CliException>(act);
            ex.Message.Should().Contain("more than one binary").And.Contain("default-run");
        }

        [SkipIfCargoNonExistFact]
        public async Task BuildProject_TwoBinariesWithDefaultRun_CopiesTheDefaultOne()
        {
            using var project = new CargoProject("multi", "fn main() { println!(\"main bin\"); }\n", extraPackageLines: "default-run = \"second\"\n");
            Directory.CreateDirectory(Path.Combine(project.Path, "src", "bin"));
            File.WriteAllText(Path.Combine(project.Path, "src", "bin", "second.rs"), "fn main() { println!(\"second bin\"); }\n");

            await RustHelpers.BuildProject(project.Path);

            (await RunAsync(Path.Combine(project.Path, RustHelpers.LocalBinaryPath))).Should().Be("second bin");
        }

        [SkipIfCargoNonExistFact]
        public async Task BuildProject_AWorkspaceRootThatIsNoPackage_SaysWhichPackageBuiltABinary()
        {
            // The root manifest only lists members. Cargo builds the member, which compiles, but the root has no binary.
            var root = Path.Combine(Path.GetTempPath(), "func-rust-virtual-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[workspace]\nmembers = [\"app\"]\nresolver = \"3\"\n");
                var appDir = Path.Combine(root, "app");
                Directory.CreateDirectory(Path.Combine(appDir, "src"));
                File.WriteAllText(Path.Combine(appDir, "Cargo.toml"), "[package]\nname = \"member-app\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
                File.WriteAllText(Path.Combine(appDir, "src", "main.rs"), "fn main() {}\n");

                Func<Task> act = () => RustHelpers.BuildProject(root);

                var ex = await Assert.ThrowsAsync<CliException>(act);
                ex.Message.Should().Contain("member-app").And.Contain("other packages").And.NotContain("did not build a binary");
                File.Exists(Path.Combine(root, RustHelpers.LocalBinaryPath)).Should().BeFalse();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [SkipIfCargoNonExistFact]
        public async Task BuildProject_PackageInsideAWorkspace_FindsTheBinaryOutsideTheProjectFolder()
        {
            // Cargo writes a workspace member's output to the target folder of the workspace root,
            // which is above the project, so the path must come from cargo and not from a guess.
            var root = Path.Combine(Path.GetTempPath(), "func-rust-workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[workspace]\nmembers = [\"app\"]\nresolver = \"3\"\n");
                var appDir = Path.Combine(root, "app");
                Directory.CreateDirectory(Path.Combine(appDir, "src"));
                File.WriteAllText(Path.Combine(appDir, "Cargo.toml"), "[package]\nname = \"member-app\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
                File.WriteAllText(Path.Combine(appDir, "src", "main.rs"), "fn main() { println!(\"from a member\"); }\n");

                await RustHelpers.BuildProject(appDir);

                // Cargo's own output is in the target folder of the workspace root. Only the binary of 'func start' is in the folder of the
                // package, in target/func, and it is found through the path that cargo printed.
                Directory.Exists(Path.Combine(appDir, "target", "debug")).Should().BeFalse("the workspace root holds the target folder of cargo");
                (await RunAsync(Path.Combine(appDir, RustHelpers.LocalBinaryPath))).Should().Be("from a member");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static async Task<string> RunAsync(string executable)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output.Trim();
        }

        // Records which of the two steps 'func start' ran, and in which folder. The probe returns the version that the test gives it.
        private sealed class StartSteps
        {
            private readonly WorkerLanguageVersionInfo _version;

            public StartSteps(WorkerLanguageVersionInfo version)
            {
                _version = version;
            }

            public List<string> Calls { get; } = new List<string>();

            public Task<WorkerLanguageVersionInfo> GetCargoVersion(string folder)
            {
                Calls.Add($"toolchain probe in {folder}");
                return Task.FromResult(_version);
            }

            public Task Build(string folder)
            {
                Calls.Add($"build in {folder}");
                return Task.CompletedTask;
            }
        }

        private sealed class StartFolder : IDisposable
        {
            public StartFolder(bool withBinary)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-start-" + Guid.NewGuid().ToString("N"));
                var binary = System.IO.Path.Combine(Path, RustHelpers.LocalBinaryPath);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(binary));
                if (withBinary)
                {
                    File.WriteAllText(binary, "stub");
                }
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }

        private sealed class CargoProject : IDisposable
        {
            public CargoProject(string packageName, string mainRs, string extraPackageLines = "")
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-build-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(System.IO.Path.Combine(Path, "src"));
                File.WriteAllText(
                    System.IO.Path.Combine(Path, "Cargo.toml"),
                    $"[package]\nname = \"{packageName}\"\nversion = \"0.1.0\"\nedition = \"2024\"\n{extraPackageLines}");
                File.WriteAllText(System.IO.Path.Combine(Path, "src", "main.rs"), mainRs);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a scanner may still hold a handle on a freshly built file.
                }
            }
        }

        private static class CargoJson
        {
            public static readonly string[] OneBinary =
            [
                """{"reason":"compiler-artifact","package_id":"path+file:///scratch/facts/one#one-bin@0.1.0","manifest_path":"/scratch/facts/one/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"one-bin","src_path":"/scratch/facts/one/src/main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["/ct-target-facts/debug/one-bin"],"executable":"/ct-target-facts/debug/one-bin","fresh":true}""",
                """{"reason":"build-finished","success":true}""",
            ];

            // Not captured from cargo. It is the message of the binary above with the target kind changed, once to a build script and
            // once to an example. Cargo reports no executable for a build script, and one for an example only when examples are built,
            // so the check of the kind is what keeps such a target out if cargo ever reports one.
            public static readonly string[] BinaryWithOtherExecutableTargets =
            [
                OneBinary[0].Replace("\"kind\":[\"bin\"]", "\"kind\":[\"custom-build\"]").Replace("/ct-target-facts/debug/one-bin", "/ct-target-facts/debug/build/one-bin/build-script-build"),
                OneBinary[0].Replace("\"kind\":[\"bin\"]", "\"kind\":[\"example\"]").Replace("/ct-target-facts/debug/one-bin", "/ct-target-facts/debug/examples/demo"),
                .. OneBinary,
            ];

            public static readonly string[] TwoBinaries =
            [
                """{"reason":"compiler-artifact","package_id":"path+file:///scratch/facts/multi#0.1.0","manifest_path":"/scratch/facts/multi/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"second","src_path":"/scratch/facts/multi/src/bin/second.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["/ct-target-facts/debug/second"],"executable":"/ct-target-facts/debug/second","fresh":true}""",
                """{"reason":"compiler-artifact","package_id":"path+file:///scratch/facts/multi#0.1.0","manifest_path":"/scratch/facts/multi/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"multi","src_path":"/scratch/facts/multi/src/main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["/ct-target-facts/debug/multi"],"executable":"/ct-target-facts/debug/multi","fresh":true}""",
                """{"reason":"build-finished","success":true}""",
            ];

            public static readonly string[] BinaryWithDependency =
            [
                """{"reason":"compiler-artifact","package_id":"path+file:///scratch/facts/lib2#helper@0.1.0","manifest_path":"/scratch/facts/lib2/Cargo.toml","target":{"kind":["lib"],"crate_types":["lib"],"name":"helper","src_path":"/scratch/facts/lib2/src/lib.rs","edition":"2024","doc":true,"doctest":true,"test":true},"profile":{"opt_level":"3","debuginfo":0,"debug_assertions":false,"overflow_checks":false,"test":false},"features":[],"filenames":["/ct-target-facts/x86_64-unknown-linux-gnu/release/deps/libhelper-cc6fb1af73d7fd04.rlib","/ct-target-facts/x86_64-unknown-linux-gnu/release/deps/libhelper-cc6fb1af73d7fd04.rmeta"],"executable":null,"fresh":true}""",
                """{"reason":"compiler-artifact","package_id":"path+file:///scratch/facts/dep#withdep@0.1.0","manifest_path":"/scratch/facts/dep/Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"withdep","src_path":"/scratch/facts/dep/src/main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"3","debuginfo":0,"debug_assertions":false,"overflow_checks":false,"test":false},"features":[],"filenames":["/ct-target-facts/x86_64-unknown-linux-gnu/release/withdep"],"executable":"/ct-target-facts/x86_64-unknown-linux-gnu/release/withdep","fresh":true}""",
                """{"reason":"build-finished","success":true}""",
            ];

            public static readonly string Metadata = """{"packages":[{"name":"multi","version":"0.1.0","id":"path+file:///scratch/facts/multi#0.1.0","license":null,"license_file":null,"description":null,"source":null,"dependencies":[],"targets":[{"kind":["bin"],"crate_types":["bin"],"name":"multi","src_path":"/scratch/facts/multi/src/main.rs","edition":"2024","doc":true,"doctest":false,"test":true},{"kind":["bin"],"crate_types":["bin"],"name":"second","src_path":"/scratch/facts/multi/src/bin/second.rs","edition":"2024","doc":true,"doctest":false,"test":true}],"features":{},"manifest_path":"/scratch/facts/multi/Cargo.toml","metadata":null,"publish":null,"authors":[],"categories":[],"keywords":[],"readme":null,"repository":null,"homepage":null,"documentation":null,"edition":"2024","links":null,"default_run":"second","rust_version":null}],"workspace_members":["path+file:///scratch/facts/multi#0.1.0"],"workspace_default_members":["path+file:///scratch/facts/multi#0.1.0"],"resolve":null,"target_directory":"/ct-target-facts","version":1,"workspace_root":"/scratch/facts/multi","metadata":null}""";
        }
    }

    // Skips a test that runs the real cargo when cargo is missing or older than the minimum that Core Tools checks.
    // The tests build with edition 2024, and BuildForLinux and PrepareForPublish refuse anything below 1.90, so an
    // older cargo that works would make them fail and not skip.
    public sealed class SkipIfCargoNonExistFact : FactAttribute
    {
        private static readonly Version _minimumCargoVersion = new Version(1, 90);

        public SkipIfCargoNonExistFact()
        {
            if (!CheckIfCargoWorks())
            {
                Skip = "cargo 1.90 or later is not installed or not functional";
            }
        }

        internal static bool CheckIfCargoWorks()
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cargo.exe" : "cargo",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                return process.ExitCode == 0
                    && Version.TryParse(RustHelpers.ParseCargoVersion(output), out var version)
                    && version >= _minimumCargoVersion;
            }
            catch
            {
                return false;
            }
        }
    }
}
