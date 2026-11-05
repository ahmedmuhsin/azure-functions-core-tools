// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using AwesomeAssertions;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // 'func start --no-build' for a Rust app needs no Rust toolchain. The documents that a Rust user reads say so.
    public class RustNoBuildTextTests
    {
        [Fact]
        public void DocsRowOfRust_SaysThatNoBuildNeedsNoToolchain()
        {
            var row = RowOfRuntime("Rust");

            row.Should().Contain("`--no-build`").And.Contain("toolchain");
        }

        [Fact]
        public void ReleaseNote_SaysThatNoBuildNeedsNoRustToolchain()
        {
            var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "release_notes.md"))
                .Where(line => line.Contains("Rust", StringComparison.OrdinalIgnoreCase))
                .ToList();

            lines.Should().ContainSingle("the release note has one line about Rust");
            lines[0].Should().Contain("`func start --no-build`").And.Contain("toolchain");
        }

        // The debug binary of 'func start' is in its own folder, and 'func pack' never reads that folder. The docs say where it is.
        [Fact]
        public void DocsRowOfRust_SaysWhereFuncStartPutsItsBinaryAndThatPackNeverReadsIt()
        {
            var row = RowOfRuntime("Rust");

            row.Should().Contain("`target/func/app`").And.Contain(@"`target\func\app.exe`").And.Contain("`func pack`");
            row.Should().NotContain("copies the binary to `bin/app`", "that is where 'func start' put it before");
            row.Should().NotContain("Other platforms keep `bin/app`", "the host runs the binary of 'func start' on every OS");
        }

        [Fact]
        public void ReleaseNote_SaysWhereFuncStartBuildsAndThatPackNeverPacksIt()
        {
            var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "release_notes.md"))
                .Where(line => line.Contains("Rust", StringComparison.OrdinalIgnoreCase))
                .ToList();

            lines.Should().ContainSingle("the release note has one line about Rust");
            lines[0].Should().Contain("`target/func`").And.Contain("`func pack --no-build`").And.Contain("debug build");
        }

        // A binary that somebody copies to bin/app can still be a debug build, so 'func pack --no-build' warns when it has the sections
        // of a debug build. The docs and the release note say so.
        [Fact]
        public void DocsRowOfRust_SaysThatPackNoBuildWarnsAboutDebugInformation()
        {
            var row = RowOfRuntime("Rust");

            row.Should().Contain("`func pack --no-build`").And.Contain("warn").And.Contain("`bin/app` has debug information");
        }

        [Fact]
        public void ReleaseNote_SaysThatPackNoBuildWarnsAboutDebugInformation()
        {
            var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "release_notes.md"))
                .Where(line => line.Contains("Rust", StringComparison.OrdinalIgnoreCase))
                .ToList();

            lines.Should().ContainSingle("the release note has one line about Rust");
            lines[0].Should().Contain("`func pack --no-build` warns").And.Contain("`bin/app` has debug information");
        }

        // The row of the table 'Per-Runtime Setup' of the docs for one runtime.
        private static string RowOfRuntime(string runtime)
        {
            var rows = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "host-configuration.md"))
                .Where(line => line.StartsWith($"| **{runtime}** |", StringComparison.Ordinal))
                .ToList();

            rows.Should().ContainSingle($"the docs have one row for {runtime}");
            return rows[0];
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
    }
}
