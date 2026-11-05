// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Functions.Cli.Actions.LocalActions.PackAction;
using Azure.Functions.Cli.Common;
using Colors.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Azure.Functions.Cli.Common.OutputTheme;

namespace Azure.Functions.Cli.Helpers
{
    internal static class RustHelpers
    {
        internal const string CargoTomlFileName = "Cargo.toml";
        internal const string SourceDirectoryName = "src";
        internal const string MainRsFileName = "main.rs";
        internal const string DefaultPackageName = "app";
        internal const string CargoExecutable = "cargo";
        internal const string LinuxTarget = "x86_64-unknown-linux-gnu";

        // Where Cargo writes build output, and the folder inside it that holds the binary of 'func start'. Other Cargo tools keep their
        // output in target/<tool>, such as target/lambda for cargo-lambda.
        internal const string TargetDirectoryName = "target";
        internal const string FuncDirectoryName = "func";

        // The last sentence is advice and names no glibc version, because the glibc of the platform that runs the app can change.
        internal const string LinuxToolchainHint =
            $"Hint: 'func pack' builds for {LinuxTarget}. Install the target with 'rustup target add {LinuxTarget}' and make sure a linker for it is available. " +
            "A build from Windows or macOS needs a cross linker, and a C toolchain for that target if a dependency builds C code. " +
            "The binary links glibc dynamically, so build against a glibc that is not newer than the one on the system that runs the app.";

        private const int MinimumRustMajorVersion = 1;
        private const int MinimumRustMinorVersion = 90;
        private const string TestedRustVersion = "1.90.0";
        private const string PackageNamePlaceholder = "{PackageName}";
        private const string CargoBinFolderRule = "!src/bin/";
        private const int MaximumQuotedLineLength = 300;

        // The section that holds the debug information of a debug build.
        private const string DebugInformationSectionName = ".debug_info";

        // The error that Windows and Unix both give when a program does not exist (ERROR_FILE_NOT_FOUND and ENOENT).
        private const int ProgramNotFoundErrorCode = 2;

        // Names that Cargo or rustc refuse for a package: Rust keywords, names Cargo keeps for its
        // own use, and Windows device names that cannot be a file name.
        private const string ReservedPackageNames =
            "as async await break const continue crate dyn else enum extern false fn for gen if impl in let loop " +
            "match mod move mut pub ref return self static struct super trait true type unsafe use where while " +
            "abstract become box do final macro override priv try typeof unsized virtual yield " +
            "test core std alloc proc_macro proc-macro build deps examples incremental " +
            "con prn aux nul com1 com2 com3 com4 com5 com6 com7 com8 com9 lpt1 lpt2 lpt3 lpt4 lpt5 lpt6 lpt7 lpt8 lpt9";

        private static readonly HashSet<string> _reservedPackageNames = new HashSet<string>(ReservedPackageNames.Split(' '), StringComparer.Ordinal);

        // Parses the "cargo 1.90.0 (840b83a10 2025-07-30)" format.
        private static readonly Regex _cargoVersionPattern = new Regex(@"cargo (\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

        // Matches rustc's "error: linker `cc` not found".
        private static readonly Regex _missingLinkerPattern = new Regex(@"linker `[^`]+` not found", RegexOptions.Compiled);

        // 'cargo --version' answers at once, unless rustup installs the toolchain that a rust-toolchain.toml names.
        // The limit has to be long enough for that install. It only ends a hang.
        private static readonly TimeSpan _cargoVersionTimeout = TimeSpan.FromSeconds(120);

        // How long the probe waits for the rest of the output once the program has ended or been stopped.
        private static readonly TimeSpan _pumpGrace = TimeSpan.FromSeconds(3);

        /// <summary>
        /// The binary that <c>func start</c> builds and the host runs, relative to the project folder: <c>target/func/app</c>
        /// (<c>target\func\app.exe</c> on Windows). It is a debug build, in a folder of its own under <c>target/</c>, where a Rust
        /// developer expects build output and where git ignores it. It is not <c>bin/app</c>, which is the binary of the package:
        /// only <c>func pack</c> writes that file, and only <c>func pack --no-build</c> and <c>func publish --no-build</c> read it,
        /// so a debug build that a start left behind can never be packed by accident.
        /// </summary>
        internal static string LocalBinaryPath => Path.Combine(TargetDirectoryName, FuncDirectoryName, OperatingSystem.IsWindows() ? $"{NativeWorkerHelpers.BinaryName}.exe" : NativeWorkerHelpers.BinaryName);

        /// <summary>
        /// The warning of <c>func pack --no-build</c> and <c>func publish --no-build</c> for a <c>bin/app</c> that has debug information.
        /// A profile cannot be read from a binary, so the text says that it may be a debug build. It is a warning and not an error,
        /// because a release profile can have debug information on purpose.
        /// </summary>
        internal static string DebugInformationWarning =>
            $"Warning: '{PackedBinaryDisplayPath}' has debug information, so it may be a debug build, which is larger and slower than a release build. " +
            $"To use a release build, run without '--no-build', or build with 'cargo build --release --target {LinuxTarget}' and copy the executable to '{PackedBinaryDisplayPath}'.";

        private static string PackedBinaryDisplayPath => Path.Combine(NativeWorkerHelpers.BinDirectoryName, NativeWorkerHelpers.BinaryName);

        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// Gets the version of the Rust toolchain that cargo uses in <paramref name="workingDirectory"/>, or in the
        /// current folder when it is <c>null</c>. The folder matters because rustup reads rust-toolchain.toml there.
        /// Returns <c>null</c> when there is no cargo, and throws a <see cref="CliException"/> that says why when cargo
        /// is there but does not answer with a version.
        /// </summary>
        public static async Task<WorkerLanguageVersionInfo> GetEnvironmentCargoVersion(string workingDirectory = null)
        {
            return await Task.Run(() => ProbeCargoVersion(CargoExecutable, "--version", workingDirectory, _cargoVersionTimeout));
        }

        /// <summary>
        /// Runs <c>&lt;executable&gt; &lt;arguments&gt;</c> and reads the Rust version from what it prints. Returns <c>null</c>, after a
        /// warning, only when the program does not exist, because that is what a missing toolchain looks like. When the program runs and
        /// no version comes out, a <see cref="CliException"/> says what happened, so that nobody is told to install Rust while cargo is
        /// there and fails. Both pipes are read at the same time, so a program that writes a lot to stderr cannot block, and a program
        /// that runs past <paramref name="timeout"/> is stopped.
        /// </summary>
        internal static WorkerLanguageVersionInfo ProbeCargoVersion(string executable, string arguments, string workingDirectory, TimeSpan timeout)
        {
            var command = $"{executable} {arguments}";
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

            // rustup reads rust-toolchain.toml from the folder that it runs in.
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                process.StartInfo.WorkingDirectory = workingDirectory;
            }

            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ProgramNotFoundErrorCode)
            {
                ColoredConsole.Error.WriteLine(WarningColor($"Warning: unable to detect Rust version: {ex.GetType().Name}: {ex.Message}"));
                return null;
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                throw new CliException($"Could not run '{command}': {ex.Message}");
            }

            // The synchronous Read of a reader is used on purpose. Async process I/O was unreliable for short commands on Linux agents,
            // which is why NativeWorkerHelpers reads synchronously too.
            var output = new StringBuilder();
            var error = new StringBuilder();
            var pumps = new[]
            {
                Task.Run(() => Drain(process.StandardOutput, output)),
                Task.Run(() => Drain(process.StandardError, error)),
            };

            var milliseconds = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
            var finished = process.WaitForExit(milliseconds);
            if (!finished)
            {
                StopProcessTree(process);
            }

            // The pipes close with the program. A grandchild that keeps them open must not keep the probe waiting.
            Task.WaitAll(pumps, _pumpGrace);
            var said = FirstLine(error.ToString());

            if (!finished)
            {
                throw new CliException(
                    $"'{command}' did not finish within {Math.Ceiling(timeout.TotalSeconds)} seconds and was stopped." +
                    (said == null ? string.Empty : $" It said: {said}") +
                    $" If rustup is installing a toolchain, run '{command}' in this folder once and let it finish.");
            }

            if (process.ExitCode != 0)
            {
                throw new CliException($"'{command}' failed with exit code {process.ExitCode}." + (said == null ? string.Empty : $" It said: {said}"));
            }

            var version = ParseCargoVersion(output.ToString());
            if (version == null)
            {
                throw new CliException($"Could not read the Rust version from '{command}'. It printed: {FirstLine(output.ToString()) ?? "nothing"}");
            }

            return new WorkerLanguageVersionInfo(WorkerRuntime.Rust, version, executable);
        }

        private static void Drain(TextReader reader, StringBuilder sink)
        {
            var buffer = new char[4096];
            try
            {
                var count = reader.Read(buffer, 0, buffer.Length);
                while (count > 0)
                {
                    lock (sink)
                    {
                        sink.Append(buffer, 0, count);
                    }

                    count = reader.Read(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The pipe was closed under the reader. What was read until then is all there is.
            }
        }

        private static void StopProcessTree(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException || ex is AggregateException)
            {
                // The program ended by itself, or the system refuses to stop it. The probe reports the timeout either way.
            }
        }

        // The first line that holds text, cut to a length that fits in an error message. Null when there is none.
        private static string FirstLine(string text)
        {
            foreach (var line in text.Split('\n', '\r'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed.Length > MaximumQuotedLineLength ? trimmed.Substring(0, MaximumQuotedLineLength) + "..." : trimmed;
                }
            }

            return null;
        }

        internal static string ParseCargoVersion(string output)
        {
            var match = _cargoVersionPattern.Match(output ?? string.Empty);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static void AssertCargoVersion(WorkerLanguageVersionInfo cargoVersion)
        {
            if (cargoVersion?.Version == null)
            {
                throw new CliException(
                    $"Could not find a Rust toolchain ('{CargoExecutable}'). Rust {MinimumRustMajorVersion}.{MinimumRustMinorVersion} or later is required (tested with Rust {TestedRustVersion}). " +
                    "Please install Rust from https://rustup.rs/");
            }

            if (GlobalCoreToolsSettings.IsVerbose)
            {
                ColoredConsole.WriteLine(VerboseColor($"Found Rust version {cargoVersion.Version} ({cargoVersion.ExecutablePath})."));
            }

            if (cargoVersion.Major == null || cargoVersion.Minor == null)
            {
                throw new CliException(
                    $"Unable to parse the Rust version '{cargoVersion.Version}'. " +
                    $"Rust {MinimumRustMajorVersion}.{MinimumRustMinorVersion} or later is required (tested with Rust {TestedRustVersion}).");
            }

            // Accept any major version > 1, or major == 1 with minor >= 90
            if (cargoVersion.Major > MinimumRustMajorVersion
                || (cargoVersion.Major == MinimumRustMajorVersion && cargoVersion.Minor >= MinimumRustMinorVersion))
            {
                return;
            }

            throw new CliException(
                $"Rust version {cargoVersion.Version} is not supported. " +
                $"Rust {MinimumRustMajorVersion}.{MinimumRustMinorVersion} or later is required (tested with Rust {TestedRustVersion}). " +
                "Please update Rust with 'rustup update'.");
        }

        /// <summary>
        /// Turns a folder name into a valid Cargo package name: lower case ASCII letters, digits,
        /// '-' and '_', starting with a letter or '_', and not a reserved name.
        /// </summary>
        internal static string SanitizePackageName(string folderName)
        {
            var name = new StringBuilder();
            foreach (var character in (folderName ?? string.Empty).ToLowerInvariant())
            {
                bool valid = (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '_' || character == '-';
                char mapped = valid ? character : '-';

                // No leading or repeated dashes.
                if (mapped == '-' && (name.Length == 0 || name[name.Length - 1] == '-'))
                {
                    continue;
                }

                name.Append(mapped);
            }

            var sanitized = name.ToString().TrimEnd('-');
            if (sanitized.Length == 0)
            {
                return DefaultPackageName;
            }

            if (char.IsDigit(sanitized[0]))
            {
                sanitized = $"{DefaultPackageName}-{sanitized}";
            }

            return _reservedPackageNames.Contains(sanitized) ? $"{sanitized}-app" : sanitized;
        }

        /// <summary>
        /// Scaffolds the Cargo manifest, <c>src/main.rs</c> and <c>.funcignore</c> into the current
        /// directory, never overwriting a file that exists. There is no
        /// dependency step here, because <c>cargo build</c> resolves and locks dependencies itself.
        /// </summary>
        public static async Task SetupProject(string packageName)
        {
            var cargoToml = (await StaticResources.CargoToml).Replace(PackageNamePlaceholder, SanitizePackageName(packageName));
            await FileSystemHelpers.WriteFileIfNotExists(CargoTomlFileName, EndWithNewLine(cargoToml));

            FileSystemHelpers.EnsureDirectory(SourceDirectoryName);
            await FileSystemHelpers.WriteFileIfNotExists(Path.Combine(SourceDirectoryName, MainRsFileName), EndWithNewLine(await StaticResources.MainRs));
            await FileSystemHelpers.WriteFileIfNotExists(Constants.FuncIgnoreFile, await StaticResources.FuncIgnore);
        }

        /// <summary>
        /// Adds <c>target/</c>, where Cargo writes build output, to a <c>.gitignore</c> that does not
        /// ignore it yet.
        /// </summary>
        internal static string IgnoreTargetDirectory(string gitIgnoreContent)
        {
            gitIgnoreContent ??= string.Empty;
            if (gitIgnoreContent.Split('\n').Any(line => line.Trim() is "target/" or "/target/" or "/target" or "target"))
            {
                return gitIgnoreContent;
            }

            return AppendGitIgnoreRule(gitIgnoreContent, "# Rust build output", "target/");
        }

        /// <summary>
        /// Lets <c>src/bin</c> back into a <c>.gitignore</c> whose <c>bin/</c> rules hide it. Cargo builds extra
        /// binaries from <c>src/bin</c>, and a rule without a leading slash matches a folder with that name at
        /// any depth. The negation goes last because the last matching rule wins.
        /// </summary>
        internal static string KeepCargoBinFolderTracked(string gitIgnoreContent)
        {
            gitIgnoreContent ??= string.Empty;
            if (gitIgnoreContent.Split('\n').Any(line => line.Trim() == CargoBinFolderRule))
            {
                return gitIgnoreContent;
            }

            return AppendGitIgnoreRule(gitIgnoreContent, "# Cargo builds extra binaries from src/bin, which the bin/ rules above would hide", CargoBinFolderRule);
        }

        /// <summary>
        /// Builds the user's package in the debug profile and copies the binary it produces to <see cref="LocalBinaryPath"/>,
        /// <c>target/func/app</c> (<c>target\func\app.exe</c> on Windows), in <paramref name="workingDirectory"/>. Core Tools
        /// points the host at that file, because the shipped native worker config names <c>bin/app</c>, which is the binary of the
        /// package. Cargo has no stable option for the output path, which depends on the profile, the target, the
        /// package name and whether the package belongs to a workspace, so the path comes from cargo itself.
        /// </summary>
        public static async Task BuildProject(string workingDirectory = null)
        {
            workingDirectory ??= Environment.CurrentDirectory;

            ColoredConsole.WriteLine($"Building Rust worker binary '{LocalBinaryPath}'...");
            await BuildAndCopyAsync(
                workingDirectory,
                string.Empty,
                LocalBinaryPath,
                (exitCode, _) => new CliException($"Rust build failed with exit code {exitCode}. See output above for details."));
        }

        /// <summary>
        /// Verifies that the compiled Rust worker binary exists in
        /// <paramref name="workingDirectory"/>. Throws a <see cref="CliException"/>
        /// with an actionable message when missing, used by <c>func start --no-build</c>.
        /// </summary>
        public static void AssertBinaryExists(string workingDirectory = null)
        {
            workingDirectory ??= Environment.CurrentDirectory;
            var binaryPath = Path.Combine(workingDirectory, LocalBinaryPath);

            if (!BinaryExists(binaryPath))
            {
                throw new CliException(
                    $"Could not find a built Rust binary '{LocalBinaryPath}' in '{workingDirectory}'. " +
                    $"Run 'func start' without '--no-build' to compile, or run 'cargo build' and copy the executable it builds to '{LocalBinaryPath}'.");
            }
        }

        /// <summary>
        /// Tells whether there is a binary at <paramref name="path"/>. A symbolic link that points nowhere exists as a file,
        /// but there is nothing behind it to run, or to pack.
        /// </summary>
        internal static bool BinaryExists(string path)
        {
            if (!FileSystemHelpers.FileExists(path))
            {
                return false;
            }

            try
            {
                var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
                return target == null || target.Exists;
            }
            catch (IOException)
            {
                // A link that loops, or that cannot be read, leads to no binary either.
                return false;
            }
        }

        /// <summary>
        /// Gets the worker binary ready for <c>func start</c>. With <c>--no-build</c> it only checks that the binary
        /// exists, because a prebuilt binary runs without a Rust toolchain, and asking cargo for its version can make
        /// rustup download the toolchain that a rust-toolchain.toml names. Otherwise it checks the toolchain and builds.
        /// <paramref name="getCargoVersion"/> and <paramref name="build"/> stand for the toolchain probe and the build,
        /// and only tests pass them.
        /// </summary>
        internal static async Task PrepareForStart(
            bool noBuild,
            string workingDirectory = null,
            Func<string, Task<WorkerLanguageVersionInfo>> getCargoVersion = null,
            Func<string, Task> build = null)
        {
            if (noBuild)
            {
                AssertBinaryExists(workingDirectory);
                return;
            }

            getCargoVersion ??= GetEnvironmentCargoVersion;
            build ??= BuildProject;

            AssertCargoVersion(await getCargoVersion(workingDirectory));
            await build(workingDirectory);
        }

        /// <summary>
        /// Reads the binaries of the package at <paramref name="manifestPath"/> from the JSON messages of
        /// <c>cargo build --message-format=json</c>. Libraries, dependencies, other packages and lines that
        /// are not JSON are skipped.
        /// </summary>
        internal static IReadOnlyList<CargoBinary> ReadBinaryArtifacts(IEnumerable<string> cargoMessages, string manifestPath)
        {
            return ReadBinaryArtifacts(cargoMessages, manifest => string.Equals(manifest, manifestPath, PathComparison));
        }

        /// <summary>
        /// Reads the binaries that the build made for packages other than the one at <paramref name="manifestPath"/>, which a
        /// workspace root that has no package of its own does. It tells that case from a package without a binary target.
        /// </summary>
        internal static IReadOnlyList<CargoBinary> ReadOtherBinaryArtifacts(IEnumerable<string> cargoMessages, string manifestPath)
        {
            return ReadBinaryArtifacts(cargoMessages, manifest => !string.Equals(manifest, manifestPath, PathComparison));
        }

        private static IReadOnlyList<CargoBinary> ReadBinaryArtifacts(IEnumerable<string> cargoMessages, Func<string, bool> isWanted)
        {
            var binaries = new List<CargoBinary>();
            foreach (var line in cargoMessages)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                {
                    continue;
                }

                JObject message;
                try
                {
                    message = JObject.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                var executable = message.Value<string>("executable");
                if (message.Value<string>("reason") != "compiler-artifact"
                    || string.IsNullOrEmpty(executable)
                    || !isWanted(message.Value<string>("manifest_path")))
                {
                    continue;
                }

                var target = message["target"] as JObject;
                if (target?["kind"] is not JArray kinds || !kinds.Any(kind => kind.Value<string>() == "bin"))
                {
                    continue;
                }

                if (!binaries.Any(binary => string.Equals(binary.Path, executable, PathComparison)))
                {
                    binaries.Add(new CargoBinary(target.Value<string>("name"), executable));
                }
            }

            return binaries;
        }

        /// <summary>
        /// Picks the worker binary: the only one, or the one named by <c>default-run</c> in the manifest.
        /// <paramref name="otherBinaries"/> holds the binaries that the build made for other packages. When the package has no
        /// binary but they exist, the manifest is the root of a workspace that has no package of its own, and the error says so.
        /// </summary>
        internal static string SelectBinary(IReadOnlyList<CargoBinary> binaries, string defaultRun, string manifestPath, IReadOnlyList<CargoBinary> otherBinaries = null)
        {
            if (binaries.Count == 0 && otherBinaries?.Count > 0)
            {
                throw new CliException(
                    $"cargo built no binary for '{manifestPath}'. It built binaries for other packages in the same build ({string.Join(", ", otherBinaries.Select(binary => binary.Name))}). " +
                    "Run func in the folder of the package that holds the worker.");
            }

            if (binaries.Count == 0)
            {
                throw new CliException(
                    $"cargo did not build a binary for '{manifestPath}'. The app needs a package with a binary target, such as src/main.rs.");
            }

            if (binaries.Count == 1)
            {
                return binaries[0].Path;
            }

            var named = string.IsNullOrEmpty(defaultRun) ? new List<CargoBinary>() : binaries.Where(binary => binary.Name == defaultRun).ToList();
            if (named.Count == 1)
            {
                return named[0].Path;
            }

            throw new CliException(
                $"The package in '{manifestPath}' builds more than one binary ({string.Join(", ", binaries.Select(binary => binary.Name))}). " +
                "Set 'default-run' in the [package] section of Cargo.toml to the worker binary, or keep a single binary target.");
        }

        /// <summary>
        /// Reads <c>default_run</c> of the package at <paramref name="manifestPath"/> from the output of
        /// <c>cargo metadata</c>. Returns <c>null</c> when it is not set or the output cannot be used.
        /// </summary>
        internal static string ReadDefaultRun(string cargoMetadataJson, string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(cargoMetadataJson))
            {
                return null;
            }

            try
            {
                var packages = JObject.Parse(cargoMetadataJson)["packages"] as JArray;
                var package = packages?.FirstOrDefault(p => string.Equals(p.Value<string>("manifest_path"), manifestPath, PathComparison));
                return package?.Value<string>("default_run");
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Builds the user's package in the release profile for <see cref="LinuxTarget"/> and copies the binary
        /// to <c>bin/app</c> in <paramref name="workingDirectory"/>, the Linux binary that <c>func pack</c> puts
        /// in the zip. The path comes from cargo, as in <see cref="BuildProject"/>. Only x86_64 Linux is built.
        /// A build from another operating system needs the target and a linker for it, which Core Tools does
        /// not set up. When the build fails for that reason it says so in a hint.
        /// <paramref name="getCargoVersion"/> stands for the toolchain probe, and only tests pass it.
        /// </summary>
        internal static async Task BuildForLinux(string workingDirectory = null, Func<string, Task<WorkerLanguageVersionInfo>> getCargoVersion = null)
        {
            workingDirectory ??= Environment.CurrentDirectory;
            getCargoVersion ??= GetEnvironmentCargoVersion;
            AssertCargoVersion(await getCargoVersion(workingDirectory));

            var outputPath = Path.Combine(NativeWorkerHelpers.BinDirectoryName, NativeWorkerHelpers.BinaryName);
            ColoredConsole.WriteLine($"Building Rust worker binary '{outputPath}' for {LinuxTarget}...");

            await BuildAndCopyAsync(
                workingDirectory,
                $"--release --target {LinuxTarget}",
                outputPath,
                (exitCode, stderr) =>
                {
                    if (LooksLikeMissingLinuxToolchain(stderr))
                    {
                        ColoredConsole.WriteLine(AdditionalInfoColor(LinuxToolchainHint));
                    }

                    return new CliException($"Rust build for {LinuxTarget} failed with exit code {exitCode}. See output above for details.");
                });
        }

        /// <summary>
        /// Ensures the binary in <c>bin/app</c> is an x86_64 Linux executable. Called by
        /// <c>func pack --no-build</c> to confirm the binary was built for Linux before packaging. A binary that is valid but has
        /// debug information gets a warning, because a debug build must not be packed by accident.
        /// </summary>
        internal static void AssertLinuxAmd64Binary(string workingDirectory = null)
        {
            NativeWorkerHelpers.AssertLinuxAmd64Binary(
                workingDirectory,
                "Rust",
                "Core Tools packs Rust apps for x86_64 Linux only.",
                $"'cargo build --release --target {LinuxTarget}' and copy the executable it builds to '{PackedBinaryDisplayPath}'",
                BinaryExists);

            var binaryPath = Path.Combine(workingDirectory ?? Environment.CurrentDirectory, NativeWorkerHelpers.BinDirectoryName, NativeWorkerHelpers.BinaryName);
            if (HasDebugInformation(binaryPath))
            {
                ColoredConsole.WriteLine(WarningColor(DebugInformationWarning));
            }
        }

        /// <summary>
        /// Tells whether the ELF file at <paramref name="binaryPath"/> has a <c>.debug_info</c> section, which a debug build has and a
        /// release build normally does not. It reads only the names of the sections, and not the bytes of the file, because the standard
        /// library keeps the text <c>.debug_info</c> in the data of every release binary that can print a backtrace. This is advice and
        /// must not fail a pack, so a file that is missing, cannot be read or is not what an ELF file should be gives <c>false</c>.
        /// </summary>
        internal static bool HasDebugInformation(string binaryPath)
        {
            try
            {
                using var file = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return HasDebugInformation(file);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool HasDebugInformation(Stream elfFile)
        {
            return ElfSections.HasSection(elfFile, DebugInformationSectionName);
        }

        /// <summary>
        /// Returns the explicit allowlist of files in the deployment zip: <c>host.json</c> when it
        /// exists, and the Linux binary.
        /// </summary>
        internal static IEnumerable<string> GetPackFiles(string functionAppRoot)
        {
            return NativeWorkerHelpers.GetPackFiles(functionAppRoot);
        }

        /// <summary>
        /// Verifies the function app root has the files required to pack a Rust app. <c>Cargo.toml</c> is
        /// required and <c>host.json</c> is optional. Routes through <see cref="PackValidationHelper"/> so
        /// the Rust pack flow shares the validation flow and messages of the other runtimes.
        /// </summary>
        internal static void AssertRustFunctionAppLayout(string functionAppRoot)
        {
            functionAppRoot ??= Environment.CurrentDirectory;

            var validations = new List<Action<string>>
            {
                dir => PackValidationHelper.RunRequiredFilesValidation(dir, new[] { CargoTomlFileName }, "Validate Cargo.toml"),
            };
            PackValidationHelper.RunValidations(functionAppRoot, validations);
        }

        /// <summary>
        /// Tells whether the error output of a failed build for <see cref="LinuxTarget"/> says that the target
        /// or a linker for it is missing, which is when the toolchain hint helps.
        /// </summary>
        internal static bool LooksLikeMissingLinuxToolchain(string stderr)
        {
            if (string.IsNullOrEmpty(stderr))
            {
                return false;
            }

            return stderr.Contains("target may not be installed", StringComparison.Ordinal)
                || stderr.Contains("rustup target add", StringComparison.Ordinal)
                || _missingLinkerPattern.IsMatch(stderr);
        }

        /// <summary>
        /// Prepares the Linux binary that <c>func publish</c> uploads: checks the project layout, then either
        /// verifies the pre-built binary (<c>--no-build</c>) or builds it for Linux. The same steps as
        /// <c>func pack</c>.
        /// </summary>
        internal static async Task PrepareForPublish(string functionAppRoot, bool noBuild)
        {
            AssertRustFunctionAppLayout(functionAppRoot);

            if (noBuild)
            {
                // Trust the user's pre-built binary, but verify it is an x86_64 Linux ELF so a host OS
                // binary is not uploaded to a Linux host.
                AssertLinuxAmd64Binary(functionAppRoot);
            }
            else
            {
                await BuildForLinux(functionAppRoot);
            }
        }

        /// <summary>
        /// Copies the built executable to <paramref name="destination"/>, which is <c>bin/app</c> for the package or
        /// <c>target/func/app</c> for <c>func start</c>, through a
        /// temporary file in the same folder that is then moved over the destination. A copy that fails half way, for example
        /// on a full disk, leaves the old binary or none, and never half of a binary, which <c>func pack --no-build</c> would
        /// pack because the header is valid. The move also puts a new file in place of the old one and does not rewrite it.
        /// On Apple silicon the kernel keeps the code signature that it checked for a file, so a binary that is rewritten in
        /// place after it ran can be killed the next time it starts. That is unverified here, no Mac was available.
        /// A move works on Linux and macOS while a worker runs from the old file. Windows refuses to replace the file of a
        /// running program, and then the error says that a worker may still run.
        /// <paramref name="copyFile"/> stands for the copy of the bytes, and only tests pass it.
        /// </summary>
        internal static void CopyBinary(string builtExecutable, string destination, Action<string, string> copyFile = null)
        {
            copyFile ??= (source, target) => File.Copy(source, target);
            var folder = Path.GetDirectoryName(Path.GetFullPath(destination));
            var failure = $"Could not copy the built binary '{builtExecutable}' to '{destination}'.";

            if (Directory.Exists(destination))
            {
                throw new CliException($"{failure} '{destination}' is a folder. Remove or rename it.");
            }

            if (File.Exists(folder))
            {
                throw new CliException($"{failure} '{folder}' is a file, but the binary goes into a folder of that name. Remove or rename it.");
            }

            // Windows does not replace a read-only file, and a worker is not the reason.
            if (OperatingSystem.IsWindows() && File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReadOnly))
            {
                throw new CliException($"{failure} '{destination}' is read-only. Clear its read-only attribute or delete it, then try again.");
            }

            var temporary = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            var replacing = false;

            try
            {
                Directory.CreateDirectory(folder);
                copyFile(builtExecutable, temporary);

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(temporary) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }

                replacing = true;
                File.Move(temporary, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var stopTheWorker = replacing && OperatingSystem.IsWindows() && IsHeldByAnotherProgram(ex);
                throw new CliException($"{failure}{(stopTheWorker ? " Stop any running worker first." : string.Empty)} {ex.Message}");
            }
            finally
            {
                DeleteQuietly(temporary);
            }
        }

        // Windows answers with an access denied error, or with a sharing or lock violation (the low word of the HRESULT is 32 or 33),
        // when a program runs from the file or has it open.
        private static bool IsHeldByAnotherProgram(Exception exception)
        {
            return exception is UnauthorizedAccessException || (exception is IOException && (exception.HResult & 0xFFFF) is 32 or 33);
        }

        // Removes a file that is left over after a failure. The error of the copy is the one to show, so a leftover that stays is not.
        private static void DeleteQuietly(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done about it.
            }
        }

        // The embedded resources are joined line by line and carry no final newline, which rustfmt rejects.
        private static string EndWithNewLine(string content)
        {
            return content.EndsWith('\n') ? content : content + Environment.NewLine;
        }

        // Appends a commented rule to a .gitignore, after a blank line, and keeps its line ending style.
        private static string AppendGitIgnoreRule(string gitIgnoreContent, string comment, string rule)
        {
            var newLine = gitIgnoreContent.Contains("\r\n") ? "\r\n" : "\n";
            var content = new StringBuilder(gitIgnoreContent);
            if (content.Length > 0)
            {
                if (!gitIgnoreContent.EndsWith('\n'))
                {
                    content.Append(newLine);
                }

                content.Append(newLine);
            }

            return content
                .Append(comment).Append(newLine)
                .Append(rule).Append(newLine)
                .ToString();
        }

        private static async Task BuildAndCopyAsync(string workingDirectory, string buildArguments, string destination, Func<int, string, CliException> createBuildFailure)
        {
            // Cargo prints the path of the manifest it built in every artifact message, in its own form
            // (for example a resolved symlink), so ask cargo for the manifest of this folder to compare.
            var manifestPath = (await RunCargoAsync(workingDirectory, "locate-project --message-format plain"))?.Trim()
                ?? Path.GetFullPath(Path.Combine(workingDirectory, CargoTomlFileName));

            var messages = new List<string>();
            var stderr = new StringBuilder();
            var exe = new Executable(CargoExecutable, $"build --message-format=json-render-diagnostics {buildArguments}".TrimEnd(), workingDirectory: workingDirectory);

            // The JSON messages are on stdout. Cargo reports its progress and the compiler's diagnostics on stderr.
            var exitCode = await exe.RunAsync(
                line =>
                {
                    if (line != null)
                    {
                        messages.Add(line);
                    }
                },
                line =>
                {
                    if (line != null)
                    {
                        stderr.AppendLine(line);
                        ColoredConsole.Error.WriteLine(line);
                    }
                });

            if (exitCode != 0)
            {
                throw createBuildFailure(exitCode, stderr.ToString());
            }

            var binaries = ReadBinaryArtifacts(messages, manifestPath);
            var otherBinaries = binaries.Count == 0 ? ReadOtherBinaryArtifacts(messages, manifestPath) : null;
            var defaultRun = binaries.Count > 1
                ? ReadDefaultRun(await RunCargoAsync(workingDirectory, "metadata --no-deps --format-version 1"), manifestPath)
                : null;

            CopyBinary(SelectBinary(binaries, defaultRun, manifestPath, otherBinaries), Path.Combine(workingDirectory, destination));
        }

        // Runs cargo and returns its stdout, or null when it fails. Used for the small queries around a build.
        private static async Task<string> RunCargoAsync(string workingDirectory, string arguments)
        {
            var output = new StringBuilder();
            var exe = new Executable(CargoExecutable, arguments, workingDirectory: workingDirectory);
            var exitCode = await exe.RunAsync(
                line =>
                {
                    if (line != null)
                    {
                        output.AppendLine(line);
                    }
                },
                _ => { });

            return exitCode == 0 ? output.ToString() : null;
        }

        internal readonly record struct CargoBinary(string Name, string Path);
    }
}
