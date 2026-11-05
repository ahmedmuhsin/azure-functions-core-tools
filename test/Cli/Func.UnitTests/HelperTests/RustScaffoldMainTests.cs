// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // The sample that 'func init' writes for a Rust app is written against crates that are not published, so a unit test cannot
    // compile it. These tests hold what a compile and a run against the worker library showed. The sample reads the startup
    // arguments of the worker only through the worker library, which reads both spellings that a launcher passes, and it uses no
    // named outputs, which the SDK removed.
    public class RustScaffoldMainTests
    {
        // The nine startup arguments that the worker library reads, the plain ones that the host passes and the prefixed ones that
        // a proxy in front of the host passes.
        private static readonly string[] _startupArguments =
        [
            "--host",
            "--port",
            "--workerId",
            "--requestId",
            "--grpcMaxMessageLength",
            "--functions-uri",
            "--functions-worker-id",
            "--functions-request-id",
            "--functions-grpc-max-message-length",
        ];

        [Fact]
        public async Task MainRs_ReadsItsArgumentsThroughTheWorkerLibrary()
        {
            var code = CodeOf(await StaticResources.MainRs);

            code.Should().Contain("StartupArguments::from_env()", "a comment that names the call does not read the arguments");
        }

        // The worker library holds its own examples to the same rule, and a clippy rule disallows the two calls outside the library.
        // StartupArguments::parse takes the arguments from its caller, so a sample that calls it has read them itself.
        [Fact]
        public async Task MainRs_DoesNotReadTheArgumentsOfTheProcessItself()
        {
            var source = await StaticResources.MainRs;

            source.Should().NotContain("env::args", "that also matches env::args_os, and the worker library reads the arguments in one place")
                .And.NotContain("StartupArguments::parse");
        }

        // A worker that reads one spelling only fails to start when a launcher passes the other one.
        [Fact]
        public async Task MainRs_NamesNoStartupArgument()
        {
            var source = await StaticResources.MainRs;

            var named = _startupArguments.Where(argument => source.Contains(argument, StringComparison.Ordinal)).ToList();

            named.Should().BeEmpty("the worker library reads the startup arguments, in both spellings, and the sample names none of them");
        }

        // StartupArguments carries the largest gRPC message that the launcher allows. It reaches the worker only through the Config.
        [Fact]
        public async Task MainRs_AppliesTheMessageLimitThatTheStartupArgumentsCarry()
        {
            var source = await StaticResources.MainRs;

            CodeOf(source).Should().Contain(".with_startup_arguments(&startup)");
            source.Should().NotContain("with_grpc_max_message_length", "that method of the Config reads one spelling of the argument");
        }

        [Fact]
        public async Task MainRs_ConnectsWithTheEndpointAndTheIdsOfTheStartupArguments()
        {
            var code = CodeOf(await StaticResources.MainRs);

            code.Should().Contain("startup.endpoint()")
                .And.Contain("startup.worker_id()")
                .And.Contain("startup.request_id()");
        }

        // The SDK removed named outputs. The response of the sample comes back as the return value, which is all that the shared
        // sample of the SDK uses, and an InvocationResult that names outputs does not compile against it.
        [Fact]
        public async Task MainRs_UsesNoNamedOutputs()
        {
            var source = await StaticResources.MainRs;

            source.Should().NotContain("outputs");
            CodeOf(source).Should().Contain("return_value");
        }

        // main.rs and Cargo.toml are two files of the scaffold, and a unit test cannot compile them together.
        [Theory]
        [InlineData("azure_functions_rust_sdk", "azure-functions-rust-sdk")]
        [InlineData("azure_functions_rust_worker", "azure-functions-rust-worker")]
        [InlineData("serde_json", "serde_json")]
        [InlineData("tokio", "tokio")]
        public async Task ScaffoldManifest_DeclaresTheCratesThatMainRsUses(string crateInSource, string crateInManifest)
        {
            var source = await StaticResources.MainRs;
            var manifest = await StaticResources.CargoToml;

            CodeOf(source).Should().Contain($"{crateInSource}::", "the sample uses the crate");
            manifest.Replace("\r\n", "\n").Split('\n')
                .Should().Contain(line => line.StartsWith($"{crateInManifest} = ", StringComparison.Ordinal));
        }

        // A comment is not a call. The checks for what the sample does look at its code only, and the checks for what it must not
        // contain look at the whole file.
        [Fact]
        public void CodeOf_LeavesOutTheTextOfComments()
        {
            var source = "// StartupArguments::from_env()\nlet a = 1; // startup.endpoint()\n  // run()\nlet b = 2;\n";

            var code = CodeOf(source);

            code.Should().Contain("let a = 1;").And.Contain("let b = 2;");
            code.Should().NotContain("from_env").And.NotContain("endpoint").And.NotContain("run()");
        }

        // The sample has no string that holds two slashes, so cutting a line at the first two slashes leaves all of its code.
        private static string CodeOf(string source)
        {
            var lines = new List<string>();
            foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                lines.Add(comment < 0 ? line : line[..comment]);
            }

            return string.Join("\n", lines);
        }
    }
}
