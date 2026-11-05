// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    [Collection("NativeWorkerRuntimeTests")]
    public class RustHelperPackTests
    {
        [Fact]
        public void LinuxTarget_IsX86_64UnknownLinuxGnu()
        {
            RustHelpers.LinuxTarget.Should().Be("x86_64-unknown-linux-gnu");
        }

        [Fact]
        public void AssertLinuxAmd64Binary_BinaryMissing_ThrowsActionableCliException()
        {
            using var dir = new TempDirectory();

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("Could not find a built Rust binary")
                .And.Contain("--no-build")
                .And.Contain("cargo build --release --target x86_64-unknown-linux-gnu")
                .And.Contain(Path.Combine("bin", "app"));
        }

        [Fact]
        public void AssertLinuxAmd64Binary_TooSmall_Throws()
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, new byte[10]);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("too small to be an ELF binary");
        }

        // A symbolic link that points nowhere still exists as a file. The user should hear that there is no binary, and not
        // get the raw error of the file system.
        [SkippableFact]
        public void AssertLinuxAmd64Binary_ADanglingSymbolicLink_SaysThereIsNoBuiltBinary()
        {
            using var dir = new TempDirectory();
            SymbolicLinkTestHelper.CreateOrSkip(dir.BinaryPath, Path.Combine(dir.Path, "gone"));

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("Could not find a built Rust binary")
                .And.Contain("cargo build --release --target x86_64-unknown-linux-gnu");
        }

        [SkippableFact]
        public void AssertLinuxAmd64Binary_ASymbolicLinkToAValidBinary_DoesNotThrow()
        {
            using var dir = new TempDirectory();
            var target = Path.Combine(dir.Path, "built");
            File.WriteAllBytes(target, BuildElfHeader(machine: 0x3E));
            SymbolicLinkTestHelper.CreateOrSkip(dir.BinaryPath, target);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().NotThrow("'func pack' follows the link and packs the binary behind it");
        }

        [Fact]
        public void AssertLinuxAmd64Binary_NotElf_Throws()
        {
            using var dir = new TempDirectory();

            // 32 bytes of non-ELF content so the size check passes and the magic check fails.
            File.WriteAllBytes(dir.BinaryPath, new byte[32]);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("not an ELF binary")
                .And.Contain("Rust")
                .And.Contain("cargo build --release --target x86_64-unknown-linux-gnu");
        }

        [Fact]
        public void AssertLinuxAmd64Binary_WindowsExecutable_Throws()
        {
            using var dir = new TempDirectory();

            // The MZ header of a Windows executable, which is what 'cargo build' makes on Windows without a target.
            var header = new byte[64];
            header[0] = (byte)'M';
            header[1] = (byte)'Z';
            File.WriteAllBytes(dir.BinaryPath, header);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("not an ELF binary");
        }

        [Fact]
        public void AssertLinuxAmd64Binary_ElfButWrongArch_Throws()
        {
            using var dir = new TempDirectory();

            // EM_AARCH64 (0xB7) instead of EM_X86_64.
            File.WriteAllBytes(dir.BinaryPath, BuildElfHeader(machine: 0xB7));

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>()
                .Which.Message.Should().Contain("0x00B7")
                .And.Contain("not x86_64");
        }

        [Fact]
        public void AssertLinuxAmd64Binary_ValidHeader_DoesNotThrow()
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, BuildElfHeader(machine: 0x3E));

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().NotThrow();
        }

        [Fact]
        public void AssertLinuxAmd64Binary_Elf32_Throws()
        {
            using var dir = new TempDirectory();

            // EI_CLASS (offset 4) set to ELFCLASS32 is rejected even when e_machine says x86_64.
            var header = BuildElfHeader(machine: 0x3E);
            header[4] = 1;
            File.WriteAllBytes(dir.BinaryPath, header);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("not a 64-bit little-endian ELF binary");
        }

        // The host runs x86_64, which is little endian. A header with the big-endian data encoding is refused
        // even when everything else in it looks right.
        [Fact]
        public void AssertLinuxAmd64Binary_BigEndian_Throws()
        {
            using var dir = new TempDirectory();
            var header = BuildElfHeader(machine: 0x3E);
            header[5] = 2;
            File.WriteAllBytes(dir.BinaryPath, header);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("not a 64-bit little-endian ELF binary");
        }

        // The magic is four bytes. A file that has one of them wrong is not ELF, whichever it is, and the rest of the header
        // here is valid, so that only the magic can be the reason.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void AssertLinuxAmd64Binary_OneByteOfTheMagicIsWrong_Throws(int index)
        {
            using var dir = new TempDirectory();
            var header = BuildElfHeader(machine: 0x3E);
            header[index] = 0;
            File.WriteAllBytes(dir.BinaryPath, header);

            var action = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("not an ELF binary");
        }

        // The version check is the first step of the build for Linux, before cargo builds anything. The probe is a stand-in here,
        // which makes the version old or missing without an old cargo on the machine.
        [Fact]
        public async Task BuildForLinux_RustIsTooOld_StopsBeforeTheBuild()
        {
            using var dir = new TempDirectory();
            var folders = new List<string>();

            Func<Task> act = () => RustHelpers.BuildForLinux(dir.Path, folder => Probe(folders, folder, "1.80.0"));

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Rust version 1.80.0 is not supported");
            folders.Should().Equal(dir.Path);
        }

        [Fact]
        public async Task BuildForLinux_NoToolchain_StopsBeforeTheBuild()
        {
            using var dir = new TempDirectory();
            var folders = new List<string>();

            Func<Task> act = () => RustHelpers.BuildForLinux(dir.Path, folder => Probe(folders, folder, version: null));

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Could not find a Rust toolchain");
            folders.Should().Equal(dir.Path);
        }

        // Records the folder that the probe ran in, and answers with the version that the test gives it.
        private static Task<WorkerLanguageVersionInfo> Probe(List<string> folders, string folder, string version)
        {
            folders.Add(folder);
            return Task.FromResult(version == null ? null : new WorkerLanguageVersionInfo(WorkerRuntime.Rust, version, "cargo"));
        }

        [Fact]
        public void GetPackFiles_WhenHostJsonExists_ReturnsHostJsonAndAppBinary()
        {
            using var dir = new TempDirectory();
            File.WriteAllText(Path.Combine(dir.Path, "host.json"), "{}");

            var files = RustHelpers.GetPackFiles(dir.Path).ToArray();

            files.Should().HaveCount(2);
            files.Should().Contain(Path.Combine(dir.Path, "host.json"));
            files.Should().Contain(Path.Combine(dir.Path, "bin", "app"));
        }

        [Fact]
        public void GetPackFiles_WhenHostJsonMissing_ReturnsOnlyAppBinary()
        {
            using var dir = new TempDirectory();

            var files = RustHelpers.GetPackFiles(dir.Path).ToArray();

            files.Should().ContainSingle().Which.Should().Be(Path.Combine(dir.Path, "bin", "app"));
        }

        [Fact]
        public void GetPackFiles_NeverIncludesTheLocalExe()
        {
            using var dir = new TempDirectory();

            // The binary that 'func start' runs on Windows is an .exe. It never goes into the package.
            RustHelpers.GetPackFiles(dir.Path).Should().NotContain(file => file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        // The package holds host.json and bin/app, and nothing else, so the binary that 'func start' runs is never in it.
        [Fact]
        public void GetPackFiles_NeverIncludesTheBinaryThatFuncStartRuns()
        {
            using var dir = new TempDirectory();
            var startBinary = Path.Combine(dir.Path, RustHelpers.LocalBinaryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(startBinary));
            File.WriteAllText(startBinary, "x");

            RustHelpers.GetPackFiles(dir.Path).Should().NotContain(startBinary);
        }

        // The cargo output below is real, captured from cargo 1.90.0 on x86_64 Linux. The missing
        // target text is for aarch64-unknown-linux-gnu because on that machine the
        // x86_64 target is always installed. Cargo words it the same for any target.
        [Theory]
        [InlineData("   Compiling tgt v0.1.0 (/scratch/facts/tgt)\nerror[E0463]: can't find crate for `std`\n  |\n  = note: the `aarch64-unknown-linux-gnu` target may not be installed\n  = help: consider downloading the target with `rustup target add aarch64-unknown-linux-gnu`\n\nFor more information about this error, try `rustc --explain E0463`.\nerror: could not compile `tgt` (bin \"tgt\") due to 1 previous error\n")]
        [InlineData("   Compiling tgt v0.1.0 (/scratch/facts/tgt)\nerror: linker `ct-no-such-linker` not found\n  |\n  = note: No such file or directory (os error 2)\n\nerror: could not compile `tgt` (bin \"tgt\") due to 1 previous error\n")]
        public void LooksLikeMissingLinuxToolchain_RealCargoOutput_IsDetected(string stderr)
        {
            RustHelpers.LooksLikeMissingLinuxToolchain(stderr).Should().BeTrue();
        }

        [Theory]
        [InlineData("error[E0308]: mismatched types\n --> src/main.rs:1:31\n  |\n1 | fn main() { let x: i32 = \"no\"; }\n  |                    ---   ^^^^ expected `i32`, found `&str`\n")]
        [InlineData("")]
        [InlineData(null)]
        public void LooksLikeMissingLinuxToolchain_OrdinaryFailure_IsNotDetected(string stderr)
        {
            RustHelpers.LooksLikeMissingLinuxToolchain(stderr).Should().BeFalse();
        }

        [SkipIfNotLinuxX64CargoFact]
        public async Task BuildForLinux_ValidProject_ProducesLinuxAmd64Binary()
        {
            using var dir = new TempDirectory();
            WriteCargoProject(dir.Path, "linux-app", "fn main() { println!(\"hello from linux\"); }\n");

            await RustHelpers.BuildForLinux(dir.Path);

            File.Exists(dir.BinaryPath).Should().BeTrue("BuildForLinux should produce the Linux worker binary in bin/app");
            File.Exists(Path.Combine(dir.Path, "bin", "app.exe")).Should().BeFalse();

            // The folder is wherever cargo puts it. A developer can move it with CARGO_TARGET_DIR or the cargo config.
            var targetDirectory = await CargoTargetDirectoryAsync(dir.Path);
            Directory.Exists(Path.Combine(targetDirectory, RustHelpers.LinuxTarget, "release")).Should().BeTrue("the Linux binary is a release build for the Linux target");

            // The produced binary is itself an x86_64 Linux ELF, so AssertLinuxAmd64Binary accepts it.
            var assert = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);
            assert.Should().NotThrow();
        }

        [SkipIfNotLinuxX64CargoFact]
        public async Task BuildForLinux_InvalidProject_ThrowsCliException()
        {
            using var dir = new TempDirectory();
            WriteCargoProject(dir.Path, "broken", "fn main() { let x: i32 = \"no\"; }\n");

            Func<Task> act = () => RustHelpers.BuildForLinux(dir.Path);

            var ex = await Assert.ThrowsAsync<CliException>(act);
            ex.Message.Should().Contain("Rust build for x86_64-unknown-linux-gnu failed");
            File.Exists(dir.BinaryPath).Should().BeFalse();
        }

        [Fact]
        public async Task PrepareForPublish_NoBuild_ExistingLinuxBinary_Succeeds()
        {
            using var dir = new TempDirectory();
            File.WriteAllText(Path.Combine(dir.Path, "Cargo.toml"), "[package]\nname = \"x\"\n");
            File.WriteAllBytes(dir.BinaryPath, BuildElfHeader(machine: 0x3E));

            Func<Task> act = () => RustHelpers.PrepareForPublish(dir.Path, noBuild: true);

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task PrepareForPublish_NoBuild_MissingBinary_Throws()
        {
            using var dir = new TempDirectory();
            File.WriteAllText(Path.Combine(dir.Path, "Cargo.toml"), "[package]\nname = \"x\"\n");

            Func<Task> act = () => RustHelpers.PrepareForPublish(dir.Path, noBuild: true);

            (await act.Should().ThrowAsync<CliException>())
                .Which.Message.Should().Contain("Could not find a built Rust binary");
        }

        [Fact]
        public async Task PrepareForPublish_NoBuild_BinaryForAnotherPlatform_Throws()
        {
            using var dir = new TempDirectory();
            File.WriteAllText(Path.Combine(dir.Path, "Cargo.toml"), "[package]\nname = \"x\"\n");

            // A Windows executable must not be uploaded to a Linux host.
            File.WriteAllBytes(dir.BinaryPath, new byte[] { (byte)'M', (byte)'Z', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

            Func<Task> act = () => RustHelpers.PrepareForPublish(dir.Path, noBuild: true);

            (await act.Should().ThrowAsync<CliException>())
                .Which.Message.Should().Contain("not an ELF binary");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task PrepareForPublish_WithoutCargoToml_Throws(bool noBuild)
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, BuildElfHeader(machine: 0x3E));

            Func<Task> act = () => RustHelpers.PrepareForPublish(dir.Path, noBuild);

            (await act.Should().ThrowAsync<CliException>())
                .Which.Message.Should().Contain("Cargo.toml");
        }

        [SkipIfNotLinuxX64CargoFact]
        public async Task PrepareForPublish_Build_ProducesTheLinuxBinary()
        {
            using var dir = new TempDirectory();
            WriteCargoProject(dir.Path, "publish-app", "fn main() { println!(\"hello\"); }\n");

            await RustHelpers.PrepareForPublish(dir.Path, noBuild: false);

            File.Exists(dir.BinaryPath).Should().BeTrue();
            var assert = () => RustHelpers.AssertLinuxAmd64Binary(dir.Path);
            assert.Should().NotThrow();
        }

        private static async Task<string> CargoTargetDirectoryAsync(string projectPath)
        {
            var output = new StringBuilder();
            var exe = new Executable(RustHelpers.CargoExecutable, "metadata --no-deps --format-version 1", workingDirectory: projectPath);
            var exitCode = await exe.RunAsync(line => output.AppendLine(line), _ => { });
            exitCode.Should().Be(0);

            using var metadata = JsonDocument.Parse(output.ToString());
            return metadata.RootElement.GetProperty("target_directory").GetString();
        }

        private static void WriteCargoProject(string path, string packageName, string mainRs)
        {
            Directory.CreateDirectory(Path.Combine(path, "src"));
            File.WriteAllText(Path.Combine(path, "Cargo.toml"), $"[package]\nname = \"{packageName}\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
            File.WriteAllText(Path.Combine(path, "src", "main.rs"), mainRs);
        }

        // A minimal 20-byte ELF identification region with EI_CLASS=64-bit, EI_DATA=little-endian and an
        // arbitrary e_machine. AssertLinuxAmd64Binary reads only these 20 bytes.
        private static byte[] BuildElfHeader(ushort machine)
        {
            var header = new byte[20];
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = 2;
            header[5] = 1;
            header[18] = (byte)(machine & 0xFF);
            header[19] = (byte)(machine >> 8);
            return header;
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-pack-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(System.IO.Path.Combine(Path, "bin"));
            }

            public string Path { get; }

            public string BinaryPath => System.IO.Path.Combine(Path, "bin", "app");

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
    }

    // Only an x64 Linux host builds for x86_64-unknown-linux-gnu without setup. Other hosts need the target
    // and a linker for it first, which is the cross build that Core Tools only gives a hint for.
    public sealed class SkipIfNotLinuxX64CargoFact : FactAttribute
    {
        public SkipIfNotLinuxX64CargoFact()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
            {
                Skip = "Building for x86_64-unknown-linux-gnu without extra setup needs an x64 Linux host.";
            }
            else if (!SkipIfCargoNonExistFact.CheckIfCargoWorks())
            {
                Skip = "cargo is not installed or not functional";
            }
        }
    }
}
