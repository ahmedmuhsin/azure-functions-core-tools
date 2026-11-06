// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using Azure.Functions.Cli.E2ETests.Traits;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.TestFramework.Assertions;
using Azure.Functions.Cli.TestFramework.Commands;
using Azure.Functions.Cli.TestFramework.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Azure.Functions.Cli.E2ETests.Commands.FuncStart
{
    [Trait(WorkerRuntimeTraits.WorkerRuntime, WorkerRuntimeTraits.Rust)]
    public class RustTests(ITestOutputHelper log) : BaseE2ETests(log)
    {
        // Set this to a checkout of the Rust SDK workspace (the folder that holds crates/) to run the
        // test that calls a function. The scaffolded manifest names a crate that is not published yet,
        // so that test points it at this checkout.
        private const string SdkDirectoryVariable = "RUST_E2E_SDK_DIR";

        // A worker that needs no crates. It records its working directory and the arguments the host gave
        // it, which is all the first test needs to prove that Core Tools built it and the host launched it.
        private const string ProbeManifest = "[package]\nname = \"probe\"\nversion = \"0.1.0\"\nedition = \"2024\"\n";
        private const string ProbeSource =
            "fn main() {\n" +
            "    let args: Vec<String> = std::env::args().collect();\n" +
            "    let cwd = std::env::current_dir().unwrap();\n" +
            "    std::fs::write(\"probe-exe.txt\", std::env::current_exe().unwrap().display().to_string()).unwrap();\n" +
            "    std::fs::write(\"probe-args.txt\", format!(\"{}\\n{}\", cwd.display(), args.join(\"\\n\"))).unwrap();\n" +
            "    std::thread::sleep(std::time::Duration::from_secs(5));\n" +
            "}\n";

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Start_Rust_BuildsAndLaunchesTheWorker(bool noBuild)
        {
            var testName = $"{nameof(Start_Rust_BuildsAndLaunchesTheWorker)}_{noBuild}";
            await FuncInitWithRetryAsync(testName, [".", "--worker-runtime", "rust"]);
            File.WriteAllText(Path.Combine(WorkingDirectory, "Cargo.toml"), ProbeManifest);
            File.WriteAllText(Path.Combine(WorkingDirectory, "src", "main.rs"), ProbeSource);

            var binaryPath = Path.Combine(WorkingDirectory, RustHelpers.LocalBinaryPath);
            if (noBuild)
            {
                await RustHelpers.BuildProject(WorkingDirectory);
                File.Exists(binaryPath).Should().BeTrue();
            }
            else
            {
                File.Exists(binaryPath).Should().BeFalse();
            }

            var probeFile = Path.Combine(WorkingDirectory, "probe-args.txt");
            var port = ProcessHelper.GetAvailablePort();
            var command = new FuncStartCommand(FuncPath, testName, Log);
            command.ProcessStartedHandler = async process =>
            {
                try
                {
                    await WaitForFileAsync(probeFile, TimeSpan.FromMinutes(4));
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
            };

            var arguments = new List<string> { "--port", port.ToString() };
            if (noBuild)
            {
                arguments.Add("--no-build");
            }

            var result = command
                .WithWorkingDirectory(WorkingDirectory)
                .WithEnvironmentVariable(Common.Constants.FunctionsWorkerRuntime, "native")
                .Execute(arguments);

            File.Exists(probeFile).Should().BeTrue("the host should have launched the worker");
            var recorded = File.ReadAllLines(probeFile);
            Path.GetFileName(recorded[0]).Should().Be(Path.GetFileName(WorkingDirectory), "the worker starts in the app folder");

            // The host ran bin/app (bin\app.exe on Windows), the executable that the native worker config names.
            var executable = File.ReadAllText(Path.Combine(WorkingDirectory, "probe-exe.txt")).Replace('\\', '/');
            executable.Should().EndWith(OperatingSystem.IsWindows() ? "bin/app.exe" : "bin/app", "the host runs the binary that 'func start' built");
            executable.Should().NotContain("/target/", "the host is not pointed at the build output of cargo");

            // The host gives a directly launched worker both spellings of its startup arguments.
            recorded.Should().Contain(["--host", "--port", "--workerId", "--requestId", "--functions-uri", "--functions-worker-id", "--functions-request-id"]);

            result.Should().HaveStdOutContaining(Utilities.RustPreviewWarning);
            if (noBuild)
            {
                result.Should().NotHaveStdOutContaining("Building Rust worker binary");
            }
            else
            {
                result.Should().HaveStdOutContaining($"Building Rust worker binary '{RustHelpers.LocalBinaryPath}'");
            }
        }

        // 'func start --no-build' runs a binary that is already built, so it must not need cargo on the PATH. The probe
        // worker is built here with cargo, and the host is then started with a PATH that has no cargo in it.
        [SkippableFact]
        public async Task Start_Rust_NoBuild_RunsWithoutCargoOnThePath()
        {
            Skip.IfNot(CargoIsAvailable(), "Needs cargo to build the probe worker.");

            var testName = nameof(Start_Rust_NoBuild_RunsWithoutCargoOnThePath);
            await FuncInitWithRetryAsync(testName, [".", "--worker-runtime", "rust"]);
            File.WriteAllText(Path.Combine(WorkingDirectory, "Cargo.toml"), ProbeManifest);
            File.WriteAllText(Path.Combine(WorkingDirectory, "src", "main.rs"), ProbeSource);
            await RustHelpers.BuildProject(WorkingDirectory);
            File.Exists(Path.Combine(WorkingDirectory, RustHelpers.LocalBinaryPath)).Should().BeTrue();

            var pathWithoutCargo = PathWithoutCargo();
            var probeFile = Path.Combine(WorkingDirectory, "probe-args.txt");
            var port = ProcessHelper.GetAvailablePort();
            var command = new FuncStartCommand(FuncPath, testName, Log);
            command.ProcessStartedHandler = async process =>
            {
                try
                {
                    await WaitForFileAsync(probeFile, TimeSpan.FromMinutes(4), process);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
            };

            var result = command
                .WithWorkingDirectory(WorkingDirectory)
                .WithEnvironmentVariable("PATH", pathWithoutCargo)
                .WithEnvironmentVariable(Common.Constants.FunctionsWorkerRuntime, "native")
                .Execute(["--port", port.ToString(), "--no-build"]);

            File.Exists(probeFile).Should().BeTrue("the host should have launched the worker although there is no cargo on the PATH");
            result.Should().NotHaveStdOutContaining("Building Rust worker binary");
        }

        // The debug binary that 'func start' builds is the binary of the package. On an x86_64 Linux machine it is a valid x86_64 ELF file,
        // so 'func pack --no-build' takes it for the binary of the package, packs it and warns that it has debug information. On Windows
        // the binary of 'func start' is bin\app.exe, which 'func pack --no-build' never reads, so it finds no binary to pack.
        [SkippableFact]
        public async Task Start_Rust_ThenPackNoBuild_PacksTheDebugBinaryOnLinuxAndWarns()
        {
            Skip.IfNot(CargoIsAvailable(), "Needs cargo to build the probe worker.");

            var testName = nameof(Start_Rust_ThenPackNoBuild_PacksTheDebugBinaryOnLinuxAndWarns);
            await FuncInitWithRetryAsync(testName, [".", "--worker-runtime", "rust"]);
            File.WriteAllText(Path.Combine(WorkingDirectory, "Cargo.toml"), ProbeManifest);
            File.WriteAllText(Path.Combine(WorkingDirectory, "src", "main.rs"), ProbeSource);

            var probeFile = Path.Combine(WorkingDirectory, "probe-args.txt");
            var port = ProcessHelper.GetAvailablePort();
            var start = new FuncStartCommand(FuncPath, testName, Log);
            start.ProcessStartedHandler = async process =>
            {
                try
                {
                    await WaitForFileAsync(probeFile, TimeSpan.FromMinutes(4), process);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
            };

            start
                .WithWorkingDirectory(WorkingDirectory)
                .WithEnvironmentVariable(Common.Constants.FunctionsWorkerRuntime, "native")
                .Execute(["--port", port.ToString()]);

            File.Exists(probeFile).Should().BeTrue("the start built the worker and the host launched it");
            File.Exists(Path.Combine(WorkingDirectory, RustHelpers.LocalBinaryPath)).Should().BeTrue("the start left its binary in bin");

            var pack = new FuncPackCommand(FuncPath, $"{testName}_pack", Log)
                .WithWorkingDirectory(WorkingDirectory)
                .Execute(["--no-build"]);

            if (OperatingSystem.IsWindows())
            {
                pack.Should().ExitWith(1);
                pack.Should().HaveStdErrContaining("Could not find a built Rust binary");
                Directory.GetFiles(WorkingDirectory, "*.zip").Should().BeEmpty("there was no binary of the package to pack");
            }
            else if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
            {
                pack.Should().ExitWith(0);
                pack.Should().HaveStdOutContaining($"'{Path.Combine("bin", "app")}' has debug information");
                Directory.GetFiles(WorkingDirectory, "*.zip").Should().ContainSingle();
            }
        }

        [SkippableTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Start_Rust_ExecutesHttpFunction(bool noBuild)
        {
            var sdkDirectory = Environment.GetEnvironmentVariable(SdkDirectoryVariable);
            Skip.If(string.IsNullOrEmpty(sdkDirectory), $"Set {SdkDirectoryVariable} to a checkout of the Rust SDK workspace to run this test.");

            var testName = $"{nameof(Start_Rust_ExecutesHttpFunction)}_{noBuild}";
            await FuncInitWithRetryAsync(testName, [".", "--worker-runtime", "rust"]);
            PointTheSdkDependenciesAt(sdkDirectory!);

            var binaryPath = Path.Combine(WorkingDirectory, RustHelpers.LocalBinaryPath);
            if (noBuild)
            {
                await RustHelpers.BuildProject(WorkingDirectory);
                File.Exists(binaryPath).Should().BeTrue();
            }
            else
            {
                File.Exists(binaryPath).Should().BeFalse();
            }

            var port = ProcessHelper.GetAvailablePort();
            var command = new FuncStartCommand(FuncPath, testName, Log);
            string? response = null;
            command.ProcessStartedHandler = async process =>
            {
                response = await ProcessHelper.ProcessStartedHandlerHelper(
                    port,
                    process,
                    command.FileWriter ?? throw new ArgumentNullException(nameof(command.FileWriter)),
                    "hello");
            };

            var arguments = new List<string> { "--port", port.ToString() };
            if (noBuild)
            {
                arguments.Add("--no-build");
            }

            var result = command
                .WithWorkingDirectory(WorkingDirectory)
                .WithEnvironmentVariable(Common.Constants.FunctionsWorkerRuntime, "native")
                .Execute(arguments);

            response.Should().Be("hello world");
            File.Exists(binaryPath).Should().BeTrue();
            if (noBuild)
            {
                result.Should().NotHaveStdOutContaining("Building Rust worker binary");
            }
            else
            {
                result.Should().HaveStdOutContaining($"Building Rust worker binary '{RustHelpers.LocalBinaryPath}'");
            }
        }

        private static async Task WaitForFileAsync(string path, TimeSpan timeout, Process? process = null)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return;
                }

                // A start that failed has nothing left to wait for.
                if (process is { HasExited: true })
                {
                    return;
                }

                await Task.Delay(500);
            }
        }

        private static bool CargoIsAvailable()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("cargo", "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                process!.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                return process.WaitForExit(30000) && process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        // The PATH of this process without the folders that hold cargo.
        private static string PathWithoutCargo()
        {
            var cargo = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
            var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(folder => !File.Exists(Path.Combine(folder, cargo)));
            return string.Join(Path.PathSeparator, folders);
        }

        private void PointTheSdkDependenciesAt(string sdkDirectory)
        {
            var manifestPath = Path.Combine(WorkingDirectory, "Cargo.toml");
            var crate = sdkDirectory.Replace('\\', '/').TrimEnd('/') + "/crates/azure-functions-rust";
            var lines = File.ReadAllLines(manifestPath)
                .Select(line => line.StartsWith("azure-functions-rust = ", StringComparison.Ordinal)
                    ? $"azure-functions-rust = {{ path = \"{crate}\", features = [\"worker\"] }}"
                    : line);
            File.WriteAllLines(manifestPath, lines);
        }
    }
}
