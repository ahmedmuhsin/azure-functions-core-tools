// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using AwesomeAssertions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // How Core Tools asks cargo for its version, with real processes. A shell stands in for cargo, because the tests need a
    // program that writes a lot to stderr, fails after it printed a version, or never finishes. The probe writes a warning
    // to the shared console when the program is missing, so the class does not run in parallel with the others.
    [Collection("NativeWorkerRuntimeTests")]
    public class RustToolchainProbeTests
    {
        // A probe that blocks fails its test after this long, and does not hang the run.
        private static readonly TimeSpan _guard = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan _noDeadline = TimeSpan.FromMinutes(2);

        [Fact]
        public void Probe_ToolPrintsAVersion_ReturnsIt()
        {
            var tool = PrintsAVersion();

            var info = Probe(tool, _noDeadline);

            info.Should().NotBeNull();
            info.Version.Should().Be("1.90.0");
            info.Runtime.Should().Be(WorkerRuntime.Rust);
            info.ExecutablePath.Should().Be(tool.FileName);
        }

        // The tool fills the pipe of stderr before it prints its version. A probe that reads stdout to the end first waits for
        // a tool that waits for it.
        [Fact]
        public void Probe_ToolWritesMoreThanAPipeBufferToStderr_DoesNotBlock()
        {
            var info = Probe(WritesALotToStderrThenPrintsAVersion(), _noDeadline);

            info.Should().NotBeNull();
            info.Version.Should().Be("1.90.0");
        }

        [Fact]
        public void Probe_ToolFailsAfterPrintingAVersion_ShowsTheFirstLineOfItsStderr()
        {
            Action act = () => Probe(FailsAfterPrintingAVersion(), _noDeadline);

            // The message quotes the command line, and a stand-in shell carries its script there. So the end of the message is what is checked.
            var message = act.Should().Throw<CliException>().Which.Message;
            message.Should().Contain("failed with exit code 1");
            message.Should().EndWith("It said: error: toolchain is broken", "only the first line of what the tool said is shown");
            message.Should().NotContain("rustup.rs", "the tool ran, so the text must not say that Rust is missing");
        }

        [Fact]
        public void Probe_ToolFailsWithoutSayingAnything_SaysSo()
        {
            Action act = () => Probe(FailsInSilence(), _noDeadline);

            var message = act.Should().Throw<CliException>().Which.Message;
            message.Should().EndWith("failed with exit code 3.", "the tool said nothing that could be added");
            message.Should().NotContain("rustup.rs");
        }

        [Fact]
        public void Probe_ToolPrintsSomethingElse_ShowsWhatItPrinted()
        {
            Action act = () => Probe(PrintsSomethingElse(), _noDeadline);

            var message = act.Should().Throw<CliException>().Which.Message;
            message.Should().EndWith("It printed: hello");
            message.Should().NotContain("rustup.rs", "the tool ran, so the text must not say that Rust is missing");
        }

        [Fact]
        public void Probe_ToolDoesNotFinishInTime_StopsItAndItsChildProcessAndSaysSo()
        {
            using var dir = new TempDirectory();
            var stopwatch = Stopwatch.StartNew();

            Action act = () => Probe(NeverFinishes(dir.Path), TimeSpan.FromSeconds(2), dir.Path);

            var message = act.Should().Throw<CliException>().Which.Message;
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the probe gives up at its deadline and does not wait for the tool to end");
            message.Should().Contain("did not finish within 2 seconds").And.Contain("It said: started");

            // The shell runs a second process that does the waiting, and the probe has to end that one too. A Unix shell writes the
            // id of the second process. On Windows the second shell writes a file when its wait is over, which a stopped shell never does.
            if (OperatingSystem.IsWindows())
            {
                Thread.Sleep(TimeSpan.FromSeconds(4));
                File.Exists(Path.Combine(dir.Path, "finished.txt")).Should().BeFalse("the probe ends the tool and the processes that it started");
            }
            else
            {
                ProcessEnds(int.Parse(File.ReadAllText(Path.Combine(dir.Path, "child.pid")).Trim())).Should().BeTrue("the probe ends the tool and the processes that it started");
            }
        }

        [Fact]
        public void Probe_ProgramCannotBeStarted_ReturnsNull()
        {
            var tool = new Tool("ct-no-such-program-" + Guid.NewGuid().ToString("N"), "--version");

            var info = Probe(tool, _noDeadline);

            info.Should().BeNull("a program that cannot be started is what 'cargo is not installed' looks like");
        }

        [Fact]
        public void Probe_RunsTheToolInTheGivenFolder()
        {
            using var dir = new TempDirectory();
            var tool = Shell("cd > cwd.txt & echo cargo 1.90.0", "pwd > cwd.txt; echo cargo 1.90.0");

            var info = Probe(tool, _noDeadline, dir.Path);

            info.Should().NotBeNull();
            var folder = File.ReadAllText(Path.Combine(dir.Path, "cwd.txt")).Trim();

            // The last folder name only, because the OS can spell the rest of the path differently.
            Path.GetFileName(folder.TrimEnd('/', '\\')).Should().Be(Path.GetFileName(dir.Path.TrimEnd('/', '\\')));
        }

        private static WorkerLanguageVersionInfo Probe(Tool tool, TimeSpan deadline, string workingDirectory = null)
        {
            var task = Task.Run(() => RustHelpers.ProbeCargoVersion(tool.FileName, tool.Arguments, workingDirectory, deadline));
            if (!task.Wait(_guard))
            {
                throw new TimeoutException($"The probe did not return within {_guard.TotalSeconds} seconds.");
            }

            return task.GetAwaiter().GetResult();
        }

        // A shell with one command. The Windows form goes to cmd, the other one to sh. Neither command holds a double quote.
        private static Tool Shell(string windows, string unix)
        {
            return OperatingSystem.IsWindows()
                ? new Tool("cmd.exe", $"/c \"{windows}\"")
                : new Tool("sh", $"-c \"{unix}\"");
        }

        private static Tool PrintsAVersion()
        {
            return Shell("echo cargo 1.90.0", "echo cargo 1.90.0");
        }

        private static Tool WritesALotToStderrThenPrintsAVersion()
        {
            const string line = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
            return Shell(
                $"(for /L %i in (1,1,4000) do @echo {line} 1>&2) & echo cargo 1.90.0",
                $"i=0; while [ $i -lt 4000 ]; do echo {line} 1>&2; i=$((i+1)); done; echo cargo 1.90.0");
        }

        private static Tool FailsAfterPrintingAVersion()
        {
            return Shell(
                "echo cargo 1.90.0& echo error: toolchain is broken 1>&2& echo second line 1>&2& exit /b 1",
                "echo cargo 1.90.0; echo error: toolchain is broken 1>&2; echo second line 1>&2; exit 1");
        }

        private static Tool FailsInSilence()
        {
            return Shell("exit /b 3", "exit 3");
        }

        private static Tool PrintsSomethingElse()
        {
            return Shell("echo hello", "echo hello");
        }

        // Says something on stderr, then starts a second process that waits for a while, and waits for it. The second process of a Unix
        // shell becomes the sleep and writes its id. On Windows the second one is a cmd that runs a batch file, which pings with the
        // full path (cmd cannot search a very long PATH, which developer machines can have) and then writes a file.
        private static Tool NeverFinishes(string folder)
        {
            File.WriteAllText(
                Path.Combine(folder, "waiter.cmd"),
                $"@echo off\r\n\"{Environment.SystemDirectory}\\PING.EXE\" -n 5 127.0.0.1 > nul\r\necho finished > finished.txt\r\n");

            return Shell(
                "echo started 1>&2& %ComSpec% /c waiter.cmd",
                "echo started 1>&2; sh -c 'echo $$ > child.pid; exec sleep 20'; echo done");
        }

        // True when no process with that id runs any more. The end of a killed process can take a moment to show, and a
        // process that nothing has waited for yet is a zombie, which is over for this purpose.
        private static bool ProcessEnds(int processId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!ProcessRuns(processId))
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return false;
        }

        private static bool ProcessRuns(int processId)
        {
            if (OperatingSystem.IsLinux())
            {
                // The state is the first field after the name, which is in parentheses and can hold spaces.
                try
                {
                    var stat = File.ReadAllText($"/proc/{processId}/stat");
                    var end = stat.LastIndexOf(')');
                    return end < 0 || end + 2 >= stat.Length || stat[end + 2] != 'Z';
                }
                catch (IOException)
                {
                    return false;
                }
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private readonly record struct Tool(string FileName, string Arguments);

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
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
    }

    // GetEnvironmentCargoVersion with a program called cargo, found through the PATH. Unix only, because there a script can be
    // the program. Putting a folder on the PATH changes the process, so the class does not run in parallel with the others.
    [Collection("NativeWorkerRuntimeTests")]
    public class RustCargoOnThePathTests : IDisposable
    {
        private readonly string _folder;
        private readonly string _originalPath;

        public RustCargoOnThePathTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "func-fake-cargo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _originalPath = Environment.GetEnvironmentVariable("PATH");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        [SkippableFact]
        public async Task GetEnvironmentCargoVersion_CargoPrintsAVersion_ReturnsIt()
        {
            Skip.If(OperatingSystem.IsWindows(), "A script is a program on Unix only.");
            InstallCargo("echo cargo 1.91.2 '(abc 2025-11-01)'");

            var info = await RustHelpers.GetEnvironmentCargoVersion();

            info.Should().NotBeNull();
            info.Version.Should().Be("1.91.2");
            info.ExecutablePath.Should().Be("cargo");
        }

        [SkippableFact]
        public async Task GetEnvironmentCargoVersion_CargoFails_SaysWhyAndNotThatRustIsMissing()
        {
            Skip.If(OperatingSystem.IsWindows(), "A script is a program on Unix only.");
            InstallCargo("echo \"error: override toolchain 'ct-none' is not installed\" 1>&2; echo 'help: run rustup toolchain install' 1>&2; exit 1");

            Func<Task> act = () => RustHelpers.GetEnvironmentCargoVersion();

            var message = (await act.Should().ThrowAsync<CliException>()).Which.Message;
            message.Should().Contain("failed with exit code 1");
            message.Should().EndWith("It said: error: override toolchain 'ct-none' is not installed", "only the first line of what cargo said is shown");
            message.Should().NotContain("rustup.rs", "cargo ran, so the text must not say that Rust is missing");
        }

        // Puts a program called cargo, which runs the script, first on the PATH.
        private void InstallCargo(string script)
        {
            var path = Path.Combine(_folder, "cargo");
            File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");

            // The analyzer only understands a direct platform check, and the tests have already skipped on Windows.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            Environment.SetEnvironmentVariable("PATH", _folder + Path.PathSeparator + _originalPath);
        }
    }
}
