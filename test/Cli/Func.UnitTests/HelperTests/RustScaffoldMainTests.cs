// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // The sample that 'func init' writes for a Rust app is written against a crate that is not published, so a unit test cannot
    // compile it. These tests hold what a compile and a run against the crate showed. The sample is built on the one
    // azure-functions-rust crate, which reads the startup arguments of the worker itself, so the sample names none of them.
    public class RustScaffoldMainTests
    {
        // The nine startup arguments that the worker reads, the plain ones that the host passes and the prefixed ones that
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
        public async Task MainRs_BuildsTheAppWithTheCrateOfTheFacade()
        {
            var code = CodeOf(await StaticResources.MainRs);

            code.Should().Contain("use azure_functions_rust::prelude::*;")
                .And.Contain("app()")
                .And.Contain(".http(")
                .And.Contain(".run()");
        }

        // The lower level crates are an implementation detail of the one crate that the sample uses.
        [Theory]
        [InlineData("azure_functions_rust_sdk")]
        [InlineData("azure_functions_rust_worker")]
        [InlineData("serde_json")]
        [InlineData("tokio")]
        public async Task MainRs_UsesNoLowerLevelCrate(string crate)
        {
            var source = await StaticResources.MainRs;

            source.Should().NotContain(crate);
        }

        // A worker that reads one spelling only fails to start when a launcher passes the other one.
        [Fact]
        public async Task MainRs_NamesNoStartupArgument()
        {
            var source = await StaticResources.MainRs;

            var named = _startupArguments.Where(argument => source.Contains(argument, StringComparison.Ordinal)).ToList();

            named.Should().BeEmpty("the crate reads the startup arguments, in both spellings, and the sample names none of them");
            source.Should().NotContain("env::args");
        }

        // A timer trigger needs storage, and 'func init' leaves AzureWebJobsStorage empty. The host cannot start its listener
        // then, so the sample registers an HTTP function only.
        [Fact]
        public async Task MainRs_RegistersAnHttpFunctionOnly()
        {
            var code = CodeOf(await StaticResources.MainRs);

            code.Should().NotContain(".timer(");
            code.Should().NotContain("TimerTrigger");
        }

        // main.rs and Cargo.toml are two files of the scaffold, and a unit test cannot compile them together.
        [Fact]
        public async Task ScaffoldManifest_DeclaresTheCrateThatMainRsUsesWithTheWorkerFeature()
        {
            var source = await StaticResources.MainRs;
            var manifest = ManifestLines(await StaticResources.CargoToml);

            CodeOf(source).Should().Contain("azure_functions_rust::", "the sample uses the crate");
            manifest.Should().ContainSingle(line => line.StartsWith("azure-functions-rust = ", StringComparison.Ordinal));
            manifest.Single(line => line.StartsWith("azure-functions-rust = ", StringComparison.Ordinal))
                .Should().Contain("features = [\"worker\"]", "the worker feature is the one that runs the app");
        }

        [Fact]
        public async Task ScaffoldManifest_HasThatDependencyOnly()
        {
            var manifest = ManifestLines(await StaticResources.CargoToml);

            var dependencies = manifest
                .SkipWhile(line => line != "[dependencies]")
                .Skip(1)
                .TakeWhile(line => !line.StartsWith('['))
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToList();

            dependencies.Should().ContainSingle().Which.Should().StartWith("azure-functions-rust = ");
        }

        // A comment is not a call. The checks for what the sample does look at its code only, and the checks for what it must not
        // contain look at the whole file.
        [Fact]
        public void CodeOf_LeavesOutTheTextOfComments()
        {
            var source = "// app()\nlet a = 1; // .http(\n  // run()\nlet b = 2;\n";

            var code = CodeOf(source);

            code.Should().Contain("let a = 1;").And.Contain("let b = 2;");
            code.Should().NotContain("app()").And.NotContain(".http(").And.NotContain("run()");
        }

        private static List<string> ManifestLines(string manifest)
        {
            return manifest.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()).ToList();
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
