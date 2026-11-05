// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // The cargo output in these tests is real output of cargo 1.90.0 (840b83a10 2025-07-30) on Windows 10 x64 (host
    // x86_64-pc-windows-msvc), captured as raw bytes in small projects and sanitized with one substitution: the user name and the
    // folder of the capture became "Dev" and "scratch", in the plain, the JSON escaped and the URL spelling. The project in capitals
    // has the same substitution in capitals, and the 8.3 short path project got other valid 8.3 names. The letters of the non ASCII
    // folder are the token <NONASCII> in the text, and Real puts them back. Nothing else was changed.
    //
    // What the captures show, and what the comparison in BuildAndCopyAsync relies on: cargo prints the folder in the spelling that
    // was typed, both in 'cargo locate-project' and in the manifest_path of each artifact message, so the two agree byte for byte.
    // That holds for a path in capitals, a junction, an 8.3 short path, and a folder name with spaces and non ASCII letters. There
    // is no \\?\ prefix. The two paths differ only when the folder is the root of a virtual workspace.
    public class RustHelperWindowsCargoOutputTests
    {
        private const string NonAsciiFolder = "pr\u00F8j \u00E9\u00FC \u65E5\u672C";

        [Theory]
        [InlineData("one-bin")]
        [InlineData("typed-uppercase")]
        [InlineData("junction")]
        [InlineData("short-8dot3")]
        [InlineData("non-ascii-and-spaces")]
        [InlineData("target-dir-config")]
        [InlineData("workspace-member")]
        public void ReadBinaryArtifacts_RealWindowsOutput_FindsTheBinaryOfTheFolder(string scenario)
        {
            var capture = WindowsCargo.Captures[scenario];

            var binaries = RustHelpers.ReadBinaryArtifacts(capture.Build, capture.Manifest);

            binaries.Should().ContainSingle().Which.Should().Be(new RustHelpers.CargoBinary(capture.Name, capture.Executable));
            RustHelpers.SelectBinary(binaries, defaultRun: null, capture.Manifest).Should().Be(capture.Executable);
        }

        // The artifact lists the executable and a .pdb file, and the path of the executable is the one that gets copied.
        [Fact]
        public void ReadBinaryArtifacts_RealWindowsOutput_TakesTheExecutableAndNotTheDebugFile()
        {
            var capture = WindowsCargo.Captures["one-bin"];
            capture.Build[0].Should().Contain("one_bin.pdb", "the artifact of a Windows build names the debug file as well");

            var binaries = RustHelpers.ReadBinaryArtifacts(capture.Build, capture.Manifest);

            binaries.Should().ContainSingle().Which.Path.Should().EndWith("one-bin.exe");
        }

        // The executable of a member of a workspace is in the target folder of the workspace, not in the folder of the package.
        [Fact]
        public void ReadBinaryArtifacts_RealWindowsOutputOfAWorkspaceMember_FindsTheBinaryOutsideTheProjectFolder()
        {
            var capture = WindowsCargo.Captures["workspace-member"];

            var binaries = RustHelpers.ReadBinaryArtifacts(capture.Build, capture.Manifest);

            binaries.Should().ContainSingle().Which.Path.Should()
                .StartWith(@"C:\Users\Dev\scratch\work\ws\target\debug\")
                .And.NotStartWith(@"C:\Users\Dev\scratch\work\ws\app\");
        }

        // With target-dir = "out/tgt" in .cargo/config.toml, cargo joins the folders with a forward slash inside a backslash path.
        // The path has to reach CopyBinary as cargo printed it.
        [Fact]
        public void ReadBinaryArtifacts_RealWindowsOutputWithATargetDirThatHasAForwardSlash_KeepsThePathOfCargo()
        {
            var capture = WindowsCargo.Captures["target-dir-config"];

            var binaries = RustHelpers.ReadBinaryArtifacts(capture.Build, capture.Manifest);

            binaries.Should().ContainSingle().Which.Path.Should().Be(@"C:\Users\Dev\scratch\work\cfg-target\out/tgt\debug\cfg-target.exe");
        }

        [SkippableFact]
        public void CopyBinary_AnExecutablePathWithMixedSeparators_CopiesTheFile()
        {
            Skip.IfNot(OperatingSystem.IsWindows(), "Windows takes both separators in one path. On Linux a backslash is a letter of a name.");
            using var dir = new TempDirectory();
            var built = Path.Combine(dir.Path, "out", "tgt", "debug", "cfg-target.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(built));
            File.WriteAllText(built, "exe");
            var mixed = Path.Combine(dir.Path, "out/tgt") + @"\debug\cfg-target.exe";
            var destination = Path.Combine(dir.Path, "bin", "app.exe");

            RustHelpers.CopyBinary(mixed, destination);

            File.ReadAllText(destination).Should().Be("exe");
        }

        // The root of a virtual workspace has no package. Cargo builds the members, and their manifest is not the one that
        // 'cargo locate-project' printed for the folder.
        [Fact]
        public void SelectBinary_RealWindowsOutputOfAVirtualWorkspaceRoot_NamesTheMemberAndSaysWhereToRun()
        {
            var capture = WindowsCargo.Captures["workspace-virtual-root"];
            capture.Manifest.Should().Be(@"C:\Users\Dev\scratch\work\ws\Cargo.toml");
            capture.Build[0].Should().Contain(@"C:\\Users\\Dev\\scratch\\work\\ws\\app\\Cargo.toml");

            var own = RustHelpers.ReadBinaryArtifacts(capture.Build, capture.Manifest);
            var others = RustHelpers.ReadOtherBinaryArtifacts(capture.Build, capture.Manifest);

            own.Should().BeEmpty();
            others.Should().ContainSingle().Which.Should().Be(new RustHelpers.CargoBinary("member-app", @"C:\Users\Dev\scratch\work\ws\target\debug\member-app.exe"));
            var action = () => RustHelpers.SelectBinary(own, defaultRun: null, capture.Manifest, others);
            var message = action.Should().Throw<CliException>().Which.Message;
            message.Should().Contain(capture.Manifest).And.Contain("member-app").And.Contain("other packages");
            message.Should().NotContain("did not build a binary", "cargo did build a binary, for the member");
        }

        [Fact]
        public void SelectBinary_RealWindowsOutputOfTwoBinaries_PicksTheDefaultRunOfTheMetadata()
        {
            var capture = WindowsCargo.Captures["multi-default-run"];

            var binaries = RustHelpers.ReadBinaryArtifacts(capture.Build, capture.Manifest);
            var defaultRun = RustHelpers.ReadDefaultRun(WindowsCargo.MultiMetadata, capture.Manifest);

            binaries.Select(binary => binary.Name).Should().BeEquivalentTo("multi", "second");
            defaultRun.Should().Be("second");
            RustHelpers.SelectBinary(binaries, defaultRun, capture.Manifest).Should().Be(@"C:\Users\Dev\scratch\work\multi\target\debug\second.exe");
        }

        // The messages of three failed builds on a Windows host: for the Linux target without the target, for the Linux target
        // without a Linux linker, and a plain compile error that has nothing to do with the target.
        [Theory]
        [InlineData("target-not-installed", true)]
        [InlineData("linker-not-found", true)]
        [InlineData("compile-error", false)]
        public void LooksLikeMissingLinuxToolchain_RealWindowsCargoStderr_RecognizesTheMissingToolchainOnly(string scenario, bool expected)
        {
            var stderr = string.Join(Environment.NewLine, WindowsCargo.Stderr[scenario]) + Environment.NewLine;

            RustHelpers.LooksLikeMissingLinuxToolchain(stderr).Should().Be(expected);
        }

        private static string Real(string text) => text.Replace("<NONASCII>", NonAsciiFolder);

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-win-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup.
                }
            }
        }

        private sealed class Capture
        {
            public string Name { get; init; }

            // The text that 'cargo locate-project --message-format plain' printed: the manifest path and a line feed.
            public string Locate { get; init; }

            // The lines that 'cargo build --message-format=json-render-diagnostics' printed on stdout.
            public string[] Build { get; init; }

            // The executable that the artifact of the package names. Not set where the folder builds no binary of its own.
            public string Executable { get; init; }

            // BuildAndCopyAsync joins the lines that cargo prints for 'locate-project' and trims the text.
            public string Manifest => Locate.Trim();
        }

        private static class WindowsCargo
        {
            public static readonly Dictionary<string, Capture> Captures = new()
            {
                ["one-bin"] = new Capture
                {
                    Name = "one-bin",
                    Locate = """C:\Users\Dev\scratch\work\one-bin\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/one-bin#0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\one-bin\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"one-bin","src_path":"C:\\Users\\Dev\\scratch\\work\\one-bin\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\one-bin\\target\\debug\\one-bin.exe","C:\\Users\\Dev\\scratch\\work\\one-bin\\target\\debug\\one_bin.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\one-bin\\target\\debug\\one-bin.exe","fresh":false}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                    Executable = @"C:\Users\Dev\scratch\work\one-bin\target\debug\one-bin.exe",
                },

                // The whole path was typed in capitals. Cargo printed it in capitals in both places.
                ["typed-uppercase"] = new Capture
                {
                    Name = "one-bin",
                    Locate = """C:\USERS\DEV\SCRATCH\WORK\ONE-BIN\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/USERS/DEV/SCRATCH/WORK/ONE-BIN#one-bin@0.1.0","manifest_path":"C:\\USERS\\DEV\\SCRATCH\\WORK\\ONE-BIN\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"one-bin","src_path":"C:\\USERS\\DEV\\SCRATCH\\WORK\\ONE-BIN\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\USERS\\DEV\\SCRATCH\\WORK\\ONE-BIN\\target\\debug\\one-bin.exe","C:\\USERS\\DEV\\SCRATCH\\WORK\\ONE-BIN\\target\\debug\\one_bin.pdb"],"executable":"C:\\USERS\\DEV\\SCRATCH\\WORK\\ONE-BIN\\target\\debug\\one-bin.exe","fresh":true}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                    Executable = @"C:\USERS\DEV\SCRATCH\WORK\ONE-BIN\target\debug\one-bin.exe",
                },

                // A junction to the folder of the project. Cargo kept the name of the junction and did not resolve it.
                ["junction"] = new Capture
                {
                    Name = "one-bin",
                    Locate = """C:\Users\Dev\scratch\work\junction-to-one-bin\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/junction-to-one-bin#one-bin@0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\junction-to-one-bin\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"one-bin","src_path":"C:\\Users\\Dev\\scratch\\work\\junction-to-one-bin\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\junction-to-one-bin\\target\\debug\\one-bin.exe","C:\\Users\\Dev\\scratch\\work\\junction-to-one-bin\\target\\debug\\one_bin.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\junction-to-one-bin\\target\\debug\\one-bin.exe","fresh":true}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                    Executable = @"C:\Users\Dev\scratch\work\junction-to-one-bin\target\debug\one-bin.exe",
                },

                // The working directory was given as an 8.3 short path. Cargo kept the short spelling in both places.
                ["short-8dot3"] = new Capture
                {
                    Name = "one-bin",
                    Locate = """C:\Users\DEVELO~1\AppData\Local\SCRATC~1\work\one-bin\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/DEVELO~1/AppData/Local/SCRATC~1/work/one-bin#0.1.0","manifest_path":"C:\\Users\\DEVELO~1\\AppData\\Local\\SCRATC~1\\work\\one-bin\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"one-bin","src_path":"C:\\Users\\DEVELO~1\\AppData\\Local\\SCRATC~1\\work\\one-bin\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\DEVELO~1\\AppData\\Local\\SCRATC~1\\work\\one-bin\\target\\debug\\one-bin.exe","C:\\Users\\DEVELO~1\\AppData\\Local\\SCRATC~1\\work\\one-bin\\target\\debug\\one_bin.pdb"],"executable":"C:\\Users\\DEVELO~1\\AppData\\Local\\SCRATC~1\\work\\one-bin\\target\\debug\\one-bin.exe","fresh":true}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                    Executable = @"C:\Users\DEVELO~1\AppData\Local\SCRATC~1\work\one-bin\target\debug\one-bin.exe",
                },

                // Cargo prints the letters as they are in the JSON strings, and percent encodes them in the package_id.
                ["non-ascii-and-spaces"] = new Capture
                {
                    Name = "one-bin",
                    Locate = Real("""C:\Users\Dev\scratch\work\<NONASCII>\Cargo.toml""") + "\n",
                    Build =
                    [
                        Real("""{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/pr%C3%B8j%20%C3%A9%C3%BC%20%E6%97%A5%E6%9C%AC#one-bin@0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\<NONASCII>\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"one-bin","src_path":"C:\\Users\\Dev\\scratch\\work\\<NONASCII>\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\<NONASCII>\\target\\debug\\one-bin.exe","C:\\Users\\Dev\\scratch\\work\\<NONASCII>\\target\\debug\\one_bin.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\<NONASCII>\\target\\debug\\one-bin.exe","fresh":false}"""),
                        """{"reason":"build-finished","success":true}""",
                    ],
                    Executable = Real(@"C:\Users\Dev\scratch\work\<NONASCII>\target\debug\one-bin.exe"),
                },

                // .cargo/config.toml has target-dir = "out/tgt". The executable path mixes the separators.
                ["target-dir-config"] = new Capture
                {
                    Name = "cfg-target",
                    Locate = """C:\Users\Dev\scratch\work\cfg-target\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/cfg-target#0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\cfg-target\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"cfg-target","src_path":"C:\\Users\\Dev\\scratch\\work\\cfg-target\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\cfg-target\\out/tgt\\debug\\cfg-target.exe","C:\\Users\\Dev\\scratch\\work\\cfg-target\\out/tgt\\debug\\cfg_target.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\cfg-target\\out/tgt\\debug\\cfg-target.exe","fresh":false}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                    Executable = @"C:\Users\Dev\scratch\work\cfg-target\out/tgt\debug\cfg-target.exe",
                },

                // The working directory is the package app of a workspace. Its executable is in the target folder of the workspace.
                ["workspace-member"] = new Capture
                {
                    Name = "member-app",
                    Locate = """C:\Users\Dev\scratch\work\ws\app\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/ws/app#member-app@0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\ws\\app\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"member-app","src_path":"C:\\Users\\Dev\\scratch\\work\\ws\\app\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\ws\\target\\debug\\member-app.exe","C:\\Users\\Dev\\scratch\\work\\ws\\target\\debug\\member_app.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\ws\\target\\debug\\member-app.exe","fresh":false}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                    Executable = @"C:\Users\Dev\scratch\work\ws\target\debug\member-app.exe",
                },

                // The working directory is the root of the same workspace, a virtual one. 'cargo locate-project' prints the manifest of the root.
                ["workspace-virtual-root"] = new Capture
                {
                    Name = "member-app",
                    Locate = """C:\Users\Dev\scratch\work\ws\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/ws/app#member-app@0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\ws\\app\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"member-app","src_path":"C:\\Users\\Dev\\scratch\\work\\ws\\app\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\ws\\target\\debug\\member-app.exe","C:\\Users\\Dev\\scratch\\work\\ws\\target\\debug\\member_app.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\ws\\target\\debug\\member-app.exe","fresh":true}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                },

                // Two binaries, and default-run = "second" in the manifest.
                ["multi-default-run"] = new Capture
                {
                    Name = "multi",
                    Locate = """C:\Users\Dev\scratch\work\multi\Cargo.toml""" + "\n",
                    Build =
                    [
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/multi#0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\multi\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"multi","src_path":"C:\\Users\\Dev\\scratch\\work\\multi\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\multi\\target\\debug\\multi.exe","C:\\Users\\Dev\\scratch\\work\\multi\\target\\debug\\multi.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\multi\\target\\debug\\multi.exe","fresh":false}""",
                        """{"reason":"compiler-artifact","package_id":"path+file:///C:/Users/Dev/scratch/work/multi#0.1.0","manifest_path":"C:\\Users\\Dev\\scratch\\work\\multi\\Cargo.toml","target":{"kind":["bin"],"crate_types":["bin"],"name":"second","src_path":"C:\\Users\\Dev\\scratch\\work\\multi\\src\\bin\\second.rs","edition":"2024","doc":true,"doctest":false,"test":true},"profile":{"opt_level":"0","debuginfo":2,"debug_assertions":true,"overflow_checks":true,"test":false},"features":[],"filenames":["C:\\Users\\Dev\\scratch\\work\\multi\\target\\debug\\second.exe","C:\\Users\\Dev\\scratch\\work\\multi\\target\\debug\\second.pdb"],"executable":"C:\\Users\\Dev\\scratch\\work\\multi\\target\\debug\\second.exe","fresh":false}""",
                        """{"reason":"build-finished","success":true}""",
                    ],
                },
            };

            // The output of 'cargo metadata --no-deps --format-version 1' for the project with two binaries.
            public static readonly string MultiMetadata = """{"packages":[{"name":"multi","version":"0.1.0","id":"path+file:///C:/Users/Dev/scratch/work/multi#0.1.0","license":null,"license_file":null,"description":null,"source":null,"dependencies":[],"targets":[{"kind":["bin"],"crate_types":["bin"],"name":"multi","src_path":"C:\\Users\\Dev\\scratch\\work\\multi\\src\\main.rs","edition":"2024","doc":true,"doctest":false,"test":true},{"kind":["bin"],"crate_types":["bin"],"name":"second","src_path":"C:\\Users\\Dev\\scratch\\work\\multi\\src\\bin\\second.rs","edition":"2024","doc":true,"doctest":false,"test":true}],"features":{},"manifest_path":"C:\\Users\\Dev\\scratch\\work\\multi\\Cargo.toml","metadata":null,"publish":null,"authors":[],"categories":[],"keywords":[],"readme":null,"repository":null,"homepage":null,"documentation":null,"edition":"2024","links":null,"default_run":"second","rust_version":null}],"workspace_members":["path+file:///C:/Users/Dev/scratch/work/multi#0.1.0"],"workspace_default_members":["path+file:///C:/Users/Dev/scratch/work/multi#0.1.0"],"resolve":null,"target_directory":"C:\\Users\\Dev\\scratch\\work\\multi\\target","version":1,"workspace_root":"C:\\Users\\Dev\\scratch\\work\\multi","metadata":null}""";

            // The standard error of failed builds of a probe project on a Windows host. The empty strings are blank lines of cargo.
            public static readonly Dictionary<string, string[]> Stderr = new()
            {
                ["target-not-installed"] =
                [
                    """   Compiling probe v0.1.0 (C:\Users\Dev\scratch\work\pack-win\proj)""",
                    """error[E0463]: can't find crate for `std`""",
                    """  |""",
                    """  = note: the `x86_64-unknown-linux-gnu` target may not be installed""",
                    """  = help: consider downloading the target with `rustup target add x86_64-unknown-linux-gnu`""",
                    string.Empty,
                    """For more information about this error, try `rustc --explain E0463`.""",
                    """error: could not compile `probe` (bin "probe") due to 1 previous error""",
                ],
                ["linker-not-found"] =
                [
                    """   Compiling probe v0.1.0 (C:\Users\Dev\scratch\work\pack-win\proj)""",
                    """error: linker `cc` not found""",
                    """  |""",
                    """  = note: program not found""",
                    string.Empty,
                    """error: could not compile `probe` (bin "probe") due to 1 previous error""",
                ],
                ["compile-error"] =
                [
                    """   Compiling probe v0.1.0 (C:\Users\Dev\scratch\work\pack-win\proj)""",
                    """error: expected expression, found `;`""",
                    """ --> src\main.rs:2:13""",
                    """  |""",
                    """2 |     let x = ;""",
                    """  |             ^ expected expression""",
                    string.Empty,
                    """error: could not compile `probe` (bin "probe") due to 1 previous error""",
                ],
            };
        }
    }
}
