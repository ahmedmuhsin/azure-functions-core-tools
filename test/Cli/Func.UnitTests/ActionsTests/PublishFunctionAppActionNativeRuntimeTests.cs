// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Azure.Functions.Cli.Actions.AzureActions;
using Azure.Functions.Cli.Arm.Models;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.Interfaces;
using Moq;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.ActionsTests
{
    // The checks that 'func publish' makes on the Azure app before it uploads anything, for native apps, with fake
    // sites and no Azure. They run in the collection that does not run in parallel because the force path writes
    // a warning to the shared console, which other tests capture.
    [Collection("NativeWorkerRuntimeTests")]
    public class PublishFunctionAppActionNativeRuntimeTests
    {
        [Theory]
        [InlineData("flexconsumption", "go", null)]
        [InlineData("elasticpremium", null, "native")]
        [InlineData("premium", null, "native")]
        public async Task ValidateFunctionAppPublish_RustAppOnTheNativeStack_PassesWithoutChangingTheRuntime(string sku, string flexRuntimeName, string runtimeSetting)
        {
            using var root = new TempDirectory();
            var site = CreateLinuxApp(sku, flexRuntimeName, runtimeSetting, linuxFxVersion: sku == "premium" ? "Go|1.0" : null);
            var action = CreateAction();

            var settings = await action.ValidateFunctionAppPublish(site, WorkerRuntime.Rust, root.Path);

            Assert.False(settings.ContainsKey(Constants.FunctionsWorkerRuntime), "the app is already on the native stack, so no runtime setting changes");
        }

        [Theory]
        [InlineData("flexconsumption", "python", null, "'go'")]
        [InlineData("elasticpremium", null, "python", "'native'")]
        public async Task ValidateFunctionAppPublish_RustAppOnAnotherStack_ThrowsWithTheNativeSettingAsTheFix(string sku, string flexRuntimeName, string runtimeSetting, string expectedInHint)
        {
            using var root = new TempDirectory();
            var site = CreateLinuxApp(sku, flexRuntimeName, runtimeSetting);
            var action = CreateAction();

            var exception = await Assert.ThrowsAsync<CliException>(() => action.ValidateFunctionAppPublish(site, WorkerRuntime.Rust, root.Path));

            Assert.Contains($"'{Constants.FunctionsWorkerRuntime}' set to 'Python' while your local project is set to 'Rust'", exception.Message);
            Assert.Contains($"update your Azure app with {expectedInHint} as a '{Constants.FunctionsWorkerRuntime}'", exception.Message);
        }

        [Fact]
        public async Task ValidateFunctionAppPublish_RustAppOnAnotherStackWithForce_SetsTheNativeRuntime()
        {
            using var root = new TempDirectory();
            var site = CreateLinuxApp("elasticpremium", null, "python");
            var action = CreateAction();
            action.Force = true;

            var settings = await action.ValidateFunctionAppPublish(site, WorkerRuntime.Rust, root.Path);

            Assert.Equal("native", settings[Constants.FunctionsWorkerRuntime]);
        }

        [Theory]
        [InlineData("flexconsumption", "go", null)]
        [InlineData("elasticpremium", null, "native")]
        public async Task ValidateFunctionAppPublish_GoApp_StillPassesOnTheNativeStack(string sku, string flexRuntimeName, string runtimeSetting)
        {
            // Guards the shared runtime matching that Rust now goes through.
            using var root = new TempDirectory();
            var site = CreateLinuxApp(sku, flexRuntimeName, runtimeSetting);
            var action = CreateAction();

            var settings = await action.ValidateFunctionAppPublish(site, WorkerRuntime.Go, root.Path);

            Assert.False(settings.ContainsKey(Constants.FunctionsWorkerRuntime));
        }

        private static PublishFunctionAppAction CreateAction()
        {
            return new PublishFunctionAppAction(Mock.Of<ISettings>(), Mock.Of<ISecretsManager>());
        }

        // A Linux app as the management API returns it, with the host storage setting that publish requires.
        private static Site CreateLinuxApp(string sku, string flexRuntimeName, string runtimeSetting, string linuxFxVersion = null)
        {
            var settings = new Dictionary<string, string>
            {
                [Constants.AzureWebJobsStorage] = "UseDevelopmentStorage=true"
            };
            if (runtimeSetting != null)
            {
                settings[Constants.FunctionsWorkerRuntime] = runtimeSetting;
            }

            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = sku,
                LinuxFxVersion = linuxFxVersion,
                AzureAppSettings = settings
            };

            if (flexRuntimeName != null)
            {
                site.FunctionAppConfig = new FunctionAppConfig
                {
                    Runtime = new Runtime { Name = flexRuntimeName, Version = "1.0" }
                };
            }

            return site;
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-publish-" + Guid.NewGuid().ToString("N"));
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
