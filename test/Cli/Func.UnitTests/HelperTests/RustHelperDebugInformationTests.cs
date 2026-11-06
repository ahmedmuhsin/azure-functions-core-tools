// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Azure.Functions.Cli.Helpers;
using Colors.Net;
using NSubstitute;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // A debug binary must never be packed by accident. 'func start' builds a debug binary into bin/app, the file that 'func pack --no-build'
    // and 'func publish --no-build' read, so a debug build has to be noticed there. A debug build has a .debug_info section and a normal
    // release build has none, so the two commands warn when the binary has that section. The check reads the names of
    // the sections of the ELF file and nothing else. The text '.debug_info' is also in the data of a release binary, because the
    // standard library holds it there, so a scan of the bytes would warn about every build.
    [Collection("NativeWorkerRuntimeTests")]
    public class RustHelperDebugInformationTests
    {
        private static readonly byte[] _debugInfoText = Encoding.ASCII.GetBytes("x.debug_info\0.debug_str\0");

        [Fact]
        public void HasDebugInformation_ASectionNamedDebugInfo_IsTrue()
        {
            using var dir = new TempDirectory();
            var path = dir.Write(ElfTestFile.Create(".text", ".debug_info", ".debug_line"));

            RustHelpers.HasDebugInformation(path).Should().BeTrue();
        }

        [Fact]
        public void HasDebugInformation_OnlyOtherSections_IsFalse()
        {
            using var dir = new TempDirectory();
            var path = dir.Write(ElfTestFile.Create(".text", ".rodata", ".data", ".symtab", ".strtab", ".eh_frame"));

            RustHelpers.HasDebugInformation(path).Should().BeFalse();
        }

        // The standard library keeps the names of DWARF sections in the data of every binary that can print a backtrace.
        [Fact]
        public void HasDebugInformation_TheTextOnlyInTheDataOfASection_IsFalse()
        {
            var image = ElfTestFile.Create((".text", new byte[100]), (".rodata", _debugInfoText));
            image.AsSpan().IndexOf(_debugInfoText.AsSpan(1, 11)).Should().BeGreaterThan(0, "the file holds the text, and the test means that it does");
            using var dir = new TempDirectory();
            var path = dir.Write(image);

            RustHelpers.HasDebugInformation(path).Should().BeFalse("only the name of a section counts");
        }

        [Fact]
        public void HasDebugInformation_ANameThatOnlyContainsOrResemblesDebugInfo_IsFalse()
        {
            var names = new[] { ".rela.debug_info", ".debug_info_x", ".debug_inf", ".zdebug_info", ".DEBUG_INFO", "debug_info", ".debug_info " };
            using var dir = new TempDirectory();

            var offenders = names
                .Where(name => RustHelpers.HasDebugInformation(dir.Write(ElfTestFile.Create(".text", name))))
                .ToList();

            offenders.Should().BeEmpty("the whole name of the section has to be .debug_info");
        }

        // Linkers save space by letting a name point into the end of a longer name, so the name is read from its offset.
        [Fact]
        public void HasDebugInformation_ANameThatSharesTheEndOfAnotherName_IsTrue()
        {
            var image = ElfTestFile.Create(".text", ".rela.debug_info", ".other");
            using var dir = new TempDirectory();
            RustHelpers.HasDebugInformation(dir.Write(image)).Should().BeFalse("before the name is moved, nothing is called .debug_info");

            var relaName = BitConverter.ToUInt32(image, ElfTestFile.SectionHeaderPosition(image, 2) + ElfTestFile.SectionNameField);
            ElfTestFile.Patch(image, ElfTestFile.SectionHeaderPosition(image, 3) + ElfTestFile.SectionNameField, (ulong)(relaName + ".rela".Length), 4);

            RustHelpers.HasDebugInformation(dir.Write(image)).Should().BeTrue();
        }

        // A diagnostic must never break a pack. Whatever is wrong with the file, the answer is no and nothing is thrown. The file
        // overload swallows what the file system throws, so each file is also read through a stream, where nothing is swallowed and
        // the reader itself has to cope with the file.
        [Fact]
        public void HasDebugInformation_BrokenFiles_AreFalseAndNeverThrow()
        {
            using var dir = new TempDirectory();
            RustHelpers.HasDebugInformation(dir.Write(ValidDebugFile())).Should().BeTrue("the file that is broken in each case below is a good one");
            RustHelpers.HasDebugInformation(new MemoryStream(ValidDebugFile())).Should().BeTrue("and so it is through a stream");

            var wrong = new List<string>();
            void Check(string label, Func<bool> read)
            {
                try
                {
                    if (read())
                    {
                        wrong.Add($"{label}: said true");
                    }
                }
                catch (Exception ex)
                {
                    wrong.Add($"{label}: threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var (name, file) in BrokenFiles())
            {
                Check($"{name} (stream)", () => RustHelpers.HasDebugInformation(new MemoryStream(file)));
                Check($"{name} (file)", () => RustHelpers.HasDebugInformation(dir.Write(file)));
            }

            wrong.Should().BeEmpty();
        }

        // The names of the sections take a few hundred bytes. A file that says that they take more than a megabyte is not read, so that
        // a file cannot make the check allocate what it likes.
        [Fact]
        public void HasDebugInformation_ANameTableOfMoreThanAMegabyte_IsNotRead()
        {
            var image = ElfTestFile.Create(".text", new string('x', 1_100_000), ".debug_info");
            using var dir = new TempDirectory();

            RustHelpers.HasDebugInformation(dir.Write(image)).Should().BeFalse();
        }

        // The loop over the sections must not scan the whole table of names for each of them. This file has the most sections that a
        // file can have (65535) and the biggest table of names that the reader accepts (one megabyte). Every section but the last one
        // points at the start of that table, which has no zero byte until its end, and the last one is called .debug_info. A reader that
        // looks for the end of each name scans about 64 gigabytes for it and takes more than a second. A reader that looks at the first
        // twelve bytes of each name takes a few milliseconds. The answer is true, so the whole loop was walked.
        [Fact]
        public void HasDebugInformation_ManySectionsAndALongNameTableWithoutAnEnd_IsAnsweredQuickly()
        {
            const int sections = 65535;
            var debugInfo = Encoding.ASCII.GetBytes(".debug_info\0");
            var names = new byte[1024 * 1024];
            Array.Fill(names, (byte)'x');
            debugInfo.CopyTo(names, names.Length - debugInfo.Length);

            var tableOffset = ElfTestFile.HeaderSize + names.Length;
            var image = new byte[tableOffset + (sections * ElfTestFile.SectionHeaderSize)];
            Array.Copy(ValidDebugFile(), image, ElfTestFile.HeaderSize);
            names.CopyTo(image, ElfTestFile.HeaderSize);
            ElfTestFile.Patch(image, ElfTestFile.SectionTableOffsetField, (ulong)tableOffset, 8);
            ElfTestFile.Patch(image, ElfTestFile.SectionCountField, sections, 2);
            ElfTestFile.Patch(image, ElfTestFile.StringTableIndexField, 1, 2);

            var namesHeader = tableOffset + ElfTestFile.SectionHeaderSize;
            ElfTestFile.Patch(image, namesHeader + ElfTestFile.SectionOffsetField, ElfTestFile.HeaderSize, 8);
            ElfTestFile.Patch(image, namesHeader + ElfTestFile.SectionSizeField, (ulong)names.Length, 8);

            var lastHeader = tableOffset + ((sections - 1) * ElfTestFile.SectionHeaderSize);
            ElfTestFile.Patch(image, lastHeader + ElfTestFile.SectionNameField, (ulong)(names.Length - debugInfo.Length), 4);

            using var stream = new MemoryStream(image);
            var watch = Stopwatch.StartNew();
            var found = RustHelpers.HasDebugInformation(stream);
            watch.Stop();

            found.Should().BeTrue("the last of the sections is called .debug_info");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the reader has to look at a few bytes of each name and not at the whole table of names");
        }

        [Fact]
        public void HasDebugInformation_AMissingFileOrAFolder_IsFalse()
        {
            using var dir = new TempDirectory();

            RustHelpers.HasDebugInformation(Path.Combine(dir.Path, "none")).Should().BeFalse();
            RustHelpers.HasDebugInformation(dir.Path).Should().BeFalse();
        }

        // A debug binary is tens of megabytes. The check reads the header, the table of sections and the names, and no more.
        [Fact]
        public void HasDebugInformation_ReadsOnlyTheTablesAndNotTheContent()
        {
            var image = ElfTestFile.Create((".text", new byte[2 * 1024 * 1024]), (".debug_info", Array.Empty<byte>()));
            using var counting = new CountingStream(new MemoryStream(image));

            RustHelpers.HasDebugInformation(counting).Should().BeTrue();

            counting.BytesRead.Should().BeLessThan(4096, "the file has {0} bytes and the tables are a few hundred", image.Length);
        }

        [Fact]
        public void AssertLinuxAmd64Binary_WithDebugInformation_WarnsOnceAndDoesNotThrow()
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, ElfTestFile.Create(".text", ".debug_info"));

            var output = Capture(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path));

            output.Should().Contain($"Warning: '{Path.Combine("bin", "app")}' has debug information")
                .And.Contain("may be a debug build")
                .And.Contain("'--no-build'")
                .And.Contain("cargo build --release --target x86_64-unknown-linux-gnu")
                .And.Contain($"copy the executable to '{Path.Combine("bin", "app")}'");
            CountOf(output, "has debug information").Should().Be(1);
            RustHelpers.DebugInformationWarning.Should().NotContain("\n", "the warning is one line");
        }

        [Fact]
        public void AssertLinuxAmd64Binary_WithDebugInformation_WritesTheWarningToStandardOutput()
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, ElfTestFile.Create(".text", ".debug_info"));

            var (output, error) = CaptureStreams(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path));

            output.Should().Contain("has debug information");
            error.Should().NotContain("debug information", "the warning is on standard output, with the other messages of func pack");
        }

        [Fact]
        public void AssertLinuxAmd64Binary_WithoutDebugInformation_DoesNotWarn()
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, ElfTestFile.Create(".text", ".rodata", ".symtab"));

            var output = Capture(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path));

            output.Should().NotContain("debug information");
        }

        // The tests of the header checks write only the 20 bytes that 'func pack --no-build' reads. They have no table of sections.
        [Fact]
        public void AssertLinuxAmd64Binary_OnlyTheIdentificationBytes_DoesNotWarnAndDoesNotThrow()
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, IdentificationBytes());

            var output = Capture(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path));

            output.Should().NotContain("debug information");
        }

        // The warning is about a binary that would be packed. A binary that is refused gets only the reason.
        [Fact]
        public void AssertLinuxAmd64Binary_ADebugBinaryForAnotherMachine_ThrowsAndDoesNotWarn()
        {
            using var dir = new TempDirectory();
            var image = ElfTestFile.Create(".text", ".debug_info");
            ElfTestFile.Patch(image, 18, 0xB7, 2);
            File.WriteAllBytes(dir.BinaryPath, image);
            var output = new StringBuilder();

            var thrown = CaptureThrown(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path), output);

            thrown.Should().NotBeNull("the machine is wrong");
            output.ToString().Should().NotContain("debug information");
        }

        [Fact]
        public async Task PrepareForPublish_NoBuild_WithDebugInformation_WarnsAndDoesNotThrow()
        {
            using var dir = new TempDirectory();
            File.WriteAllText(Path.Combine(dir.Path, "Cargo.toml"), "[package]\nname = \"x\"\n");
            File.WriteAllBytes(dir.BinaryPath, ElfTestFile.Create(".text", ".debug_info"));

            var output = await CaptureAsync(() => RustHelpers.PrepareForPublish(dir.Path, noBuild: true));

            output.Should().Contain("has debug information").And.Contain("'--no-build'");
        }

        // Go is the example and stays as it was. The binary of a Go app has no such warning.
        [Fact]
        public void GoAssertLinuxAmd64Binary_WithDebugInformation_DoesNotWarn()
        {
            using var dir = new TempDirectory();
            File.WriteAllBytes(dir.BinaryPath, ElfTestFile.Create(".text", ".debug_info"));

            var output = Capture(() => GoHelpers.AssertLinuxAmd64Binary(dir.Path));

            output.Should().NotContain("debug information");
        }

        [SkipIfNotLinuxX64CargoFact]
        public async Task HasDebugInformation_ARealDebugBuild_IsTrue()
        {
            using var dir = new TempDirectory();
            WriteCargoProject(dir.Path, "debug-app", string.Empty);

            await RustHelpers.BuildProject(dir.Path);

            RustHelpers.HasDebugInformation(Path.Combine(dir.Path, RustHelpers.LocalBinaryPath)).Should().BeTrue("cargo build makes a debug build");
        }

        // After 'func start' built the app, the binary of the package is the debug binary. 'func pack --no-build' and
        // 'func publish --no-build' accept it as a valid x86_64 ELF file, and they warn about its debug information.
        [SkipIfNotLinuxX64CargoFact]
        public async Task BuildProject_ThenPackNoBuildAndPublishNoBuild_AcceptTheBinaryAndWarn()
        {
            using var dir = new TempDirectory();
            WriteCargoProject(dir.Path, "start-then-pack", string.Empty);
            await RustHelpers.BuildProject(dir.Path);

            RustHelpers.GetPackFiles(dir.Path).Should().Contain(dir.BinaryPath, "the binary of 'func start' is the file of the package");
            var packOutput = Capture(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path));
            var publishOutput = await CaptureAsync(() => RustHelpers.PrepareForPublish(dir.Path, noBuild: true));

            packOutput.Should().Contain($"Warning: '{Path.Combine("bin", "app")}' has debug information");
            publishOutput.Should().Contain($"Warning: '{Path.Combine("bin", "app")}' has debug information");
        }

        [SkipIfNotLinuxX64CargoFact]
        public async Task HasDebugInformation_ARealReleaseBuild_IsFalse()
        {
            using var dir = new TempDirectory();
            WriteCargoProject(dir.Path, "release-app", string.Empty);

            await RustHelpers.BuildForLinux(dir.Path);

            RustHelpers.HasDebugInformation(dir.BinaryPath).Should().BeFalse("the build of func pack is a release build");
            var output = Capture(() => RustHelpers.AssertLinuxAmd64Binary(dir.Path));
            output.Should().NotContain("debug information");
        }

        // The limit of the check, which is why the text says 'may be'. A release build with the debug sections on has them too.
        [SkipIfNotLinuxX64CargoFact]
        public async Task HasDebugInformation_ARealReleaseBuildWithDebugOn_IsTrue()
        {
            using var dir = new TempDirectory();
            WriteCargoProject(dir.Path, "release-debug-app", "\n[profile.release]\ndebug = true\n");

            await RustHelpers.BuildForLinux(dir.Path);

            RustHelpers.HasDebugInformation(dir.BinaryPath).Should().BeTrue("the section is there, whatever the profile");
        }

        private static byte[] ValidDebugFile() => ElfTestFile.Create(".text", ".debug_info");

        // The 20 bytes that the other tests of the header checks write, with 64 bits and little endian but no section table.
        private static byte[] IdentificationBytes()
        {
            var header = new byte[20];
            header[0] = 0x7F;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = 2;
            header[5] = 1;
            header[18] = 0x3E;
            return header;
        }

        private static byte[] Patched(int position, ulong value, int size)
        {
            var file = ValidDebugFile();
            ElfTestFile.Patch(file, position, value, size);
            return file;
        }

        private static byte[] PatchedSection(int section, int field, ulong value, int size)
        {
            var file = ValidDebugFile();
            ElfTestFile.Patch(file, ElfTestFile.SectionHeaderPosition(file, section) + field, value, size);
            return file;
        }

        // The valid file has the sections: null (0), .text (1), .debug_info (2), and the string table (3).
        private static Dictionary<string, byte[]> BrokenFiles()
        {
            const int debugInfo = 2;
            const int strings = 3;
            var valid = ValidDebugFile();
            var debugInfoName = BitConverter.ToUInt32(valid, ElfTestFile.SectionHeaderPosition(valid, debugInfo) + ElfTestFile.SectionNameField);

            var wrapsAround = PatchedSection(strings, ElfTestFile.SectionOffsetField, ulong.MaxValue - 7, 8);
            ElfTestFile.Patch(wrapsAround, ElfTestFile.SectionHeaderPosition(wrapsAround, strings) + ElfTestFile.SectionSizeField, 16, 8);

            var notEndedBeforeTheTableEnds = PatchedSection(strings, ElfTestFile.SectionSizeField, (ulong)(debugInfoName + ".debug_info".Length), 8);

            return new Dictionary<string, byte[]>
            {
                ["empty file"] = Array.Empty<byte>(),
                ["only the 20 identification bytes"] = IdentificationBytes(),
                ["ten bytes of the header"] = valid[..10],
                ["not an ELF file"] = Patched(0, 0, 4),
                ["32 bit class"] = Patched(4, 0x0101, 2),
                ["big endian"] = Patched(4, 0x0202, 2),
                ["no table of sections"] = Patched(ElfTestFile.SectionTableOffsetField, 0, 8),
                ["table of sections past the end"] = Patched(ElfTestFile.SectionTableOffsetField, 0x10000000, 8),
                ["offset of the table near the maximum"] = Patched(ElfTestFile.SectionTableOffsetField, ulong.MaxValue - 15, 8),
                ["table of sections cut short"] = valid[..^10],
                ["section header size 0"] = Patched(ElfTestFile.SectionHeaderSizeField, 0, 2),
                ["section header size 32"] = Patched(ElfTestFile.SectionHeaderSizeField, 32, 2),
                ["section header size 128"] = Patched(ElfTestFile.SectionHeaderSizeField, 128, 2),
                ["no sections"] = Patched(ElfTestFile.SectionCountField, 0, 2),
                ["more sections than the file holds"] = Patched(ElfTestFile.SectionCountField, 0xFFFE, 2),
                ["the string table is the null section"] = Patched(ElfTestFile.StringTableIndexField, 0, 2),
                ["the string table is just past the sections"] = Patched(ElfTestFile.StringTableIndexField, 4, 2),
                ["the string table is past the sections"] = Patched(ElfTestFile.StringTableIndexField, 99, 2),
                ["the string table index is the escape value"] = Patched(ElfTestFile.StringTableIndexField, 0xFFFF, 2),
                ["string table offset past the end"] = PatchedSection(strings, ElfTestFile.SectionOffsetField, 0xFFFFFFFF, 8),
                ["string table size past the end"] = PatchedSection(strings, ElfTestFile.SectionSizeField, 0xFFFFFFFF, 8),
                ["string table size is the maximum"] = PatchedSection(strings, ElfTestFile.SectionSizeField, ulong.MaxValue, 8),
                ["string table offset and size wrap around"] = wrapsAround,
                ["name offset past the string table"] = PatchedSection(debugInfo, ElfTestFile.SectionNameField, 0x7FFFFFFF, 4),
                ["the name has no end inside the string table"] = notEndedBeforeTheTableEnds,
            };
        }

        private static int CountOf(string text, string part)
        {
            var count = 0;
            for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private static void WriteCargoProject(string path, string packageName, string manifestExtra)
        {
            Directory.CreateDirectory(Path.Combine(path, "src"));
            File.WriteAllText(Path.Combine(path, "Cargo.toml"), $"[package]\nname = \"{packageName}\"\nversion = \"0.1.0\"\nedition = \"2024\"\n{manifestExtra}");
            File.WriteAllText(Path.Combine(path, "src", "main.rs"), "fn main() { println!(\"hello\"); }\n");
        }

        private static string Capture(Action action)
        {
            var output = new StringBuilder();
            CaptureThrown(action, output);
            return output.ToString();
        }

        // Runs the action with one console for standard output and one for standard error, and gives back what each one got.
        private static (string Out, string Error) CaptureStreams(Action action)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var oldOut = ColoredConsole.Out;
            var oldErr = ColoredConsole.Error;
            try
            {
                ColoredConsole.Out = RecordingConsole(output);
                ColoredConsole.Error = RecordingConsole(error);
                action();
            }
            finally
            {
                ColoredConsole.Out = oldOut;
                ColoredConsole.Error = oldErr;
            }

            return (output.ToString(), error.ToString());
        }

        // Runs the action with a console that records what it writes, and gives back what the action threw.
        private static Exception CaptureThrown(Action action, StringBuilder output)
        {
            var console = RecordingConsole(output);
            var oldOut = ColoredConsole.Out;
            var oldErr = ColoredConsole.Error;
            try
            {
                ColoredConsole.Out = console;
                ColoredConsole.Error = console;
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                ColoredConsole.Out = oldOut;
                ColoredConsole.Error = oldErr;
            }
        }

        private static async Task<string> CaptureAsync(Func<Task> action)
        {
            var output = new StringBuilder();
            var console = RecordingConsole(output);
            var oldOut = ColoredConsole.Out;
            var oldErr = ColoredConsole.Error;
            try
            {
                ColoredConsole.Out = console;
                ColoredConsole.Error = console;
                await action();
            }
            finally
            {
                ColoredConsole.Out = oldOut;
                ColoredConsole.Error = oldErr;
            }

            return output.ToString();
        }

        private static IConsoleWriter RecordingConsole(StringBuilder output)
        {
            var console = Substitute.For<IConsoleWriter>();
            console.WriteLine(Arg.Do<object>(o => output.AppendLine(o?.ToString()))).Returns(console);
            console.Write(Arg.Do<object>(o => output.Append(o?.ToString()))).Returns(console);
            return console;
        }

        private sealed class CountingStream(Stream inner) : Stream
        {
            public long BytesRead { get; private set; }

            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => false;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = inner.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                var read = inner.Read(buffer);
                BytesRead += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class TempDirectory : IDisposable
        {
            private int _files;

            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "func-rust-debug-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(System.IO.Path.Combine(Path, "bin"));
            }

            public string Path { get; }

            public string BinaryPath => System.IO.Path.Combine(Path, "bin", "app");

            // Writes a file with a new name each time, so that a test can look at many files without a clash.
            public string Write(byte[] bytes)
            {
                var path = System.IO.Path.Combine(Path, $"file{_files++}");
                File.WriteAllBytes(path, bytes);
                return path;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; a scanner may still hold a handle on a freshly built file.
                }
            }
        }
    }
}
