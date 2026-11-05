// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.IO.Compression;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Xunit;

namespace Azure.Functions.Cli.UnitTests.HelperTests
{
    // The --no-build row prints a line to the shared console, so this runs in the collection that does not run
    // in parallel, where no other test captures the console while it runs.
    [Collection("NativeWorkerRuntimeTests")]
    public class RustZipHelperTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetAppZipFile_RustRuntime_ContainsOnlyHostJsonAndApp(bool noBuild)
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            Directory.CreateDirectory(Path.Combine(dir, "target", "x86_64-unknown-linux-gnu", "release"));

            try
            {
                File.WriteAllText(Path.Combine(dir, "host.json"), "{}");
                File.WriteAllBytes(Path.Combine(dir, "bin", "app"), new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1 });

                // Files that must NOT be picked up. Rust uses an explicit allowlist, not funcignore.
                File.WriteAllText(Path.Combine(dir, "Cargo.toml"), "[package]\nname = \"test\"\n");
                File.WriteAllText(Path.Combine(dir, "Cargo.lock"), "# lock\n");
                File.WriteAllText(Path.Combine(dir, "src", "main.rs"), "fn main() {}\n");
                File.WriteAllText(Path.Combine(dir, "local.settings.json"), "{}");
                File.WriteAllBytes(Path.Combine(dir, "bin", "app.exe"), new byte[] { (byte)'M', (byte)'Z' });
                File.WriteAllBytes(Path.Combine(dir, "target", "x86_64-unknown-linux-gnu", "release", "test"), new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });

                var stream = await ZipHelper.GetAppZipFile(dir, buildNativeDeps: false, BuildOption.Default, noBuild: noBuild, WorkerRuntime.Rust);

                Assert.NotNull(stream);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var entries = archive.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();

                Assert.Equal(new[] { "app", "host.json" }, entries);

                // The Unix exec bit on the binary, or the Linux host cannot launch the extracted file.
                var appEntry = archive.Entries.Single(e => e.FullName == "app");
                Assert.Equal(0x49, (appEntry.ExternalAttributes >> 16) & 0x49);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
