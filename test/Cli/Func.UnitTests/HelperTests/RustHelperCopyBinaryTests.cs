// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using AwesomeAssertions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // How the built Rust executable gets to bin/app, with real files and real processes.
    public class RustHelperCopyBinaryTests
    {
        [Fact]
        public void CopyBinary_CreatesTheFolderAndCopiesTheFile()
        {
            using var dir = new TempDirectory();
            var built = dir.Write("built", "first");
            var destination = Path.Combine(dir.Path, "bin", "app");

            RustHelpers.CopyBinary(built, destination);

            File.ReadAllText(destination).Should().Be("first");
        }

        [Fact]
        public void CopyBinary_ExistingDestination_GetsTheNewContent()
        {
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");
            RustHelpers.CopyBinary(dir.Write("first", "first"), destination);

            RustHelpers.CopyBinary(dir.Write("second", "second"), destination);

            File.ReadAllText(destination).Should().Be("second");
        }

        [SkippableFact]
        public void CopyBinary_OnUnix_MakesTheFileExecutable()
        {
            Skip.If(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows.");
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");

            RustHelpers.CopyBinary(dir.Write("built", "x"), destination);

            // The analyzer only understands a direct platform check, and the test has already skipped on Windows.
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(destination).Should().HaveFlag(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }

        // On Apple silicon the kernel keeps the code signature that it checked for a file, so a binary that is
        // rewritten in place after it ran can be killed the next time it starts. That cannot be reproduced without a Mac,
        // so this pins what it depends on: the old file is replaced by a new one, and is not rewritten. A hard
        // link is a second name for the old file, and it must keep the old content.
        [SkippableFact]
        public void CopyBinary_ReplacesTheFileAndDoesNotRewriteItInPlace()
        {
            Skip.If(OperatingSystem.IsWindows(), "Needs hard links through ln.");
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");
            RustHelpers.CopyBinary(dir.Write("first", "first"), destination);
            var otherNameOfTheOldFile = Path.Combine(dir.Path, "old-name");
            Run("ln", $"\"{destination}\" \"{otherNameOfTheOldFile}\"").Should().Be(0);

            RustHelpers.CopyBinary(dir.Write("second", "second"), destination);

            File.ReadAllText(destination).Should().Be("second");
            File.ReadAllText(otherNameOfTheOldFile).Should().Be("first", "the old file must be replaced and not rewritten");
        }

        // The usual way to hit this is a second 'func start' while the first one still runs its worker.
        [SkippableFact]
        public void CopyBinary_OnWindowsWhileTheOldBinaryRuns_ThrowsActionableCliException()
        {
            Skip.IfNot(OperatingSystem.IsWindows(), "Windows locks the file of a running program.");
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app.exe");

            // A real running program: a copy of ping that waits for 30 seconds.
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), destination);
            using var running = Process.Start(new ProcessStartInfo(destination, "-n 30 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;

            try
            {
                var built = dir.Write("built.exe", "second");

                var action = () => RustHelpers.CopyBinary(built, destination);

                action.Should().Throw<CliException>()
                    .Which.Message.Should().Contain("Could not copy the built binary")
                    .And.Contain(destination)
                    .And.Contain("Stop any running worker first");
            }
            finally
            {
                running.Kill();
                running.WaitForExit();
            }
        }

        [Fact]
        public void CopyBinary_AfterACopy_LeavesOnlyTheBinaryInTheFolder()
        {
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");
            RustHelpers.CopyBinary(dir.Write("first", "first"), destination);

            RustHelpers.CopyBinary(dir.Write("second", "second"), destination);

            Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Should().ContainSingle("the binary is the only file in the folder").Which.Should().Be(destination);
        }

        // A full disk stops a copy half way. The half of a binary used to stay at bin/app, and
        // 'func pack --no-build' packed it, because its header was valid.
        [Fact]
        public void CopyBinary_CopyFailsHalfWay_KeepsTheOldFileAndLeavesNoTemporaryFile()
        {
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");
            RustHelpers.CopyBinary(dir.Write("first", "old binary"), destination);

            var action = () => RustHelpers.CopyBinary(dir.Write("second", "new binary"), destination, CopyHalfOfItAndRunOutOfSpace);

            var message = action.Should().Throw<CliException>().Which.Message;
            message.Should().Contain("Could not copy the built binary").And.Contain("not enough space");
            message.Should().NotContain("Stop any running worker first", "a full disk is not solved by stopping a worker");
            File.ReadAllText(destination).Should().Be("old binary", "a failed copy must not damage the binary that was there");
            Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Should().ContainSingle("the half copy must be removed").Which.Should().Be(destination);
        }

        [Fact]
        public void CopyBinary_CopyFailsHalfWayAndThereWasNoBinary_LeavesNothingBehind()
        {
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");

            var action = () => RustHelpers.CopyBinary(dir.Write("built", "new binary"), destination, CopyHalfOfItAndRunOutOfSpace);

            action.Should().Throw<CliException>();
            Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Should().BeEmpty("a failed copy leaves no binary, and no half of one");
        }

        [Fact]
        public void CopyBinary_AccessDeniedWhileCopying_DoesNotTellToStopTheWorker()
        {
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");

            var action = () => RustHelpers.CopyBinary(dir.Write("built", "new binary"), destination, CopyHalfOfItAndDenyAccess);

            var message = action.Should().Throw<CliException>().Which.Message;
            message.Should().Contain("Could not copy the built binary").And.Contain("Access to the path is denied");
            message.Should().NotContain("Stop any running worker first", "only a program that holds the old binary is a reason to stop the worker");
            Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Should().BeEmpty();
        }

        [Fact]
        public void CopyBinary_TheBuiltFileDoesNotExist_ThrowsCliExceptionAndKeepsTheOldFile()
        {
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");
            RustHelpers.CopyBinary(dir.Write("first", "old binary"), destination);

            var action = () => RustHelpers.CopyBinary(Path.Combine(dir.Path, "missing"), destination);

            action.Should().Throw<CliException>().Which.Message.Should().Contain("Could not copy the built binary").And.NotContain("Stop any running worker first");
            File.ReadAllText(destination).Should().Be("old binary");
            Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Should().ContainSingle().Which.Should().Be(destination);
        }

        [Fact]
        public void CopyBinary_AFolderIsAtTheDestination_SaysSoAndKeepsTheFolder()
        {
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");
            Directory.CreateDirectory(destination);
            var keep = Path.Combine(destination, "keep.txt");
            File.WriteAllText(keep, "mine");

            var action = () => RustHelpers.CopyBinary(dir.Write("built", "x"), destination);

            var message = action.Should().Throw<CliException>().Which.Message;
            message.Should().Contain("is a folder").And.Contain(destination);
            message.Should().NotContain("Stop any running worker first");
            File.ReadAllText(keep).Should().Be("mine");
        }

        [Fact]
        public void CopyBinary_AFileIsWhereTheBinFolderBelongs_SaysSoAndKeepsTheFile()
        {
            using var dir = new TempDirectory();
            var binFile = dir.Write("bin", "mine");
            var destination = Path.Combine(binFile, "app");

            var action = () => RustHelpers.CopyBinary(dir.Write("built", "x"), destination);

            var message = action.Should().Throw<CliException>().Which.Message;
            message.Should().Contain("is a file").And.Contain(binFile);
            message.Should().NotContain("Stop any running worker first");
            File.ReadAllText(binFile).Should().Be("mine");
        }

        [SkippableFact]
        public void CopyBinary_OnUnixAReadOnlyOldFile_IsReplaced()
        {
            Skip.If(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows.");
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app");
            RustHelpers.CopyBinary(dir.Write("first", "old binary"), destination);

            // The analyzer only understands a direct platform check, and the test has already skipped on Windows.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            RustHelpers.CopyBinary(dir.Write("second", "new binary"), destination);

            File.ReadAllText(destination).Should().Be("new binary");
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(destination).Should().HaveFlag(UnixFileMode.UserExecute);
            }
        }

        // Windows does not replace a read-only file. The old behavior said that a worker was running.
        [SkippableFact]
        public void CopyBinary_OnWindowsAReadOnlyOldFile_SaysSoAndKeepsIt()
        {
            Skip.IfNot(OperatingSystem.IsWindows(), "The read-only attribute decides on Windows only.");
            using var dir = new TempDirectory();
            var destination = Path.Combine(dir.Path, "bin", "app.exe");
            RustHelpers.CopyBinary(dir.Write("first.exe", "old binary"), destination);
            File.SetAttributes(destination, FileAttributes.ReadOnly);

            try
            {
                var action = () => RustHelpers.CopyBinary(dir.Write("second.exe", "new binary"), destination);

                var message = action.Should().Throw<CliException>().Which.Message;
                message.Should().Contain("is read-only").And.Contain(destination);
                message.Should().NotContain("Stop any running worker first");
                File.ReadAllText(destination).Should().Be("old binary");
                Directory.GetFileSystemEntries(Path.GetDirectoryName(destination)!).Should().ContainSingle().Which.Should().Be(destination);
            }
            finally
            {
                File.SetAttributes(destination, FileAttributes.Normal);
            }
        }

        // The user's file behind a link is theirs. The link is the destination, so the link is what is replaced.
        [SkippableFact]
        public void CopyBinary_ASymbolicLinkIsAtTheDestination_ReplacesTheLinkAndKeepsItsTarget()
        {
            using var dir = new TempDirectory();
            var target = dir.Write("users-file", "mine");
            var destination = Path.Combine(dir.Path, "bin", "app");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SymbolicLinkTestHelper.CreateOrSkip(destination, target);

            RustHelpers.CopyBinary(dir.Write("built", "new binary"), destination);

            File.ReadAllText(target).Should().Be("mine", "the file behind the link is not the destination");
            new FileInfo(destination).LinkTarget.Should().BeNull("the link is replaced by the binary");
            File.ReadAllText(destination).Should().Be("new binary");
        }

        // The error says what the system says, and not that a worker runs. Root can write to any folder, so it is skipped for root.
        [SkippableFact]
        public void CopyBinary_OnUnixAReadOnlyBinFolder_ThrowsWithTheSystemReasonAndNoWorkerHint()
        {
            Skip.If(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "Needs a Unix user that is not root.");
            using var dir = new TempDirectory();
            var binFolder = Path.Combine(dir.Path, "bin");
            Directory.CreateDirectory(binFolder);

            // The analyzer only understands a direct platform check, and the test has already skipped on Windows.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(binFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            try
            {
                var action = () => RustHelpers.CopyBinary(dir.Write("built", "x"), Path.Combine(binFolder, "app"));

                var message = action.Should().Throw<CliException>().Which.Message;
                message.Should().Contain("Could not copy the built binary").And.Contain("denied");
                message.Should().NotContain("Stop any running worker first");
            }
            finally
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(binFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }

        // A copier that dies as a full disk makes it die: the first bytes are written, then there is no room.
        private static void CopyHalfOfItAndRunOutOfSpace(string source, string target)
        {
            File.WriteAllText(target, File.ReadAllText(source).Substring(0, 3));
            throw new IOException("There is not enough space on the disk.");
        }

        private static void CopyHalfOfItAndDenyAccess(string source, string target)
        {
            File.WriteAllText(target, File.ReadAllText(source).Substring(0, 3));
            throw new UnauthorizedAccessException("Access to the path is denied.");
        }

        private static int Run(string fileName, string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-copy-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public string Write(string name, string content)
            {
                var path = System.IO.Path.Combine(Path, name);
                File.WriteAllText(path, content);
                return path;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup. A program that was just killed can hold its file for a moment.
                }
            }
        }
    }
}
