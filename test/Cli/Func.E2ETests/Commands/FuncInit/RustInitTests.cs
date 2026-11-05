// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.E2ETests.Traits;
using Azure.Functions.Cli.TestFramework.Assertions;
using Azure.Functions.Cli.TestFramework.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Azure.Functions.Cli.E2ETests.Commands.FuncInit
{
    [Trait(WorkerRuntimeTraits.WorkerRuntime, WorkerRuntimeTraits.Rust)]
    public class RustInitTests(ITestOutputHelper log) : BaseE2ETests(log)
    {
        [Fact]
        public void Init_WithRust_GeneratesExpectedFunctionProjectFiles()
        {
            var workingDir = WorkingDirectory;
            var testName = nameof(Init_WithRust_GeneratesExpectedFunctionProjectFiles);
            var funcInitCommand = new FuncInitCommand(FuncPath, testName, Log ?? throw new ArgumentNullException(nameof(Log)));
            var localSettingsPath = Path.Combine(workingDir, Common.Constants.LocalSettingsJsonFileName);
            var expectedLocalSettingsContent = new[] { Common.Constants.FunctionsWorkerRuntime, "native", Common.Constants.FunctionsCliNativeLanguage, "rust" };
            var hostJsonPath = Path.Combine(workingDir, "host.json");
            var expectedHostJsonContent = new[] { "extensionBundle" };
            var cargoTomlPath = Path.Combine(workingDir, "Cargo.toml");
            var expectedCargoTomlContent = new[] { "[package]" };
            var mainRsPath = Path.Combine(workingDir, "src", "main.rs");
            var expectedMainRsContent = new[] { "fn main" };
            var gitIgnorePath = Path.Combine(workingDir, ".gitignore");
            var expectedGitIgnoreContent = new[] { "target/" };
            var funcIgnorePath = Path.Combine(workingDir, ".funcignore");

            var filesToValidate = new List<(string FilePath, string[] ExpectedContent)>
            {
                (localSettingsPath, expectedLocalSettingsContent),
                (hostJsonPath, expectedHostJsonContent),
                (cargoTomlPath, expectedCargoTomlContent),
                (mainRsPath, expectedMainRsContent),
                (gitIgnorePath, expectedGitIgnoreContent)
            };

            var funcInitResult = funcInitCommand
                .WithWorkingDirectory(workingDir)
                .Execute(["--worker-runtime", "rust"]);

            funcInitResult.Should().WriteVsCodeExtensionsJsonAndExitWithZero(workingDir);
            funcInitResult.Should().FilesExistsWithExpectContent(filesToValidate);
            File.Exists(funcIgnorePath).Should().BeTrue("Expected .funcignore to exist");
            File.Exists(Path.Combine(workingDir, "go.mod")).Should().BeFalse("Rust init must not write Go files");
            funcInitResult.Should().HaveStdOutContaining(Utilities.RustPreviewWarning);
        }

        [Fact]
        public void Init_WithRustShortcutFlag_ResolvesToRust()
        {
            var workingDir = WorkingDirectory;
            var testName = nameof(Init_WithRustShortcutFlag_ResolvesToRust);
            var funcInitCommand = new FuncInitCommand(FuncPath, testName, Log ?? throw new ArgumentNullException(nameof(Log)));

            var funcInitResult = funcInitCommand
                .WithWorkingDirectory(workingDir)
                .Execute([".", "--rust"]);

            funcInitResult.Should().ExitWith(0);
            File.Exists(Path.Combine(workingDir, "Cargo.toml")).Should().BeTrue("Expected Cargo.toml to exist");
            File.ReadAllText(Path.Combine(workingDir, Common.Constants.LocalSettingsJsonFileName)).Should().Contain("\"rust\"");
        }

        [Fact]
        public void Init_WithForceFlag_InNonEmptyDirectory()
        {
            var workingDir = WorkingDirectory;
            File.WriteAllText(Path.Combine(workingDir, "existing.txt"), "test");

            var testName = nameof(Init_WithForceFlag_InNonEmptyDirectory);
            var funcInitCommand = new FuncInitCommand(FuncPath, testName, Log ?? throw new ArgumentNullException(nameof(Log)));

            var funcInitResult = funcInitCommand
                .WithWorkingDirectory(workingDir)
                .Execute(["--worker-runtime", "rust", "--force"]);

            funcInitResult.Should().ExitWith(0);
            File.Exists(Path.Combine(workingDir, "src", "main.rs")).Should().BeTrue("Expected src/main.rs to exist");
        }

        [Fact]
        public void Init_WithDockerFlag_FailsLoudly()
        {
            var workingDir = WorkingDirectory;
            var testName = nameof(Init_WithDockerFlag_FailsLoudly);
            var funcInitCommand = new FuncInitCommand(FuncPath, testName, Log ?? throw new ArgumentNullException(nameof(Log)));

            var funcInitResult = funcInitCommand
                .WithWorkingDirectory(workingDir)
                .Execute(["--worker-runtime", "rust", "--docker"]);

            funcInitResult.Should().ExitWith(1);
            funcInitResult.Should().HaveStdErrContaining("Docker is not available for the Rust worker runtime.");
            File.Exists(Path.Combine(workingDir, "Dockerfile")).Should().BeFalse("Expected no Dockerfile for Rust runtime");
        }

        [Fact]
        public void Init_WithRust_ListsRustAmongTheValidWorkerRuntimes()
        {
            var workingDir = WorkingDirectory;
            var testName = nameof(Init_WithRust_ListsRustAmongTheValidWorkerRuntimes);
            var funcInitCommand = new FuncInitCommand(FuncPath, testName, Log ?? throw new ArgumentNullException(nameof(Log)));

            // An invalid runtime prints the option list, which must now include rust right after go.
            var funcInitResult = funcInitCommand
                .WithWorkingDirectory(workingDir)
                .Execute(["--worker-runtime", "not-a-runtime"]);

            funcInitResult.Should().ExitWith(1);
            funcInitResult.Should().HaveStdErrContaining("go, rust");
        }

        // The shared .gitignore hides every folder named bin, and Cargo keeps extra binaries in src/bin. This runs
        // real git against the generated .gitignore, because only git can say what its rules do.
        [SkippableFact]
        public async Task Init_WithRust_GitIgnoreTracksCargoBinFolderAndIgnoresBuildOutput()
        {
            var workingDir = WorkingDirectory;
            Skip.If(await GitAsync(workingDir, "--version") < 0, "git is not installed.");
            await FuncInitWithRetryAsync(nameof(Init_WithRust_GitIgnoreTracksCargoBinFolderAndIgnoresBuildOutput), [".", "--worker-runtime", "rust"]);
            (await GitAsync(workingDir, "init -q")).Should().Be(0);

            foreach (var file in new[] { "src/bin/second.rs", "bin/app", "target/debug/app", "target/func/app" })
            {
                var path = Path.Combine(workingDir, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }

            // An empty file stands in for the user's global ignore rules, so only this project's .gitignore decides.
            var noGlobalRules = Path.Combine(workingDir, ".no-global-rules").Replace('\\', '/');
            File.WriteAllText(noGlobalRules, string.Empty);

            // 'git check-ignore -q' exits with 0 when the path is ignored and 1 when it is not.
            var expectedIgnored = new Dictionary<string, bool>
            {
                ["src/bin/second.rs"] = false,
                ["src/main.rs"] = false,
                ["Cargo.toml"] = false,
                ["bin/app"] = true,
                ["target/debug/app"] = true,
                ["target/func/app"] = true,
            };

            foreach (var (path, ignored) in expectedIgnored)
            {
                var exitCode = await GitAsync(workingDir, $"-c core.excludesFile=\"{noGlobalRules}\" check-ignore -q \"{path}\"");
                exitCode.Should().Be(ignored ? 0 : 1, $"'{path}' should {(ignored ? string.Empty : "not ")}be ignored by the generated .gitignore");
            }
        }

        // Runs git and returns its exit code, or -1 when git is not installed.
        private static async Task<int> GitAsync(string workingDirectory, string arguments)
        {
            try
            {
                return await new Executable("git", arguments, workingDirectory: workingDirectory).RunAsync();
            }
            catch (FileNotFoundException)
            {
                return -1;
            }
        }
    }
}
