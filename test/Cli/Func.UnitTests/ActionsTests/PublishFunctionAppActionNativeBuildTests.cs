// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.Actions.AzureActions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.Interfaces;
using Moq;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.ActionsTests
{
    // The local build step of 'func publish', which runs before anything is uploaded, with real folders and no Azure. The
    // steps that need a toolchain are not reached: a folder without the project file stops each of them first, and
    // --no-build only reads the binary that is there.
    [Collection("NativeWorkerRuntimeTests")]
    public class PublishFunctionAppActionNativeBuildTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task BuildForPublish_RustFolderWithoutCargoToml_ChecksTheLayoutFirst(bool noBuild)
        {
            using var folder = new TempDirectory();
            var action = CreateAction(noBuild);

            Func<Task> act = () => action.BuildForPublish(WorkerRuntime.Rust, folder.Path, isNonCsxDotnetRuntime: false);

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Cargo.toml");
        }

        [Fact]
        public async Task BuildForPublish_RustNoBuildWithABinaryThatIsNotForLinux_RefusesIt()
        {
            using var folder = new TempDirectory();
            folder.WriteCargoToml();
            folder.WriteBinary(new byte[64]);
            var action = CreateAction(noBuild: true);

            Func<Task> act = () => action.BuildForPublish(WorkerRuntime.Rust, folder.Path, isNonCsxDotnetRuntime: false);

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("not an ELF binary");
        }

        [Fact]
        public async Task BuildForPublish_RustNoBuildWithALinuxBinary_Passes()
        {
            using var folder = new TempDirectory();
            folder.WriteCargoToml();
            folder.WriteBinary(ElfHeader());
            var action = CreateAction(noBuild: true);

            Func<Task> act = () => action.BuildForPublish(WorkerRuntime.Rust, folder.Path, isNonCsxDotnetRuntime: false);

            await act.Should().NotThrowAsync();
        }

        // Go goes through the same step, and its behavior is the same as before.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task BuildForPublish_GoFolderWithoutGoMod_ChecksTheLayoutFirst(bool noBuild)
        {
            using var folder = new TempDirectory();
            var action = CreateAction(noBuild);

            Func<Task> act = () => action.BuildForPublish(WorkerRuntime.Go, folder.Path, isNonCsxDotnetRuntime: false);

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("go.mod");
        }

        [Fact]
        public async Task BuildForPublish_GoNoBuildWithABinaryThatIsNotForLinux_RefusesIt()
        {
            using var folder = new TempDirectory();
            File.WriteAllText(Path.Combine(folder.Path, "go.mod"), "module app\n");
            folder.WriteBinary(new byte[64]);
            var action = CreateAction(noBuild: true);

            Func<Task> act = () => action.BuildForPublish(WorkerRuntime.Go, folder.Path, isNonCsxDotnetRuntime: false);

            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("not an ELF binary");
        }

        // A remote or container build is rejected before the build step. The step still does nothing for them.
        [Theory]
        [InlineData(WorkerRuntime.Rust, BuildOption.Remote)]
        [InlineData(WorkerRuntime.Rust, BuildOption.Container)]
        [InlineData(WorkerRuntime.Go, BuildOption.Remote)]
        [InlineData(WorkerRuntime.Go, BuildOption.Container)]
        public async Task BuildForPublish_NativeAppWithARemoteOrContainerBuild_DoesNothing(WorkerRuntime runtime, BuildOption buildOption)
        {
            using var folder = new TempDirectory();
            var action = CreateAction(noBuild: false, buildOption);

            Func<Task> act = () => action.BuildForPublish(runtime, folder.Path, isNonCsxDotnetRuntime: false);

            await act.Should().NotThrowAsync();
        }

        [Theory]
        [InlineData(WorkerRuntime.Node)]
        [InlineData(WorkerRuntime.Python)]
        public async Task BuildForPublish_AppThatNeedsNoLocalBuild_DoesNothing(WorkerRuntime runtime)
        {
            using var folder = new TempDirectory();
            var action = CreateAction(noBuild: false);

            Func<Task> act = () => action.BuildForPublish(runtime, folder.Path, isNonCsxDotnetRuntime: false);

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task BuildForPublish_DotnetWithNoBuild_BuildsNothing()
        {
            using var folder = new TempDirectory();
            var action = CreateAction(noBuild: true);

            Func<Task> act = () => action.BuildForPublish(WorkerRuntime.DotnetIsolated, folder.Path, isNonCsxDotnetRuntime: true);

            await act.Should().NotThrowAsync("--no-build skips the dotnet publish, which would fail in an empty folder");
        }

        private static PublishFunctionAppAction CreateAction(bool noBuild, BuildOption buildOption = BuildOption.Local)
        {
            return new PublishFunctionAppAction(Mock.Of<ISettings>(), Mock.Of<ISecretsManager>())
            {
                NoBuild = noBuild,
                PublishBuildOption = buildOption,
            };
        }

        // The 20 byte identification region of an x86-64 Linux ELF, which is all that the check of the binary reads.
        private static byte[] ElfHeader()
        {
            var header = new byte[20];
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = 2;
            header[5] = 1;
            header[18] = 0x3E;
            return header;
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-publish-build-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void WriteCargoToml()
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "Cargo.toml"), "[package]\nname = \"app\"\nversion = \"0.1.0\"\nedition = \"2024\"\n");
            }

            public void WriteBinary(byte[] content)
            {
                Directory.CreateDirectory(System.IO.Path.Combine(Path, "bin"));
                File.WriteAllBytes(System.IO.Path.Combine(Path, "bin", "app"), content);
            }

            public void Dispose()
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
