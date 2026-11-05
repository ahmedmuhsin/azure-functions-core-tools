// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Azure.Functions.Cli.Actions.AzureActions;
using Azure.Functions.Cli.Helpers;
using Colors.Net;
using NSubstitute;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.ActionsTests
{
    // Native apps (Go and Rust) publish an explicit allowlist, so 'func publish --list-included-files'
    // and '--list-ignored-files' print that list and do not consult .funcignore.
    [Collection("NativeWorkerRuntimeTests")]
    public class PublishFunctionAppActionNativeFileListTests : IDisposable
    {
        private readonly string _originalDirectory = Environment.CurrentDirectory;
        private readonly WorkerRuntime _originalRuntime = GlobalCoreToolsSettings.CurrentWorkerRuntimeOrNone;
        private readonly string _root;

        public PublishFunctionAppActionNativeFileListTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "func-publish-list-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            Environment.CurrentDirectory = _originalDirectory;
            GlobalCoreToolsSettings.CurrentWorkerRuntime = _originalRuntime;
            Directory.Delete(_root, recursive: true);
        }

        [Fact]
        public void ListIncludedFiles_Rust_PrintsTheAllowlistAndMarksTheBinaryThatIsNotBuiltYet()
        {
            File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname = \"x\"\n");
            File.WriteAllText(Path.Combine(_root, "host.json"), "{}");
            File.WriteAllText(Path.Combine(_root, "local.settings.json"), "{}");
            Environment.CurrentDirectory = _root;
            GlobalCoreToolsSettings.CurrentWorkerRuntime = WorkerRuntime.Rust;

            // The listing is rooted at the current folder, which the OS can spell differently from the folder that was set
            // (for example /private/var for /var on macOS), so the expected paths start from what the OS reports.
            var root = Environment.CurrentDirectory;

            var output = Capture(() => PublishFunctionAppAction.InternalListIncludedFiles(null));

            var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(
                new[]
                {
                    Path.Combine(root, "host.json"),
                    $"{Path.Combine(root, "bin", "app")} (will be built during publish)",
                },
                lines);
        }

        [Fact]
        public void ListIncludedFiles_Rust_WithABuiltBinary_ListsItWithoutTheNote()
        {
            File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname = \"x\"\n");
            Directory.CreateDirectory(Path.Combine(_root, "bin"));
            File.WriteAllBytes(Path.Combine(_root, "bin", "app"), new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
            Environment.CurrentDirectory = _root;
            GlobalCoreToolsSettings.CurrentWorkerRuntime = WorkerRuntime.Rust;
            var root = Environment.CurrentDirectory;

            var output = Capture(() => PublishFunctionAppAction.InternalListIncludedFiles(null));

            // host.json is optional and this app has none.
            Assert.Equal(new[] { Path.Combine(root, "bin", "app") }, output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        [Fact]
        public void ListIgnoredFiles_Rust_SaysThatOnlyHostJsonAndAppArePublished()
        {
            File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname = \"x\"\n");
            Environment.CurrentDirectory = _root;
            GlobalCoreToolsSettings.CurrentWorkerRuntime = WorkerRuntime.Rust;

            var output = Capture(() => PublishFunctionAppAction.InternalListIgnoredFiles(null));

            Assert.Equal("Rust publishes only host.json and app; all other files are excluded.", output.Trim());
        }

        [Fact]
        public void ListIgnoredFiles_Go_KeepsItsText()
        {
            File.WriteAllText(Path.Combine(_root, "go.mod"), "module example.com/x\n");
            Environment.CurrentDirectory = _root;
            GlobalCoreToolsSettings.CurrentWorkerRuntime = WorkerRuntime.Go;

            var output = Capture(() => PublishFunctionAppAction.InternalListIgnoredFiles(null));

            Assert.Equal("Go publishes only host.json and app; all other files are excluded.", output.Trim());
        }

        private static string Capture(Action action)
        {
            var output = new StringBuilder();
            var console = Substitute.For<IConsoleWriter>();
            console.WriteLine(Arg.Do<object>(o => output.AppendLine(o?.ToString()))).Returns(console);
            console.Write(Arg.Do<object>(o => output.Append(o?.ToString()))).Returns(console);

            var oldOut = ColoredConsole.Out;
            var oldErr = ColoredConsole.Error;
            try
            {
                ColoredConsole.Out = console;
                ColoredConsole.Error = console;
                action();
            }
            finally
            {
                ColoredConsole.Out = oldOut;
                ColoredConsole.Error = oldErr;
            }

            return output.ToString();
        }
    }
}
