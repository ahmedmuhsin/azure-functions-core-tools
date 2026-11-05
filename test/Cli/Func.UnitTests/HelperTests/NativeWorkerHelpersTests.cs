// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;
using AwesomeAssertions;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // The test without a working directory reads the process working directory, which other test classes change while they run.
    [Collection("NativeWorkerRuntimeTests")]
    public class NativeWorkerHelpersTests
    {
        // The folder matters because rustup picks the toolchain from rust-toolchain.toml in the folder that cargo runs in,
        // and 'func pack <folder>' runs from another folder. The tool here is a real process that prints the folder it runs in.
        private static readonly Regex _anyLine = new Regex("(.+)", RegexOptions.Compiled);
        private static readonly Regex _toolVersion = new Regex(@"tool (\d+\.\d+\.\d+)", RegexOptions.Compiled);

        [Fact]
        public async Task GetToolVersion_WithAWorkingDirectory_RunsTheToolThere()
        {
            using var dir = new TempDirectory();
            var (tool, arguments) = PrintWorkingDirectoryCommand();

            var info = await NativeWorkerHelpers.GetToolVersion(WorkerRuntime.Rust, "test", tool, arguments, _anyLine, dir.Path);

            info.Should().NotBeNull();
            LastSegment(info.Version).Should().Be(LastSegment(dir.Path));
        }

        [Fact]
        public async Task GetToolVersion_WithoutAWorkingDirectory_RunsInTheCurrentFolder()
        {
            var (tool, arguments) = PrintWorkingDirectoryCommand();

            var info = await NativeWorkerHelpers.GetToolVersion(WorkerRuntime.Rust, "test", tool, arguments, _anyLine);

            info.Should().NotBeNull();
            LastSegment(info.Version).Should().Be(LastSegment(Environment.CurrentDirectory));
        }

        // A tool that fails has nothing to say about its version, even when it printed a number before it failed.
        [Fact]
        public async Task GetToolVersion_ToolPrintsAVersionAndThenFails_ReturnsNull()
        {
            var (tool, arguments) = PrintsAVersionAndFailsCommand();

            var info = await NativeWorkerHelpers.GetToolVersion(WorkerRuntime.Rust, "test", tool, arguments, _toolVersion);

            info.Should().BeNull("the tool exited with an error");
        }

        [Fact]
        public async Task GetToolVersion_ToolPrintsAVersionAndSucceeds_ReturnsIt()
        {
            var (tool, arguments) = PrintsAVersionCommand();

            var info = await NativeWorkerHelpers.GetToolVersion(WorkerRuntime.Rust, "test", tool, arguments, _toolVersion);

            info.Should().NotBeNull();
            info.Version.Should().Be("1.2.3");
        }

        private static (string Tool, string Arguments) PrintsAVersionCommand()
        {
            return OperatingSystem.IsWindows() ? ("cmd.exe", "/c \"echo tool 1.2.3\"") : ("sh", "-c \"echo tool 1.2.3\"");
        }

        private static (string Tool, string Arguments) PrintsAVersionAndFailsCommand()
        {
            return OperatingSystem.IsWindows() ? ("cmd.exe", "/c \"echo tool 1.2.3& exit /b 1\"") : ("sh", "-c \"echo tool 1.2.3; exit 1\"");
        }

        private static (string Tool, string Arguments) PrintWorkingDirectoryCommand()
        {
            return OperatingSystem.IsWindows() ? ("cmd.exe", "/c cd") : ("pwd", string.Empty);
        }

        // The last folder name, because the OS can spell the rest of the path differently (for example /private/var on macOS).
        private static string LastSegment(string path)
        {
            return Path.GetFileName(path.TrimEnd('/', '\\'));
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
