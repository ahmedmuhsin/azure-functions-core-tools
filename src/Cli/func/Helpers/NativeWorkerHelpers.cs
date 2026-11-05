// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Azure.Functions.Cli.Common;
using Colors.Net;
using static Azure.Functions.Cli.Common.OutputTheme;

namespace Azure.Functions.Cli.Helpers
{
    /// <summary>
    /// What the native languages (Go and Rust) share. Both run as <c>FUNCTIONS_WORKER_RUNTIME=native</c>
    /// with the shipped <c>workers/native/worker.config.json</c>, whose default executable is <c>bin/app</c>.
    /// </summary>
    internal static class NativeWorkerHelpers
    {
        internal const string BinDirectoryName = "bin";
        internal const string BinaryName = "app";
        private const int ToolVersionTimeoutMs = 5000;

        /// <summary>
        /// Gets the worker binary path relative to the project root. Windows needs the <c>.exe</c> suffix.
        /// </summary>
        internal static string LocalBinaryPath => Path.Combine(BinDirectoryName, OperatingSystem.IsWindows() ? $"{BinaryName}.exe" : BinaryName);

        /// <summary>
        /// Gets the message for <c>func init --docker</c> on a native language. Rust says only that Docker
        /// is not available, because a Rust app is a preview and nothing should suggest that it will be supported.
        /// </summary>
        internal static string GetDockerNotAvailableMessage(WorkerRuntime runtime)
        {
            return runtime == WorkerRuntime.Rust
                ? "Docker is not available for the Rust worker runtime."
                : $"Docker support for the {runtime} worker runtime is not yet available.";
        }

        /// <summary>
        /// Runs <c>&lt;executable&gt; &lt;arguments&gt;</c> and reads the first capture group of
        /// <paramref name="versionPattern"/> from its output. Returns <c>null</c>, after a warning on
        /// stderr that says why, when the tool is missing, fails, times out or prints something unexpected.
        /// </summary>
        internal static Task<WorkerLanguageVersionInfo> GetToolVersion(WorkerRuntime runtime, string displayName, string executable, string arguments, Regex versionPattern, string workingDirectory = null)
        {
            // Synchronous Process I/O for reliability: async I/O (ReadToEndAsync / WaitForExitAsync)
            // intermittently returned null on Linux CI agents for sub-second commands like 'go version',
            // producing confusing "Could not find a Go installation" errors. Surface failure reasons to
            // stderr rather than returning null silently so CI logs are diagnosable.
            void Warn(string message) => ColoredConsole.Error.WriteLine(WarningColor($"Warning: {message}"));

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };

                // Some tools pick their version from the folder they run in, for example rustup from rust-toolchain.toml.
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    process.StartInfo.WorkingDirectory = workingDirectory;
                }

                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();

                if (!process.WaitForExit(ToolVersionTimeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited between WaitForExit returning and Kill, which is benign.
                    }

                    Warn($"'{executable} {arguments}' did not complete within {ToolVersionTimeoutMs}ms.");
                    return Task.FromResult<WorkerLanguageVersionInfo>(null);
                }

                if (process.ExitCode != 0)
                {
                    Warn($"'{executable} {arguments}' exited with code {process.ExitCode}.");
                    return Task.FromResult<WorkerLanguageVersionInfo>(null);
                }

                var match = versionPattern.Match(output);
                if (match.Success)
                {
                    return Task.FromResult(new WorkerLanguageVersionInfo(runtime, match.Groups[1].Value, executable));
                }

                Warn($"'{executable} {arguments}' returned unparseable output: '{output}'.");
            }
            catch (Exception ex)
            {
                Warn($"unable to detect {displayName} version: {ex.GetType().Name}: {ex.Message}");
            }

            return Task.FromResult<WorkerLanguageVersionInfo>(null);
        }

        /// <summary>
        /// Ensures the binary at <c>bin/app</c> in <paramref name="workingDirectory"/> is an x86_64 Linux
        /// executable that the Linux Functions host can run, by checking its ELF header. Called by
        /// <c>func pack --no-build</c> to confirm the binary was built for Linux before packaging.
        /// <paramref name="targetNote"/> is the sentence that says which target Core Tools packs for, and
        /// <paramref name="manualBuild"/> is the quoted manual build step that the messages suggest.
        /// <paramref name="binaryExists"/> tells whether there is a binary at a path, and is
        /// <see cref="FileSystemHelpers.FileExists"/> when it is <c>null</c>.
        /// </summary>
        internal static void AssertLinuxAmd64Binary(string workingDirectory, string languageName, string targetNote, string manualBuild, Func<string, bool> binaryExists = null)
        {
            workingDirectory ??= Environment.CurrentDirectory;
            binaryExists ??= FileSystemHelpers.FileExists;
            var binaryPath = Path.Combine(workingDirectory, BinDirectoryName, BinaryName);
            var displayPath = Path.Combine(BinDirectoryName, BinaryName);

            void ValidateBinaryExists()
            {
                if (!binaryExists(binaryPath))
                {
                    throw new CliException(
                        $"Could not find a built {languageName} binary '{displayPath}' in '{workingDirectory}'. " +
                        $"Run 'func pack' without '--no-build' to cross-compile, or run " +
                        $"{manualBuild} manually.");
                }
            }

            byte[] ReadElfHeader()
            {
                // Read the first 20 bytes of the ELF header so we can validate magic, class, data, and machine.
                // Layout reference: https://refspecs.linuxfoundation.org/elf/gabi4+/ch4.eheader.html
                var header = new byte[20];
                using (var fs = new FileStream(binaryPath, FileMode.Open, FileAccess.Read))
                {
                    int read = fs.Read(header, 0, header.Length);
                    if (read < header.Length)
                    {
                        throw new CliException(
                            $"'{binaryPath}' is too small to be an ELF binary. " +
                            "Re-run 'func pack' without '--no-build' to produce a linux/amd64 binary.");
                    }
                }

                return header;
            }

            // ELF magic is the file signature that identifies an ELF (Executable and Linkable Format) binary
            void ValidateElfMagic(byte[] header)
            {
                // ELF magic: 0x7F 'E' 'L' 'F'
                bool isElf = header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F';
                if (!isElf)
                {
                    throw new CliException(
                        $"'{binaryPath}' is not an ELF binary. {targetNote} " +
                        $"Re-run 'func pack' without '--no-build', or build with {manualBuild}");
                }
            }

            void ValidateElfFormat(byte[] header)
            {
                // EI_CLASS == 2 (64-bit), EI_DATA == 1 (little endian)
                if (header[4] != 2 || header[5] != 1)
                {
                    throw new CliException(
                        $"'{binaryPath}' is not a 64-bit little-endian ELF binary. {targetNote} " +
                        $"Re-run 'func pack' without '--no-build', or build with {manualBuild}");
                }
            }

            void ValidateElfMachine(byte[] header)
            {
                // e_machine is at offset 18, 2 bytes little-endian. 0x3E == EM_X86_64.
                ushort eMachine = (ushort)(header[18] | (header[19] << 8));
                if (eMachine != 0x3E)
                {
                    throw new CliException(
                        $"'{binaryPath}' targets machine 0x{eMachine:X4}, not x86_64. {targetNote} " +
                        $"Re-run 'func pack' without '--no-build', or build with {manualBuild}");
                }
            }

            // Execute validations in order
            ValidateBinaryExists();
            var header = ReadElfHeader();
            ValidateElfMagic(header);
            ValidateElfFormat(header);
            ValidateElfMachine(header);
        }

        /// <summary>
        /// Returns the explicit allowlist of files in the deployment zip of a native app: <c>host.json</c>
        /// when it exists, and the Linux binary. Centralizes the list so that <see cref="ZipHelper"/>
        /// (zip composition) and <c>func publish --list-included-files</c> stay in sync.
        /// </summary>
        internal static IEnumerable<string> GetPackFiles(string functionAppRoot)
        {
            functionAppRoot ??= Environment.CurrentDirectory;

            // host.json is optional; only include it in the allowlist when it exists.
            var hostJsonPath = Path.Combine(functionAppRoot, Constants.HostJsonFileName);
            if (FileSystemHelpers.FileExists(hostJsonPath))
            {
                yield return hostJsonPath;
            }

            yield return Path.Combine(functionAppRoot, BinDirectoryName, BinaryName);
        }
    }
}
