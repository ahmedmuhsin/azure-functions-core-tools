// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.IO.Abstractions;
using AwesomeAssertions;
using Azure.Functions.Cli.Actions.LocalActions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.ConfigurationProfiles;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.Interfaces;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.ActionsTests
{
    [Collection("NativeWorkerRuntimeTests")]
    public class InitActionRustTests
    {
        private static InitAction CreateAction()
        {
            var templatesManager = Substitute.For<ITemplatesManager>();
            var secretsManager = Substitute.For<ISecretsManager>();
            var configProfiles = new List<IConfigurationProfile>();
            return new InitAction(templatesManager, secretsManager, configProfiles);
        }

        private static ISecretsManager SecretsWith(params (string Key, string Value)[] values)
        {
            var secretsManager = Substitute.For<ISecretsManager>();
            var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
            {
                secrets[key] = value;
            }

            secretsManager.GetSecrets(Arg.Any<bool>()).Returns(secrets);
            return secretsManager;
        }

        private static IFileSystem FileSystemWithFiles(params string[] existingFileNames)
        {
            var fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Exists(Arg.Any<string>()).Returns(false);
            foreach (var name in existingFileNames)
            {
                fileSystem.File.Exists(Arg.Is<string>(p => p.EndsWith(name))).Returns(true);
            }

            return fileSystem;
        }

        // Runs the action with environment variables set for the duration of the call, then restores them.
        private static T WithEnvironment<T>(IDictionary<string, string> variables, Func<T> body)
        {
            var previous = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var pair in variables)
                {
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                }

                return body();
            }
            finally
            {
                foreach (var pair in previous)
                {
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                }
            }
        }

        [Theory]
        [InlineData("rust")]
        [InlineData("Rust")]
        [InlineData("RUST")]
        public void NormalizeWorkerRuntime_RustCasings_ResolveToRust(string input)
        {
            var result = WorkerRuntimeLanguageHelper.NormalizeWorkerRuntime(input);

            result.Should().Be(WorkerRuntime.Rust);
        }

        [Fact]
        public void GetRuntimeMoniker_Rust_ReturnsRust()
        {
            var result = WorkerRuntimeLanguageHelper.GetRuntimeMoniker(WorkerRuntime.Rust);

            result.Should().Be("rust");
        }

        [Fact]
        public void AvailableWorkersRuntimeString_ListsRustRightAfterGo()
        {
            WorkerRuntimeLanguageHelper.AvailableWorkersRuntimeString.Should().Contain("go, rust");
        }

        [Fact]
        public void ProgrammingModelHelper_Rust_DefaultsToV1()
        {
            var previous = GlobalCoreToolsSettings.CurrentProgrammingModel;
            try
            {
                GlobalCoreToolsSettings.CurrentProgrammingModel = null;

                var supportedModels = ProgrammingModelHelper.GetSupportedProgrammingModels(WorkerRuntime.Rust);
                supportedModels.Should().Contain(ProgrammingModel.V1);

                var resolved = ProgrammingModelHelper.ResolveProgrammingModel(null, WorkerRuntime.Rust, string.Empty);
                resolved.Should().Be(ProgrammingModel.V1);
            }
            finally
            {
                GlobalCoreToolsSettings.CurrentProgrammingModel = previous;
            }
        }

        [Theory]
        [InlineData("native")]
        [InlineData("Native")]
        [InlineData("NATIVE")]
        public void GetCurrentWorkerRuntimeLanguage_NativeWithCargoToml_ResolvesToRust(string settingValue)
        {
            var secretsManager = SecretsWith((Constants.FunctionsWorkerRuntime, settingValue));
            var fileSystem = FileSystemWithFiles("Cargo.toml");

            var resolved = WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsWorkerRuntime] = null,
                    [Constants.FunctionsCliNativeLanguage] = null,
                },
                () =>
                {
                    using (FileSystemHelpers.Override(fileSystem))
                    {
                        return WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager);
                    }
                });

            resolved.Should().Be(WorkerRuntime.Rust);
        }

        [Fact]
        public void GetCurrentWorkerRuntimeLanguage_NativeWithGoModAndCargoToml_KeepsResolvingToGo()
        {
            // A Go app that also holds a Rust crate (FFI, tooling) must not change runtime.
            var secretsManager = SecretsWith((Constants.FunctionsWorkerRuntime, "native"));
            var fileSystem = FileSystemWithFiles("go.mod", "Cargo.toml");

            var resolved = WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsWorkerRuntime] = null,
                    [Constants.FunctionsCliNativeLanguage] = null,
                },
                () =>
                {
                    using (FileSystemHelpers.Override(fileSystem))
                    {
                        return WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager);
                    }
                });

            resolved.Should().Be(WorkerRuntime.Go);
        }

        [Fact]
        public void GetCurrentWorkerRuntimeLanguage_NativeWithoutAnyMarker_ThrowsNamingBothMarkersAndBothValues()
        {
            var secretsManager = SecretsWith((Constants.FunctionsWorkerRuntime, "native"));
            var fileSystem = FileSystemWithFiles();

            Action act = () => WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsWorkerRuntime] = null,
                    [Constants.FunctionsCliNativeLanguage] = null,
                },
                () =>
                {
                    using (FileSystemHelpers.Override(fileSystem))
                    {
                        return WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager);
                    }
                });

            act.Should().Throw<CliException>()
                .Which.Message.Should().Contain("native")
                .And.Contain("go.mod")
                .And.Contain("Cargo.toml")
                .And.Contain($"{Constants.FunctionsCliNativeLanguage}=rust");
        }

        [Theory]
        [InlineData("rust")]
        [InlineData("Rust")]
        [InlineData("RUST")]
        public void GetCurrentWorkerRuntimeLanguage_RustEnvVar_ResolvesToRust(string envValue)
        {
            // The explicit selector needs no marker file and no FUNCTIONS_WORKER_RUNTIME.
            var secretsManager = SecretsWith();

            var resolved = WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsCliNativeLanguage] = envValue,
                    [Constants.FunctionsWorkerRuntime] = null,
                },
                () => WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager));

            resolved.Should().Be(WorkerRuntime.Rust);
        }

        [Theory]
        [InlineData("rust")]
        [InlineData("Rust")]
        [InlineData("RUST")]
        public void GetCurrentWorkerRuntimeLanguage_RustSetting_ResolvesToRust(string settingValue)
        {
            // local.settings.json wins when the env var is not set, without any Cargo.toml scan.
            var secretsManager = SecretsWith(
                (Constants.FunctionsCliNativeLanguage, settingValue),
                (Constants.FunctionsWorkerRuntime, "native"));

            var resolved = WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsCliNativeLanguage] = null,
                    [Constants.FunctionsWorkerRuntime] = null,
                },
                () => WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager));

            resolved.Should().Be(WorkerRuntime.Rust);
        }

        [Fact]
        public void GetCurrentWorkerRuntimeLanguage_RustEnvVar_BeatsSecretsManagerThrow()
        {
            // SecretsManager throwing (for example a command run from outside a project root)
            // must not mask an explicit env-var selection.
            var secretsManager = Substitute.For<ISecretsManager>();
            secretsManager.GetSecrets(Arg.Any<bool>()).Returns(_ => throw new CliException("no project"));

            var resolved = WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsCliNativeLanguage] = "rust",
                    [Constants.FunctionsWorkerRuntime] = null,
                },
                () => WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager));

            resolved.Should().Be(WorkerRuntime.Rust);
        }

        [Theory]
        [InlineData("rust", "go", WorkerRuntime.Rust)]
        [InlineData("go", "rust", WorkerRuntime.Go)]
        public void GetCurrentWorkerRuntimeLanguage_EnvVarWinsOverLocalSettings(string envValue, string settingValue, WorkerRuntime expected)
        {
            var secretsManager = SecretsWith(
                (Constants.FunctionsCliNativeLanguage, settingValue),
                (Constants.FunctionsWorkerRuntime, "native"));

            var resolved = WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsCliNativeLanguage] = envValue,
                    [Constants.FunctionsWorkerRuntime] = null,
                },
                () => WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager));

            resolved.Should().Be(expected);
        }

        [Fact]
        public void GetCurrentWorkerRuntimeLanguage_UnknownEnvValue_StillReadsLocalSettingsForRust()
        {
            // An unrecognized env value must not short-circuit and hide a valid setting.
            var secretsManager = SecretsWith(
                (Constants.FunctionsCliNativeLanguage, "rust"),
                (Constants.FunctionsWorkerRuntime, "native"));

            var resolved = WithEnvironment(
                new Dictionary<string, string>
                {
                    [Constants.FunctionsCliNativeLanguage] = "cpp",
                    [Constants.FunctionsWorkerRuntime] = null,
                },
                () => WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(secretsManager));

            resolved.Should().Be(WorkerRuntime.Rust);
        }

        [Theory]
        [InlineData("--rust")]
        public void GlobalCoreToolsSettings_Init_RustShortcutFlag_SetsCurrentWorkerRuntimeToRust(string flag)
        {
            var previousRuntime = GlobalCoreToolsSettings.CurrentWorkerRuntimeOrNone;
            var previousEnv = Environment.GetEnvironmentVariable(Constants.FunctionsWorkerRuntime);
            Environment.SetEnvironmentVariable(Constants.FunctionsWorkerRuntime, null);
            try
            {
                GlobalCoreToolsSettings.Init(secretsManager: null, args: new[] { flag });

                GlobalCoreToolsSettings.CurrentWorkerRuntimeOrNone.Should().Be(WorkerRuntime.Rust);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Constants.FunctionsWorkerRuntime, previousEnv);
                GlobalCoreToolsSettings.CurrentWorkerRuntime = previousRuntime;
            }
        }

        [Fact]
        public async Task RunAsync_WithRustWorkerRuntime_ScaffoldsProjectOnDisk()
        {
            using var project = new TempProjectDirectory();
            var action = CreateAction();
            action.ParseArgs(new[] { "--worker-runtime", "rust" });

            await action.RunAsync();

            File.Exists(Path.Combine(project.Path, "Cargo.toml")).Should().BeTrue();
            File.Exists(Path.Combine(project.Path, "src", "main.rs")).Should().BeTrue();
            File.Exists(Path.Combine(project.Path, ".funcignore")).Should().BeTrue();
            File.Exists(Path.Combine(project.Path, "host.json")).Should().BeTrue();
            File.Exists(Path.Combine(project.Path, ".vscode", "extensions.json")).Should().BeTrue();
            File.Exists(Path.Combine(project.Path, "Dockerfile")).Should().BeFalse();
            File.Exists(Path.Combine(project.Path, "go.mod")).Should().BeFalse();

            // The explicit selector is written so later commands do not have to scan for Cargo.toml.
            var settings = JObject.Parse(File.ReadAllText(Path.Combine(project.Path, "local.settings.json")));
            var values = (JObject)settings["Values"];
            values[Constants.FunctionsWorkerRuntime].Value<string>().Should().Be("native");
            values[Constants.FunctionsCliNativeLanguage].Value<string>().Should().Be("rust");
            values[Constants.AzureWebJobsStorage].Value<string>().Should().BeEmpty();

            File.ReadAllText(Path.Combine(project.Path, "host.json")).Should().Contain("extensionBundle");

            // The shared .gitignore still ignores bin/ (where the worker binary goes), and now target/ too.
            // src/bin, where Cargo keeps extra binaries, is let back in after every rule that would hide it.
            var gitIgnoreLines = File.ReadAllLines(Path.Combine(project.Path, ".gitignore")).Select(l => l.Trim()).ToList();
            gitIgnoreLines.Should().Contain("bin/");
            gitIgnoreLines.Should().Contain("target/");
            gitIgnoreLines.Should().Contain("!src/bin/");
            gitIgnoreLines.LastIndexOf("!src/bin/").Should().BeGreaterThan(gitIgnoreLines.LastIndexOf("**/bin/"), "the last matching rule wins in a .gitignore");
        }

        [Fact]
        public async Task RunAsync_WithRustWorkerRuntimeAndNoBundle_OmitsExtensionBundle()
        {
            using var project = new TempProjectDirectory();
            var action = CreateAction();
            action.ParseArgs(new[] { "--worker-runtime", "rust", "--no-bundle" });

            await action.RunAsync();

            File.ReadAllText(Path.Combine(project.Path, "host.json")).Should().NotContain("extensionBundle");
        }

        [Fact]
        public async Task RunAsync_WithRustWorkerRuntimeAndDocker_FailsBeforeWritingAnything()
        {
            using var project = new TempProjectDirectory();
            var action = CreateAction();
            action.ParseArgs(new[] { "--worker-runtime", "rust", "--docker" });

            Func<Task> act = () => action.RunAsync();

            (await act.Should().ThrowAsync<CliException>())
                .Which.Message.Should().Be("Docker is not available for the Rust worker runtime.");
            File.Exists(Path.Combine(project.Path, "Dockerfile")).Should().BeFalse();
            File.Exists(Path.Combine(project.Path, "Cargo.toml")).Should().BeFalse();
        }

        // Switches the process working directory to a fresh temp folder, and restores it with the
        // global settings that InitAction mutates, so the in-process init runs are isolated.
        private sealed class TempProjectDirectory : IDisposable
        {
            private readonly string _originalDirectory = Environment.CurrentDirectory;
            private readonly ProgrammingModel? _originalModel = GlobalCoreToolsSettings.CurrentProgrammingModel;
            private readonly WorkerRuntime _originalRuntime = GlobalCoreToolsSettings.CurrentWorkerRuntimeOrNone;

            public TempProjectDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-init-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
                Environment.CurrentDirectory = Path;
                GlobalCoreToolsSettings.CurrentProgrammingModel = null;
            }

            public string Path { get; }

            public void Dispose()
            {
                Environment.CurrentDirectory = _originalDirectory;
                GlobalCoreToolsSettings.CurrentProgrammingModel = _originalModel;
                GlobalCoreToolsSettings.CurrentWorkerRuntime = _originalRuntime;

                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a scanner may still hold a handle on a freshly written file.
                }
            }
        }
    }
}
