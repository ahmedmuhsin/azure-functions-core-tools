// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.E2ETests.Traits;
using Azure.Functions.Cli.TestFramework.Assertions;
using Azure.Functions.Cli.TestFramework.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Azure.Functions.Cli.E2ETests.Commands.FuncNew
{
    [Trait(WorkerRuntimeTraits.WorkerRuntime, WorkerRuntimeTraits.Rust)]
    public class RustFuncNewTests(ITestOutputHelper log) : BaseE2ETests(log)
    {
        [Fact]
        public void FuncNew_InRustWorkspace_FailsWithNotSupportedMessage_AndDoesNotMutateLocalSettings()
        {
            var workingDir = WorkingDirectory;
            var testName = nameof(FuncNew_InRustWorkspace_FailsWithNotSupportedMessage_AndDoesNotMutateLocalSettings);

            // Arrange: scaffold a Rust workspace. local.settings.json must end up with FUNCTIONS_WORKER_RUNTIME=native.
            var initResult = new FuncInitCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(workingDir)
                .Execute(["--worker-runtime", "rust"]);
            initResult.Should().ExitWith(0);

            var localSettingsPath = Path.Combine(workingDir, Common.Constants.LocalSettingsJsonFileName);
            string before = File.ReadAllText(localSettingsPath);
            before.Should().Contain("\"native\"", "init writes FUNCTIONS_WORKER_RUNTIME=native for Rust projects");

            // Act: 'func new' in a Rust workspace must bail with a clear message before any language or
            // template prompt, because Rust registers functions in code.
            var newResult = new FuncNewCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(workingDir)
                .Execute([".", "--template", "HttpTrigger", "--name", "MyFunc"]);

            // Assert: friendly error surfaces and local.settings.json is untouched.
            newResult.Should().HaveStdErrContaining("The 'func new' command is not available for the Rust runtime.");
            string after = File.ReadAllText(localSettingsPath);
            after.Should().Be(before, "func new must not mutate local.settings.json for unsupported runtimes");
            Directory.Exists(Path.Combine(workingDir, "MyFunc")).Should().BeFalse("no function directory should be created");
        }

        [Fact]
        public void FuncNew_WithWorkerRuntimeRustFlag_InEmptyDir_FailsWithNotSupportedMessage()
        {
            var workingDir = WorkingDirectory;
            var testName = nameof(FuncNew_WithWorkerRuntimeRustFlag_InEmptyDir_FailsWithNotSupportedMessage);

            // 'func new --worker-runtime rust' in an empty dir would normally trigger an implicit
            // 'func init' before scaffolding the function. The Rust guard should fire after init resolves
            // the runtime to Rust, before any function-template work runs.
            var result = new FuncNewCommand(FuncPath, testName, Log)
                .WithWorkingDirectory(workingDir)
                .Execute([".", "--worker-runtime", "rust", "--template", "HttpTrigger", "--name", "MyFunc"]);

            result.Should().HaveStdErrContaining("The 'func new' command is not available for the Rust runtime.");
            Directory.Exists(Path.Combine(workingDir, "MyFunc")).Should().BeFalse("no function directory should be created");
        }
    }
}
