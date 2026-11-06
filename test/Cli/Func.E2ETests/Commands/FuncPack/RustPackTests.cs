// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using AwesomeAssertions;
using Azure.Functions.Cli.E2ETests.Traits;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.TestFramework.Assertions;
using Azure.Functions.Cli.TestFramework.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Azure.Functions.Cli.E2ETests.Commands.FuncPack
{
    [Trait(WorkerRuntimeTraits.WorkerRuntime, WorkerRuntimeTraits.Rust)]
    public class RustPackTests(ITestOutputHelper log) : BaseE2ETests(log)
    {
        // Only an x64 Linux host builds for x86_64-unknown-linux-gnu without setup. Other hosts need the target
        // and a linker for it first, so the tests that build skip there.
        private const string LinuxBuildSkipReason = "Building for x86_64-unknown-linux-gnu without extra setup needs an x64 Linux host.";

        // A worker that needs no crates. Packing does not run it, so it only has to compile.
        private const string ProbeManifest = "[package]\nname = \"probe\"\nversion = \"0.1.0\"\nedition = \"2024\"\n";
        private const string ProbeSource = "fn main() {\n    println!(\"probe\");\n}\n";

        private static bool IsLinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64;

        [SkippableFact]
        public async Task Pack_Rust_BuildsTheLinuxBinaryIntoTheZip()
        {
            Skip.IfNot(IsLinuxX64, LinuxBuildSkipReason);
            var testName = nameof(Pack_Rust_BuildsTheLinuxBinaryIntoTheZip);
            await InitRustAppAsync(testName);

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute([]);

            result.Should().ExitWith(0);
            result.Should().HaveStdOutContaining(Utilities.RustPreviewWarning);
            result.Should().HaveStdOutContaining("Building Rust worker binary 'bin/app' for x86_64-unknown-linux-gnu");
            result.Should().HaveStdOutContaining("Creating a new package");

            using var archive = ZipFile.OpenRead(SingleZip());
            archive.Entries.Select(e => e.FullName).OrderBy(n => n).Should().Equal("app", "host.json");
            AssertLinuxAmd64Executable(archive.GetEntry("app")!);
        }

        [SkippableFact]
        public async Task Pack_Rust_NoBuild_PacksTheBinaryBuiltForLinux()
        {
            Skip.IfNot(IsLinuxX64, LinuxBuildSkipReason);
            var testName = nameof(Pack_Rust_NoBuild_PacksTheBinaryBuiltForLinux);
            await InitRustAppAsync(testName);
            await RustHelpers.BuildForLinux(WorkingDirectory);
            var built = File.ReadAllBytes(Path.Combine(WorkingDirectory, "bin", "app"));

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(0);
            result.Should().HaveStdOutContaining("Skipping build event for functions project (--no-build).");
            result.Should().NotHaveStdOutContaining("Building Rust worker binary");
            result.Should().NotHaveStdOutContaining("debug information");

            using var archive = ZipFile.OpenRead(SingleZip());
            archive.Entries.Select(e => e.FullName).OrderBy(n => n).Should().Equal("app", "host.json");
            using var entryStream = archive.GetEntry("app")!.Open();
            using var packed = new MemoryStream();
            await entryStream.CopyToAsync(packed);
            packed.ToArray().Should().Equal(built, "the zip holds the binary that was built, byte for byte");
        }

        [Fact]
        public async Task Pack_Rust_NoBuild_PacksAnExistingLinuxBinary()
        {
            var testName = nameof(Pack_Rust_NoBuild_PacksAnExistingLinuxBinary);
            await InitRustAppAsync(testName);

            // Only the 20 byte ELF header matters to 'func pack --no-build', which does not run the binary.
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, "bin"));
            File.WriteAllBytes(Path.Combine(WorkingDirectory, "bin", "app"), ElfHeader(machine: 0x3E));

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(0);
            result.Should().HaveStdOutContaining("Skipping build event for functions project (--no-build).");

            using var archive = ZipFile.OpenRead(SingleZip());
            archive.Entries.Select(e => e.FullName).OrderBy(n => n).Should().Equal("app", "host.json");
            AssertLinuxAmd64Executable(archive.GetEntry("app")!);
        }

        // Core Tools cannot read a profile from a binary, so it warns when the binary has the sections of a debug build and still packs it.
        [Fact]
        public async Task Pack_Rust_NoBuild_WithDebugInformation_WarnsAndStillPacks()
        {
            var testName = nameof(Pack_Rust_NoBuild_WithDebugInformation_WarnsAndStillPacks);
            await InitRustAppAsync(testName);
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, "bin"));
            File.WriteAllBytes(Path.Combine(WorkingDirectory, "bin", "app"), ElfWithSections(".text", ".debug_info"));

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(0);
            result.Should().HaveStdOutContaining($"'{Path.Combine("bin", "app")}' has debug information");

            using var archive = ZipFile.OpenRead(SingleZip());
            archive.Entries.Select(e => e.FullName).OrderBy(n => n).Should().Equal("app", "host.json");
        }

        [Fact]
        public async Task Pack_Rust_NoBuild_WithoutDebugInformation_DoesNotWarn()
        {
            var testName = nameof(Pack_Rust_NoBuild_WithoutDebugInformation_DoesNotWarn);
            await InitRustAppAsync(testName);
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, "bin"));
            File.WriteAllBytes(Path.Combine(WorkingDirectory, "bin", "app"), ElfWithSections(".text", ".rodata", ".symtab"));

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(0);
            result.Should().NotHaveStdOutContaining("debug information");
        }

        // 'func start' builds a debug binary into bin/app, the file that 'func pack --no-build' reads. The two warn and still pack it.
        [SkippableFact]
        public async Task Pack_Rust_NoBuild_AfterTheBuildOfFuncStart_WarnsAndStillPacks()
        {
            Skip.IfNot(IsLinuxX64, LinuxBuildSkipReason);
            var testName = nameof(Pack_Rust_NoBuild_AfterTheBuildOfFuncStart_WarnsAndStillPacks);
            await InitRustAppAsync(testName);
            await RustHelpers.BuildProject(WorkingDirectory);
            File.Exists(Path.Combine(WorkingDirectory, "bin", "app")).Should().BeTrue("the build of 'func start' writes bin/app");

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(0);
            result.Should().HaveStdOutContaining($"'{Path.Combine("bin", "app")}' has debug information");

            using var archive = ZipFile.OpenRead(SingleZip());
            archive.Entries.Select(e => e.FullName).OrderBy(n => n).Should().Equal("app", "host.json");
        }

        // 'func pack' without the flag builds a release binary and writes it to bin/app, over the debug binary of 'func start'.
        [SkippableFact]
        public async Task Pack_Rust_AfterTheBuildOfFuncStart_ReplacesTheDebugBinaryWithAReleaseBuild()
        {
            Skip.IfNot(IsLinuxX64, LinuxBuildSkipReason);
            var testName = nameof(Pack_Rust_AfterTheBuildOfFuncStart_ReplacesTheDebugBinaryWithAReleaseBuild);
            await InitRustAppAsync(testName);
            await RustHelpers.BuildProject(WorkingDirectory);
            var binary = Path.Combine(WorkingDirectory, "bin", "app");
            RustHelpers.HasDebugInformation(binary).Should().BeTrue("the build of 'func start' is a debug build");

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute([]);

            result.Should().ExitWith(0);
            result.Should().NotHaveStdOutContaining("debug information");
            RustHelpers.HasDebugInformation(binary).Should().BeFalse("the release build replaced the debug binary");
        }

        [Fact]
        public async Task Pack_Rust_NoBuild_WithoutABinary_Fails()
        {
            var testName = nameof(Pack_Rust_NoBuild_WithoutABinary_Fails);
            await InitRustAppAsync(testName);

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(1);
            result.Should().HaveStdErrContaining("Could not find a built Rust binary");
            Directory.GetFiles(WorkingDirectory, "*.zip").Should().BeEmpty();
        }

        [Fact]
        public async Task Pack_Rust_NoBuild_WithAWindowsExecutable_Fails()
        {
            var testName = nameof(Pack_Rust_NoBuild_WithAWindowsExecutable_Fails);
            await InitRustAppAsync(testName);
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, "bin"));
            File.WriteAllBytes(Path.Combine(WorkingDirectory, "bin", "app"), new byte[] { (byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0xB8, 0, 0, 0, 0, 0, 0, 0 });

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(1);
            result.Should().HaveStdErrContaining("not an ELF binary");
            Directory.GetFiles(WorkingDirectory, "*.zip").Should().BeEmpty();
        }

        [Fact]
        public async Task Pack_Rust_WithoutCargoToml_Fails()
        {
            var testName = nameof(Pack_Rust_WithoutCargoToml_Fails);
            await InitRustAppAsync(testName);
            File.Delete(Path.Combine(WorkingDirectory, "Cargo.toml"));

            var result = new FuncPackCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            result.Should().ExitWith(1);
            result.Should().HaveStdErrContaining("Cargo.toml");
        }

        private static byte[] ElfHeader(ushort machine)
        {
            var header = new byte[64];
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

        // An ELF image with a table of sections that have the given names and no data. 'func pack --no-build' reads the header and the names.
        private static byte[] ElfWithSections(params string[] names)
        {
            var strings = new List<byte> { 0 };
            var nameOffsets = new List<uint>();
            foreach (var name in names.Append(".shstrtab"))
            {
                nameOffsets.Add((uint)strings.Count);
                strings.AddRange(Encoding.ASCII.GetBytes(name));
                strings.Add(0);
            }

            var tableOffset = (64 + strings.Count + 7) / 8 * 8;
            var image = new byte[tableOffset + ((names.Length + 2) * 64)];
            ElfHeader(machine: 0x3E).CopyTo(image, 0);
            strings.CopyTo(image, 64);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), (ulong)tableOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), (ushort)(names.Length + 2));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), (ushort)(names.Length + 1));
            for (var i = 0; i <= names.Length; i++)
            {
                var isStringTable = i == names.Length;
                var header = image.AsSpan(tableOffset + ((i + 1) * 64), 64);
                BinaryPrimitives.WriteUInt32LittleEndian(header, nameOffsets[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], isStringTable ? 3u : 1u);
                if (isStringTable)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(header[24..], 64);
                    BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)strings.Count);
                }
            }

            return image;
        }

        private static void AssertLinuxAmd64Executable(ZipArchiveEntry entry)
        {
            var header = new byte[20];
            using (var stream = entry.Open())
            {
                stream.ReadExactly(header);
            }

            header[..4].Should().Equal(0x7F, (byte)'E', (byte)'L', (byte)'F');
            header[4].Should().Be(2, "64-bit");
            header[5].Should().Be(1, "little endian");
            (header[18] | (header[19] << 8)).Should().Be(0x3E, "x86_64");

            var permissions = (entry.ExternalAttributes >> 16) & 0xFFFF;
            permissions.Should().Be(Convert.ToInt32("100777", 8), "the Linux host must be able to execute the extracted binary");
        }

        private async Task InitRustAppAsync(string testName)
        {
            await FuncInitWithRetryAsync(testName, [".", "--worker-runtime", "rust"]);

            // The scaffold names crates that are not published yet, so packing tests use a worker with no dependencies.
            File.WriteAllText(Path.Combine(WorkingDirectory, "Cargo.toml"), ProbeManifest);
            File.WriteAllText(Path.Combine(WorkingDirectory, "src", "main.rs"), ProbeSource);
        }

        private string SingleZip()
        {
            var zips = Directory.GetFiles(WorkingDirectory, "*.zip");
            zips.Should().ContainSingle("func pack writes one package");
            return zips[0];
        }
    }
}
