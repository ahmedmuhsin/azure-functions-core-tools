// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;
using AwesomeAssertions;
using Azure.Functions.Cli.Actions.AzureActions;
using Azure.Functions.Cli.Arm.Models;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // Rust is in preview and nothing has run on Azure, so no text a Rust user reads may promise support
    // or say where a Rust app runs. A text may say that Rust is a preview and what Core Tools does on this machine.
    public class RustUserTextTests
    {
        // The words of a claim that Rust is supported somewhere, or that it runs somewhere. The first line is the
        // original list. The second line holds the phrases of the platform claim that the notice and the release note once made.
        private static readonly Regex _promise = new Regex(
            @"Flex|only supported|only supports|\bsupports\b|supported on|not yet|generally available|\bGA\b|Use .* plan" +
            @"|\bthe platform\b|\bplatform's\b|native stack|\bproxy\b|\bruns on\b|like a Go app",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Claims that the blacklist has to see, and texts that say only what Core Tools does locally. These are fixed
        // samples, so the check does not depend on the strings of the product. The first two were the notice and the release
        // note of an earlier tag.
        [Theory]
        [InlineData("Rust support is in preview. A Rust app runs on the platform's native stack through its proxy, and build and publish behavior may change.")]
        [InlineData("- Add a preview of Rust apps to `func init`. A Rust app runs on the platform's native stack through its proxy, like a Go app.")]
        [InlineData("A Rust app runs like a Go app.")]
        [InlineData("Rust apps are supported on Linux plans.")]
        [InlineData("Rust is generally available on Flex Consumption.")]
        [InlineData("Use a Premium plan for Rust.")]
        [InlineData("Rust apps go through a proxy.")]
        [InlineData("A Rust app runs on Linux.")]
        [InlineData("A Rust app uses the native stack.")]
        [InlineData("The platform decides how it starts.")]
        [InlineData("A platform's worker starts the app.")]
        public void Blacklist_SeesAClaimThatARustAppIsSupportedOrRunsSomewhere(string claim)
        {
            _promise.IsMatch(claim).Should().BeTrue();
        }

        [Theory]
        [InlineData("Rust support is in preview. Build and publish behavior may change.")]
        [InlineData("Core Tools packs Rust apps for x86_64 Linux only.")]
        [InlineData("Other platforms keep `bin/app`.")]
        public void Blacklist_AllowsWhatCoreToolsDoesOnThisMachine(string text)
        {
            _promise.IsMatch(text).Should().BeFalse();
        }

        [Fact]
        public void ReleaseNote_RustLines_DoNotPromiseSupportOrSayWhereAnAppRuns()
        {
            var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "release_notes.md"));
            lines.Should().NotBeEmpty();

            var offenders = lines
                .Where(line => line.Contains("Rust", StringComparison.OrdinalIgnoreCase) && _promise.IsMatch(line))
                .ToList();

            offenders.Should().BeEmpty(
                "a release note line about Rust must not promise support or say where a Rust app runs. Offenders:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, offenders));
        }

        // The scaffold is written against crates that are not published, so a user has to know that it will change.
        [Fact]
        public async Task ScaffoldFiles_CarryAProvisionalComment()
        {
            var cargoToml = await StaticResources.CargoToml;
            var mainRs = await StaticResources.MainRs;

            HasProvisionalComment(cargoToml, "#").Should().BeTrue("Cargo.toml marks its crate lines as provisional");
            HasProvisionalComment(mainRs, "//").Should().BeTrue("main.rs is written against the unpublished SDK and worker API, and says so");
        }

        [Fact]
        public void RustMessages_DoNotPromiseSupportOrNameFlex()
        {
            using var dir = new TempDirectory();
            var messages = new Dictionary<string, string>
            {
                ["preview notice"] = Utilities.RustPreviewWarning,
                ["docker"] = NativeWorkerHelpers.GetDockerNotAvailableMessage(WorkerRuntime.Rust),
                ["toolchain hint"] = RustHelpers.LinuxToolchainHint,
                ["cargo missing"] = MessageOf(() => RustHelpers.AssertCargoVersion(null)),
                ["cargo too old"] = MessageOf(() => RustHelpers.AssertCargoVersion(new WorkerLanguageVersionInfo(WorkerRuntime.Rust, "1.85.0", "cargo"))),
                ["cargo unparseable"] = MessageOf(() => RustHelpers.AssertCargoVersion(new WorkerLanguageVersionInfo(WorkerRuntime.Rust, "beta1", "cargo"))),
                ["start binary missing"] = MessageOf(() => RustHelpers.AssertBinaryExists(dir.Path)),
                ["no binary target"] = MessageOf(() => RustHelpers.SelectBinary(Array.Empty<RustHelpers.CargoBinary>(), null, "Cargo.toml")),
                ["two binaries"] = MessageOf(() => RustHelpers.SelectBinary(
                    new[] { new RustHelpers.CargoBinary("a", "/a"), new RustHelpers.CargoBinary("b", "/b") }, null, "Cargo.toml")),
                ["pack binary missing"] = MessageOf(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path)),
                ["pack too small"] = MessageOf(() => AssertBinary(dir, new byte[10])),
                ["pack not elf"] = MessageOf(() => AssertBinary(dir, new byte[32])),
                ["pack elf32"] = MessageOf(() => AssertBinary(dir, ElfHeader(machine: 0x3E, elfClass: 1))),
                ["pack wrong arch"] = MessageOf(() => AssertBinary(dir, ElfHeader(machine: 0xB7, elfClass: 2))),
                ["pack debug binary warning"] = RustHelpers.DebugInformationWarning,
                ["publish windows app"] = PublishMessage(new Site("s") { Kind = "functionapp", Sku = "premium" }, BuildOption.Default, false),
                ["publish linux consumption"] = PublishMessage(new Site("s") { Kind = "functionapp,linux", Sku = "dynamic" }, BuildOption.Default, false),
                ["publish build remote"] = PublishMessage(new Site("s") { Kind = "functionapp,linux", Sku = "premium" }, BuildOption.Remote, false),
                ["publish build container"] = PublishMessage(new Site("s") { Kind = "functionapp,linux", Sku = "premium" }, BuildOption.Container, false),
                ["publish build native deps"] = PublishMessage(new Site("s") { Kind = "functionapp,linux", Sku = "premium" }, BuildOption.Default, true),
            };

            var offenders = messages
                .Where(message => _promise.IsMatch(message.Value))
                .Select(message => $"{message.Key}: {message.Value}")
                .ToList();

            offenders.Should().BeEmpty(
                "a Rust user text must not promise support or name Flex. Offenders:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void ToolchainHint_SaysTheBinaryLinksGlibc_WithoutPromisingAVersion()
        {
            var hint = RustHelpers.LinuxToolchainHint;

            hint.Should().Contain("glibc");

            // One short sentence.
            var sentences = hint.Split(". ", StringSplitOptions.RemoveEmptyEntries).Where(sentence => sentence.Contains("glibc")).ToList();
            sentences.Should().ContainSingle("the hint carries one sentence about glibc");
            sentences[0].Length.Should().BeLessThan(140);

            // Advice only. The glibc of the platform can change, so the text names no glibc version and no symbol version.
            hint.Should().NotMatchRegex(@"glibc\s*\d|GLIBC_|\b\d+\.\d{2}\b");
        }

        // The messages of the code that copies the binary and picks it, which a test can make without a build.
        [Fact]
        public void RustMessages_OfTheCopyAndTheBinaryChoice_DoNotPromiseSupportOrNameFlex()
        {
            using var dir = new TempDirectory();
            var built = Path.Combine(dir.Path, "built");
            File.WriteAllText(built, "binary");
            var folderAtDestination = Directory.CreateDirectory(Path.Combine(dir.Path, "app-is-a-folder")).FullName;
            var fileAsBin = Path.Combine(dir.Path, "bin-is-a-file");
            File.WriteAllText(fileAsBin, "x");
            var others = new[] { new RustHelpers.CargoBinary("member", "/member") };

            var messages = new Dictionary<string, string>
            {
                ["copy to a folder"] = MessageOf(() => RustHelpers.CopyBinary(built, folderAtDestination)),
                ["copy into a file"] = MessageOf(() => RustHelpers.CopyBinary(built, Path.Combine(fileAsBin, "app"))),
                ["copy fails"] = MessageOf(() => RustHelpers.CopyBinary(built, Path.Combine(dir.Path, "bin", "app"), FailsWithNoSpace)),
                ["workspace root"] = MessageOf(() => RustHelpers.SelectBinary(Array.Empty<RustHelpers.CargoBinary>(), null, "Cargo.toml", others)),
            };

            PromisesIn(messages).Should().BeEmpty("a Rust user text must not promise support or name Flex");
        }

        // Every string in the files that only Rust uses, and in the native code that Go and Rust share, is read from the source.
        // A text that is added later is checked without anyone listing it. That covers the progress and build texts, the errors of
        // the probe and the copy, the 'func pack --output' text and the shared texts about the binary.
        [Fact]
        public void SourceTexts_OfTheFilesOnlyRustUses_DoNotPromiseSupportOrNameFlex()
        {
            var files = new[]
            {
                Path.Combine(SourceFolder(), "Helpers", "RustHelpers.cs"),
                Path.Combine(SourceFolder(), "Actions", "LocalActions", "PackAction", "RustPackSubcommandAction.cs"),
                Path.Combine(SourceFolder(), "Helpers", "NativeWorkerHelpers.cs"),
            };

            var scanned = 0;
            var offenders = new List<string>();
            foreach (var file in files)
            {
                var literals = CSharpStringLiterals.Read(File.ReadAllText(file));
                scanned += literals.Count;
                offenders.AddRange(PromisesIn(literals.Where(literal => !IsGoOnly(literal.Text)), RelativePath(file)));
            }

            scanned.Should().BeGreaterThan(60, "the scan has to see the texts of the files, or it proves nothing");
            offenders.Should().BeEmpty("a Rust user text must not promise support or name Flex. Offenders:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
        }

        // The other shared files hold texts for every language. A text that mentions Rust is a Rust text, and is checked. That
        // covers the 'func new' refusal, the preview notice, the init texts and the texts of the runtime detection.
        [Fact]
        public void SourceTexts_OfSharedFilesThatMentionRust_DoNotPromiseSupportOrNameFlex()
        {
            var alreadyScanned = new[] { "RustHelpers.cs", "RustPackSubcommandAction.cs", "NativeWorkerHelpers.cs" };
            var mentionsRust = new Regex(@"\bRust\b", RegexOptions.IgnoreCase);
            var checkedTexts = new List<string>();
            var offenders = new List<string>();

            foreach (var file in SourceFiles().Where(file => !alreadyScanned.Contains(Path.GetFileName(file))))
            {
                var literals = CSharpStringLiterals.Read(File.ReadAllText(file)).Where(literal => mentionsRust.IsMatch(literal.Text)).ToList();
                checkedTexts.AddRange(literals.Select(literal => literal.Text));
                offenders.AddRange(PromisesIn(literals, RelativePath(file)));
            }

            checkedTexts.Should().Contain(text => text.Contains("'func new' command is not available for the Rust runtime"), "the refusal of 'func new' is one of the texts");
            checkedTexts.Should().Contain(text => text == Utilities.RustPreviewWarning, "the notice is one of the texts");
            offenders.Should().BeEmpty("a Rust user text must not promise support or name Flex. Offenders:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
        }

        // What 'func publish --list-ignored-files' and '--list-included-files' print for a native app, which Rust shares with Go.
        [Fact]
        public void SourceTexts_OfTheNativeFileListings_DoNotPromiseSupportOrNameFlex()
        {
            var file = Path.Combine(SourceFolder(), "Actions", "AzureActions", "PublishFunctionAppAction.cs");
            var literals = new List<CSharpStringLiterals.StringLiteral>();
            foreach (var method in new[] { "InternalListIgnoredFiles", "InternalListIncludedFiles" })
            {
                literals.AddRange(LiteralsOfMethod(file, method));
            }

            literals.Should().Contain(literal => literal.Text.Contains("publishes only"), "the listing of ignored files is one of the texts");
            literals.Should().Contain(literal => literal.Text.Contains("will be built during publish"), "the listing of included files is one of the texts");
            PromisesIn(literals, RelativePath(file)).Should().BeEmpty("a Rust user text must not promise support or name Flex");
        }

        [Fact]
        public async Task ScaffoldFiles_DoNotPromiseSupportOrNameFlex()
        {
            var files = new Dictionary<string, string>
            {
                ["Cargo.toml.template"] = await StaticResources.CargoToml,
                ["main.rs"] = await StaticResources.MainRs,
            };

            var offenders = files
                .SelectMany(file => file.Value.Split('\n').Select((line, index) => (File: file.Key, Line: index + 1, Text: line)))
                .Where(line => _promise.IsMatch(line.Text))
                .Select(line => $"{line.File}:{line.Line}: {line.Text.Trim()}")
                .ToList();

            files.Values.Should().OnlyContain(content => content.Length > 100, "the scaffold files have to be read");
            offenders.Should().BeEmpty("the text of a scaffold must not promise support or say where an app runs. Offenders:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
        }

        // The docs row of 'func start' and the release note are lines of the documents that the branch changed.
        [Fact]
        public void Docs_LinesThatMentionRust_DoNotPromiseSupportOrNameFlex()
        {
            var root = RepositoryRoot();
            var mentionsRust = new Regex(@"\bRust\b", RegexOptions.IgnoreCase);
            var documents = Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
                .ToList();

            var rustLines = documents
                .SelectMany(document => File.ReadAllLines(document).Select((line, index) => (Document: RelativePath(document), Line: index + 1, Text: line)))
                .Where(line => mentionsRust.IsMatch(line.Text))
                .ToList();

            rustLines.Should().Contain(line => line.Document.EndsWith("host-configuration.md"), "the docs row of func start mentions Rust");
            var offenders = rustLines.Where(line => _promise.IsMatch(line.Text)).Select(line => $"{line.Document}:{line.Line}: {line.Text.Trim()}").ToList();
            offenders.Should().BeEmpty("a line of the docs about Rust must not promise support or say where an app runs. Offenders:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, offenders));
        }

        // The scan has to find a promise in a real source file, in a string that holds code in braces, and has to leave comments alone.
        [Fact]
        public void LiteralScan_SeesAPromiseInAString_AndNotInAComment()
        {
            var source = "class A\n{\n    // \"Rust runs on the platform's native stack\"\n    string a = \"fine text\";\n    string b = $\"Rust apps run on the platform's native stack{x}\";\n}\n";

            var offenders = PromisesIn(CSharpStringLiterals.Read(source), "A.cs");

            offenders.Should().ContainSingle().Which.Should().StartWith("A.cs:5:").And.Contain("native stack");
        }

        [Fact]
        public void GoDockerMessage_IsUnchanged()
        {
            NativeWorkerHelpers.GetDockerNotAvailableMessage(WorkerRuntime.Go)
                .Should().Be("Docker support for the Go worker runtime is not yet available.");
        }

        // The text that only Go reads. It is in the shared file, and the Rust text for the same case is above it.
        private static bool IsGoOnly(string text)
        {
            return text.Contains("worker runtime is not yet available", StringComparison.Ordinal);
        }

        private static string SourceFolder()
        {
            return Path.Combine(RepositoryRoot(), "src", "Cli", "func");
        }

        private static IEnumerable<string> SourceFiles()
        {
            var separator = Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(SourceFolder(), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{separator}obj{separator}") && !file.Contains($"{separator}bin{separator}"));
        }

        private static string RelativePath(string file)
        {
            return Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');
        }

        // The literals of one method of a file, found by its name. The methods here start at the indent of a class member.
        private static IReadOnlyList<CSharpStringLiterals.StringLiteral> LiteralsOfMethod(string file, string methodName)
        {
            var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
            var start = Array.FindIndex(lines, line => line.StartsWith("        ", StringComparison.Ordinal) && !line.StartsWith("         ", StringComparison.Ordinal) && line.Contains($" {methodName}(", StringComparison.Ordinal));
            start.Should().BeGreaterThanOrEqualTo(0, $"{methodName} is a method of {Path.GetFileName(file)}");
            var end = Array.FindIndex(lines, start, line => line == "        }");
            end.Should().BeGreaterThan(start);

            return CSharpStringLiterals.Read(string.Join("\n", lines.Skip(start).Take(end - start + 1)), firstLine: start + 1);
        }

        private static List<string> PromisesIn(IEnumerable<CSharpStringLiterals.StringLiteral> literals, string where)
        {
            return literals
                .Where(literal => _promise.IsMatch(literal.Text))
                .Select(literal => $"{where}:{literal.Line}: {literal.Text.Trim()}")
                .ToList();
        }

        private static List<string> PromisesIn(Dictionary<string, string> messages)
        {
            return messages
                .Where(message => _promise.IsMatch(message.Value))
                .Select(message => $"{message.Key}: {message.Value}")
                .ToList();
        }

        private static void FailsWithNoSpace(string source, string target)
        {
            throw new IOException("There is not enough space on the disk.");
        }

        // Finds the repository root the way ZipHelperTests does: by walking up from the test assembly to the solution file.
        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Azure.Functions.Cli.sln")))
            {
                dir = dir.Parent;
            }

            Assert.True(dir is not null, "Could not find repo root (Azure.Functions.Cli.sln).");
            return dir!.FullName;
        }

        private static bool HasProvisionalComment(string content, string commentToken)
        {
            return content
                .Split('\n')
                .Any(line => line.TrimStart().StartsWith(commentToken, StringComparison.Ordinal) && line.Contains("Provisional", StringComparison.Ordinal));
        }

        private static string PublishMessage(Site site, BuildOption buildOption, bool buildNativeDeps)
        {
            return MessageOf(() => PublishFunctionAppAction.ValidateRustPublishOptions(site, buildOption, buildNativeDeps));
        }

        private static string MessageOf(Action action)
        {
            return Assert.Throws<CliException>(action).Message;
        }

        private static void AssertBinary(TempDirectory dir, byte[] content)
        {
            File.WriteAllBytes(dir.BinaryPath, content);
            RustHelpers.AssertLinuxAmd64Binary(dir.Path);
        }

        // A 20 byte ELF identification region with the given class (2 is 64-bit) and e_machine, little endian.
        private static byte[] ElfHeader(ushort machine, byte elfClass)
        {
            var header = new byte[20];
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = elfClass;
            header[5] = 1;
            header[18] = (byte)(machine & 0xFF);
            header[19] = (byte)(machine >> 8);
            return header;
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-text-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(System.IO.Path.Combine(Path, "bin"));
            }

            public string Path { get; }

            public string BinaryPath => System.IO.Path.Combine(Path, "bin", "app");

            public void Dispose()
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
