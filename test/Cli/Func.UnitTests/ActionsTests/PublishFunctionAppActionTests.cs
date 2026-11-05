// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Azure.Functions.Cli.Actions.AzureActions;
using Azure.Functions.Cli.Arm.Models;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.StacksApi;
using Moq;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.ActionsTests
{
    public class PublishFunctionAppActionTests
    {
        [Theory]
        [InlineData("flexconsumption")]
        [InlineData("elasticpremium")]
        [InlineData("premium")]
        public void ValidateGoPublishOptions_LinuxHostingSku_DoesNotThrow(string sku)
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = sku
            };

            var exception = Record.Exception(
                () => PublishFunctionAppAction.ValidateGoPublishOptions(site, BuildOption.Default, buildNativeDeps: false));

            Assert.Null(exception);
        }

        [Fact]
        public void ValidateGoPublishOptions_LinuxConsumption_Throws()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "dynamic"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateGoPublishOptions(site, BuildOption.Default, buildNativeDeps: false));

            Assert.Equal(
                "Go is not supported on Linux Consumption Function Apps. Use Flex Consumption, Elastic Premium, or a Dedicated Linux plan.",
                exception.Message);
        }

        [Fact]
        public void ValidateGoPublishOptions_WindowsApp_Throws()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp",
                Sku = "premium"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateGoPublishOptions(site, BuildOption.Default, buildNativeDeps: false));

            Assert.Equal("Go is only supported for Linux Function Apps.", exception.Message);
        }

        [Theory]
        [InlineData(BuildOption.Remote)]
        [InlineData(BuildOption.Container)]
        public void ValidateGoPublishOptions_UnsupportedBuildMode_Throws(BuildOption buildOption)
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "premium"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateGoPublishOptions(site, buildOption, buildNativeDeps: false));

            Assert.StartsWith($"--build {buildOption} is not supported for Go.", exception.Message);
        }

        [Fact]
        public void NormalizeFunctionAppWorkerRuntime_NativeGoApp_ReturnsGo()
        {
            var runtime = PublishFunctionAppAction.NormalizeFunctionAppWorkerRuntime("native", WorkerRuntime.Go);

            Assert.Equal(WorkerRuntime.Go, runtime);
        }

        [Fact]
        public void GetFunctionAppWorkerRuntimeSetting_Go_ReturnsNative()
        {
            var setting = PublishFunctionAppAction.GetFunctionAppWorkerRuntimeSetting(WorkerRuntime.Go);

            Assert.Equal("native", setting);
        }

        [Fact]
        public async Task UpdateFrameworkVersions_GoForcePublish_UsesGoLinuxFxVersion()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "elasticpremium",
                LinuxFxVersion = "Node|22"
            };
            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);
            helperServiceMock
                .Setup(x => x.UpdateWebSettings(site, It.IsAny<Dictionary<string, string>>()))
                .ReturnsAsync(new HttpResult<string, string>(string.Empty));

            await PublishFunctionAppAction.UpdateFrameworkVersions(site, WorkerRuntime.Go, null, true, helperServiceMock.Object);

            helperServiceMock.Verify(
                x => x.UpdateWebSettings(
                    site,
                    It.Is<Dictionary<string, string>>(settings =>
                        settings.Count == 1 && settings[Constants.LinuxFxVersion] == "Go|1.0")),
                Times.Once);
        }

        [Theory]
        [InlineData("flexconsumption")]
        [InlineData("elasticpremium")]
        [InlineData("premium")]
        public void ValidateRustPublishOptions_LinuxHostingSku_DoesNotThrow(string sku)
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = sku
            };

            var exception = Record.Exception(
                () => PublishFunctionAppAction.ValidateRustPublishOptions(site, BuildOption.Default, buildNativeDeps: false));

            Assert.Null(exception);
        }

        [Fact]
        public void ValidateRustPublishOptions_LinuxConsumption_Throws()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "dynamic"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateRustPublishOptions(site, BuildOption.Default, buildNativeDeps: false));

            Assert.Equal("Rust apps cannot be published to Linux Consumption Function Apps.", exception.Message);
        }

        [Fact]
        public void ValidateRustPublishOptions_WindowsApp_Throws()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp",
                Sku = "premium"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateRustPublishOptions(site, BuildOption.Default, buildNativeDeps: false));

            Assert.Equal("Rust apps can only be published to Linux Function Apps.", exception.Message);
        }

        [Theory]
        [InlineData(BuildOption.Remote)]
        [InlineData(BuildOption.Container)]
        public void ValidateRustPublishOptions_UnsupportedBuildMode_Throws(BuildOption buildOption)
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "premium"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateRustPublishOptions(site, buildOption, buildNativeDeps: false));

            Assert.Equal($"--build {buildOption} is not supported for Rust. Run 'func publish' without '--build {buildOption}'.", exception.Message);
        }

        [Fact]
        public void ValidateRustPublishOptions_BuildNativeDeps_Throws()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "premium"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateRustPublishOptions(site, BuildOption.Default, buildNativeDeps: true));

            Assert.Equal("--build-native-deps is not supported for Rust. Run 'func publish' without '--build-native-deps'.", exception.Message);
        }

        [Fact]
        public void ValidateGoPublishOptions_BuildNativeDeps_Throws()
        {
            // The Go checks share code with Rust, so pin that the Go text did not change.
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "premium"
            };

            var exception = Assert.Throws<CliException>(
                () => PublishFunctionAppAction.ValidateGoPublishOptions(site, BuildOption.Default, buildNativeDeps: true));

            Assert.Equal("--build-native-deps is not supported for Go. Run 'func publish' without '--build-native-deps'.", exception.Message);
        }

        [Theory]
        [InlineData("native", WorkerRuntime.Rust, WorkerRuntime.Rust)]
        [InlineData("NATIVE", WorkerRuntime.Rust, WorkerRuntime.Rust)]
        [InlineData("go", WorkerRuntime.Rust, WorkerRuntime.Rust)]
        [InlineData("python", WorkerRuntime.Rust, WorkerRuntime.Python)]
        [InlineData("node", WorkerRuntime.Rust, WorkerRuntime.Node)]
        [InlineData("native", WorkerRuntime.Go, WorkerRuntime.Go)]
        [InlineData("go", WorkerRuntime.Go, WorkerRuntime.Go)]
        public void NormalizeFunctionAppWorkerRuntime_NativeStack_MatchesTheLocalNativeLanguage(string azureValue, WorkerRuntime local, WorkerRuntime expected)
        {
            var runtime = PublishFunctionAppAction.NormalizeFunctionAppWorkerRuntime(azureValue, local);

            Assert.Equal(expected, runtime);
        }

        [Fact]
        public void NormalizeFunctionAppWorkerRuntime_NativeAppWithAPythonProject_IsNotAValidRuntime()
        {
            // A native Azure app is not a match for a project of another stack.
            Assert.Throws<ArgumentException>(
                () => PublishFunctionAppAction.NormalizeFunctionAppWorkerRuntime("native", WorkerRuntime.Python));
        }

        [Theory]
        [InlineData(WorkerRuntime.Rust, "native")]
        [InlineData(WorkerRuntime.Go, "native")]
        [InlineData(WorkerRuntime.Python, "python")]
        [InlineData(WorkerRuntime.Node, "node")]
        public void GetFunctionAppWorkerRuntimeSetting_ReturnsTheSettingOfTheApp(WorkerRuntime runtime, string expected)
        {
            Assert.Equal(expected, PublishFunctionAppAction.GetFunctionAppWorkerRuntimeSetting(runtime));
        }

        // Flex names the stack in functionAppConfig.runtime.name. A native app (unverified for Rust, no Rust app has run
        // on Flex yet) is expected on the same stack as a Go app, because the platform does not tell them apart.
        [Theory]
        [InlineData(true, WorkerRuntime.Rust, "go")]
        [InlineData(true, WorkerRuntime.Go, "go")]
        [InlineData(true, WorkerRuntime.Python, "python")]
        [InlineData(false, WorkerRuntime.Rust, "native")]
        [InlineData(false, WorkerRuntime.Go, "native")]
        [InlineData(false, WorkerRuntime.Node, "node")]
        public void GetExpectedFunctionAppWorkerRuntime_FlexUsesTheStackName(bool isFlex, WorkerRuntime runtime, string expected)
        {
            Assert.Equal(expected, PublishFunctionAppAction.GetExpectedFunctionAppWorkerRuntime(isFlex, runtime));
        }

        [Fact]
        public void GetLinuxFxVersionForWorkerRuntime_Rust_UsesTheNativeStack()
        {
            Assert.Equal("Go|1.0", PublishFunctionAppAction.GetLinuxFxVersionForWorkerRuntime(WorkerRuntime.Rust));
        }

        [Fact]
        public async Task UpdateFrameworkVersions_RustForcePublish_UsesTheNativeLinuxFxVersion()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "elasticpremium",
                LinuxFxVersion = "Node|22"
            };
            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);
            helperServiceMock
                .Setup(x => x.UpdateWebSettings(site, It.IsAny<Dictionary<string, string>>()))
                .ReturnsAsync(new HttpResult<string, string>(string.Empty));

            await PublishFunctionAppAction.UpdateFrameworkVersions(site, WorkerRuntime.Rust, null, true, helperServiceMock.Object);

            helperServiceMock.Verify(
                x => x.UpdateWebSettings(
                    site,
                    It.Is<Dictionary<string, string>>(settings =>
                        settings.Count == 1 && settings[Constants.LinuxFxVersion] == "Go|1.0")),
                Times.Once);
        }

        [Fact]
        public async Task UpdateFrameworkVersions_RustOnTheNativeStack_DoesNotUpdate()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "premium",
                LinuxFxVersion = "Go|1.0"
            };
            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);

            var exception = await Record.ExceptionAsync(
                () => PublishFunctionAppAction.UpdateFrameworkVersions(site, WorkerRuntime.Rust, null, false, helperServiceMock.Object));

            Assert.Null(exception);
            helperServiceMock.Verify(
                x => x.UpdateWebSettings(It.IsAny<Site>(), It.IsAny<Dictionary<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateFrameworkVersions_RustOnAnotherStack_ThrowsWithoutForce()
        {
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "premium",
                LinuxFxVersion = "Node|22"
            };
            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);

            var exception = await Assert.ThrowsAsync<CliException>(
                () => PublishFunctionAppAction.UpdateFrameworkVersions(site, WorkerRuntime.Rust, null, false, helperServiceMock.Object));

            Assert.Contains("Node|22", exception.Message);
            Assert.Contains("--force", exception.Message);
        }

        [Theory]
        [InlineData(WorkerRuntime.Rust, WorkerRuntime.Go)]
        [InlineData(WorkerRuntime.Go, WorkerRuntime.Go)]
        [InlineData(WorkerRuntime.Python, WorkerRuntime.Python)]
        [InlineData(WorkerRuntime.None, WorkerRuntime.None)]
        public void GetPlatformWorkerRuntime_NativeAppsUseTheGoStack(WorkerRuntime runtime, WorkerRuntime expected)
        {
            Assert.Equal(expected, WorkerRuntimeLanguageHelper.GetPlatformWorkerRuntime(runtime));
        }

        [Theory]
        [InlineData("functionapp", "11.0", "netFrameworkVersion", "v11.0")]
        [InlineData("functionapp", "v11.0", "netFrameworkVersion", "v11.0")]
        [InlineData("functionapp,linux", "11.0", "linuxFxVersion", "DOTNET-ISOLATED|11.0")]
        [InlineData("functionapp,linux", "v11.0", "linuxFxVersion", "DOTNET-ISOLATED|11.0")]
        public async Task UpdateFrameworkVersions_Net11_UsesPlatformVersionFormat(string kind, string version, string setting, string expected)
        {
            var site = new Site("test-site")
            {
                Kind = kind,
                Sku = "dynamic",
                NetFrameworkVersion = "v10.0",
                LinuxFxVersion = "DOTNET-ISOLATED|10.0"
            };
            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);
            helperServiceMock
                .Setup(x => x.UpdateWebSettings(site, It.IsAny<Dictionary<string, string>>()))
                .ReturnsAsync(new HttpResult<string, string>(string.Empty));

            await PublishFunctionAppAction.UpdateFrameworkVersions(site, WorkerRuntime.DotnetIsolated, version, false, helperServiceMock.Object);

            helperServiceMock.Verify(
                x => x.UpdateWebSettings(site, It.Is<Dictionary<string, string>>(settings => settings.Count == 1 && settings[setting] == expected)),
                Times.Once);
        }

        [Theory]
        [InlineData("functionapp")]
        [InlineData("functionapp,linux")]
        public async Task UpdateFrameworkVersions_Net11AlreadyConfigured_DoesNotUpdate(string kind)
        {
            var site = new Site("test-site")
            {
                Kind = kind,
                Sku = "dynamic",
                NetFrameworkVersion = "v11.0",
                LinuxFxVersion = "DOTNET-ISOLATED|11.0"
            };
            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);

            await PublishFunctionAppAction.UpdateFrameworkVersions(site, WorkerRuntime.DotnetIsolated, "11.0", false, helperServiceMock.Object);

            helperServiceMock.Verify(
                x => x.UpdateWebSettings(It.IsAny<Site>(), It.IsAny<Dictionary<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateRuntimeConfigForFlex_SkipsUpdate_WhenNoRuntimeVersionDetected()
        {
            // Arrange
            var site = new Site("test-site")
            {
                Location = "eastus",
                FunctionAppConfig = new FunctionAppConfig
                {
                    Runtime = new Runtime
                    {
                        Name = "dotnet-isolated",
                        Version = "9.0"
                    }
                }
            };

            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);

            // Act - passing null for runtimeVersion should skip the update
            await PublishFunctionAppAction.UpdateRuntimeConfigForFlex(
                site,
                "dotnet-isolated",
                null, // No version detected
                helperServiceMock.Object,
                force: false,
                overwriteSettings: false);

            // Assert - UpdateFlexRuntime should not be called
            helperServiceMock.Verify(
                x => x.UpdateFlexRuntime(It.IsAny<Site>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateRuntimeConfigForFlex_SkipsUpdate_WhenVersionsMatch()
        {
            // Arrange
            var site = new Site("test-site")
            {
                Location = "eastus",
                FunctionAppConfig = new FunctionAppConfig
                {
                    Runtime = new Runtime
                    {
                        Name = "dotnet-isolated",
                        Version = "9.0"
                    }
                }
            };

            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);
            helperServiceMock
                .Setup(x => x.GetFlexFunctionsStacks(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(CreateMockFlexStacks());

            // Act - passing same version should skip the update
            await PublishFunctionAppAction.UpdateRuntimeConfigForFlex(
                site,
                "dotnet-isolated",
                "9.0", // Same version as Azure
                helperServiceMock.Object,
                force: false,
                overwriteSettings: false);

            // Assert - UpdateFlexRuntime should not be called
            helperServiceMock.Verify(
                x => x.UpdateFlexRuntime(It.IsAny<Site>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateRuntimeConfigForFlex_UpdatesRuntime_WhenVersionsDifferAndForceIsTrue()
        {
            // Arrange
            var site = new Site("test-site")
            {
                Location = "eastus",
                FunctionAppConfig = new FunctionAppConfig
                {
                    Runtime = new Runtime
                    {
                        Name = "dotnet-isolated",
                        Version = "8.0"
                    }
                }
            };

            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);
            helperServiceMock
                .Setup(x => x.GetFlexFunctionsStacks(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(CreateMockFlexStacks());

            // Act - different version with force should update
            await PublishFunctionAppAction.UpdateRuntimeConfigForFlex(
                site,
                "dotnet-isolated",
                "9.0", // Different version from Azure (8.0)
                helperServiceMock.Object,
                force: true, // Force the update
                overwriteSettings: false);

            // Assert - UpdateFlexRuntime should be called
            helperServiceMock.Verify(
                x => x.UpdateFlexRuntime(site, "dotnet-isolated", "9.0"),
                Times.Once);
        }

        [Theory]
        [InlineData(WorkerRuntime.Powershell)]
        [InlineData(WorkerRuntime.Node)]
        [InlineData(WorkerRuntime.Python)]
        [InlineData(WorkerRuntime.Java)]
        public async Task UpdateFrameworkVersions_NonDotnetWindowsApp_NoDotnetVersion_DoesNotCallUpdateWebSettings(WorkerRuntime workerRuntime)
        {
            // Arrange — Windows function app, non-.NET runtime, no --dotnet-version specified.
            // This is the exact scenario that caused ArgumentNullException in v4.7.0.
            var site = new Site("test-site")
            {
                Kind = "functionapp",
                Sku = "dynamic",
                NetFrameworkVersion = "v6.0"
            };

            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);

            // Act — null dotnetVersion means --dotnet-version was not specified
            var exception = await Record.ExceptionAsync(() =>
                PublishFunctionAppAction.UpdateFrameworkVersions(site, workerRuntime, null, false, helperServiceMock.Object));

            // Assert — should not throw and should never attempt to update web settings
            Assert.Null(exception);
            helperServiceMock.Verify(
                x => x.UpdateWebSettings(It.IsAny<Site>(), It.IsAny<Dictionary<string, string>>()),
                Times.Never);
        }

        [Theory]
        [InlineData(WorkerRuntime.Powershell)]
        [InlineData(WorkerRuntime.Node)]
        [InlineData(WorkerRuntime.Python)]
        [InlineData(WorkerRuntime.Java)]
        public async Task UpdateFrameworkVersions_NonDotnetWindowsApp_ExplicitDotnetVersion_DoesNotUpdateWebSettings(WorkerRuntime workerRuntime)
        {
            // Arrange — Windows function app, non-.NET runtime, explicit --dotnet-version 8.0.
            // --dotnet-version only applies to DotnetIsolated (per its own help text), so even
            // when specified, non-.NET runtimes should not attempt to update NetFrameworkVersion.
            var site = new Site("test-site")
            {
                Kind = "functionapp",
                Sku = "dynamic",
                NetFrameworkVersion = "v6.0"
            };

            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);

            // Act
            var exception = await Record.ExceptionAsync(() =>
                PublishFunctionAppAction.UpdateFrameworkVersions(site, workerRuntime, "8.0", false, helperServiceMock.Object));

            // Assert — should not throw and should not try to update web settings
            Assert.Null(exception);
            helperServiceMock.Verify(
                x => x.UpdateWebSettings(It.IsAny<Site>(), It.IsAny<Dictionary<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task UpdateFrameworkVersions_NonDotnetLinuxApp_NoDotnetVersion_DoesNotThrow()
        {
            // Arrange — Linux dynamic function app, non-.NET runtime.
            // Linux dynamic apps don't hit UpdateNetFrameworkVersionWindows at all,
            // but verify this path is also safe.
            var site = new Site("test-site")
            {
                Kind = "functionapp,linux",
                Sku = "dynamic"
            };

            var helperServiceMock = new Mock<PublishFunctionAppAction.AzureHelperService>(null, null);

            var exception = await Record.ExceptionAsync(() =>
                PublishFunctionAppAction.UpdateFrameworkVersions(site, WorkerRuntime.Node, null, false, helperServiceMock.Object));

            Assert.Null(exception);
            helperServiceMock.Verify(
                x => x.UpdateWebSettings(It.IsAny<Site>(), It.IsAny<Dictionary<string, string>>()),
                Times.Never);
        }

        private static FlexFunctionsStacks CreateMockFlexStacks()
        {
            return new FlexFunctionsStacks
            {
                Languages = new List<FlexLanguage>
                {
                    new FlexLanguage
                    {
                        LanguageProperties = new FlexLanguageProperties
                        {
                            MajorVersions = new List<FlexMajorVersion>
                            {
                                new FlexMajorVersion
                                {
                                    MinorVersions = new List<FlexMinorVersion>
                                    {
                                        new FlexMinorVersion
                                        {
                                            StackSettings = new FlexStackSettings
                                            {
                                                LinuxRuntimeSettings = new FlexLinuxRuntimeSettings
                                                {
                                                    Sku = new List<FlexSku>
                                                    {
                                                        new FlexSku
                                                        {
                                                            SkuCode = "FC1",
                                                            FunctionAppConfigProperties = new FunctionAppConfigProperties
                                                            {
                                                                Runtime = new FlexRuntime
                                                                {
                                                                    Name = "dotnet-isolated",
                                                                    Version = "9.0"
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }
    }
}
