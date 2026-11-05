// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.Actions.LocalActions.PackAction;
using Azure.Functions.Cli.Common;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.ActionsTests.PackAction
{
    [Collection("NativeWorkerRuntimeTests")]
    public class RustPackSubcommandActionTests : IDisposable
    {
        private const string CargoToml = "[package]\nname = \"test\"\nversion = \"0.1.0\"\nedition = \"2024\"\n";

        private readonly string _tempDirectory;

        public RustPackSubcommandActionTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Fact]
        public void ValidateFunctionApp_ValidStructure_PassesValidation()
        {
            File.WriteAllText(Path.Combine(_tempDirectory, "host.json"), "{}");
            File.WriteAllText(Path.Combine(_tempDirectory, "Cargo.toml"), CargoToml);

            var action = new RustPackSubcommandAction();
            var validate = () => action.ValidateFunctionApp(_tempDirectory, new PackOptions());

            validate.Should().NotThrow();
        }

        [Fact]
        public void ValidateFunctionApp_MissingHostJson_DoesNotThrow()
        {
            // host.json is optional. Rust pack should still validate on Cargo.toml alone.
            File.WriteAllText(Path.Combine(_tempDirectory, "Cargo.toml"), CargoToml);

            var action = new RustPackSubcommandAction();
            var validate = () => action.ValidateFunctionApp(_tempDirectory, new PackOptions());

            validate.Should().NotThrow();
        }

        [Fact]
        public void ValidateFunctionApp_MissingCargoToml_ThrowsCliException()
        {
            File.WriteAllText(Path.Combine(_tempDirectory, "host.json"), "{}");

            var action = new RustPackSubcommandAction();
            var validate = () => action.ValidateFunctionApp(_tempDirectory, new PackOptions());

            validate.Should().Throw<CliException>()
                .Which.Message.Should().Contain("Cargo.toml");
        }

        [Fact]
        public void RunAsync_WithoutOptions_IsNotSupported()
        {
            // Dispatch happens through PackAction, which passes the options.
            var action = new RustPackSubcommandAction();

            Action run = () => action.RunAsync();

            run.Should().Throw<NotSupportedException>();
        }
    }
}
