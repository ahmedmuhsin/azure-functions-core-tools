// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using System.Text.RegularExpressions;
using Azure.Functions.Cli.Actions.LocalActions.PackAction;
using Azure.Functions.Cli.Common;
using Colors.Net;
using static Azure.Functions.Cli.Common.OutputTheme;

namespace Azure.Functions.Cli.Helpers
{
    internal static class GoHelpers
    {
        private const int MinimumGoMajorVersion = 1;
        private const int MinimumGoMinorVersion = 24;
        internal const string GoBinaryName = NativeWorkerHelpers.BinaryName;
        internal const string GoBinDir = NativeWorkerHelpers.BinDirectoryName;
        internal const string GoModFileName = "go.mod";

        private static readonly Regex _goVersionPattern = new Regex(@"go(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

        internal static string LocalBinaryPath => NativeWorkerHelpers.LocalBinaryPath;

        public static async Task<WorkerLanguageVersionInfo> GetEnvironmentGoVersion()
        {
            // Parses the "go version go1.24.2 linux/amd64" format.
            return await NativeWorkerHelpers.GetToolVersion(WorkerRuntime.Go, "Go", "go", "version", _goVersionPattern);
        }

        public static void AssertGoVersion(WorkerLanguageVersionInfo goVersion)
        {
            if (goVersion?.Version == null)
            {
                throw new CliException(
                    $"Could not find a Go installation. Go {MinimumGoMajorVersion}.{MinimumGoMinorVersion} or later is required. " +
                    "Please install Go from https://go.dev/dl/");
            }

            if (GlobalCoreToolsSettings.IsVerbose)
            {
                ColoredConsole.WriteLine(VerboseColor($"Found Go version {goVersion.Version} ({goVersion.ExecutablePath})."));
            }

            if (goVersion.Major == null || goVersion.Minor == null)
            {
                throw new CliException(
                    $"Unable to parse Go version '{goVersion.Version}'. " +
                    $"Go {MinimumGoMajorVersion}.{MinimumGoMinorVersion} or later is required.");
            }

            // Accept any major version > 1, or major == 1 with minor >= 24
            if (goVersion.Major > MinimumGoMajorVersion)
            {
                return;
            }

            if (goVersion.Major == MinimumGoMajorVersion && goVersion.Minor >= MinimumGoMinorVersion)
            {
                return;
            }

            throw new CliException(
                $"Go version {goVersion.Version} is not supported. " +
                $"Go {MinimumGoMajorVersion}.{MinimumGoMinorVersion} or later is required. " +
                "Please update Go from https://go.dev/dl/");
        }

        public static async Task SetupProject(string moduleName, bool skipGoModTidy)
        {
            var goVersion = await GetEnvironmentGoVersion();
            AssertGoVersion(goVersion);

            // Scaffold go.mod, main.go, and .funcignore from embedded templates.
            // go.mod intentionally omits a require line for azure-functions-golang-worker —
            // `go mod tidy` resolves the latest available tag from the imports in main.go,
            // so users always pick up the newest SDK without contributors having to bump
            // a pinned version here.
            var goModContent = (await StaticResources.GoMod).Replace("{ModuleName}", moduleName);
            await FileSystemHelpers.WriteFileIfNotExists("go.mod", goModContent);
            await FileSystemHelpers.WriteFileIfNotExists("main.go", await StaticResources.MainGo);
            await FileSystemHelpers.WriteFileIfNotExists(Constants.FuncIgnoreFile, await StaticResources.FuncIgnore);

            if (!skipGoModTidy)
            {
                await RunGoCommandAsync("mod tidy", null, throwOnFailure: false);
            }
            else
            {
                ColoredConsole.WriteLine(AdditionalInfoColor("Skipped \"go mod tidy\". You must run \"go mod tidy\" manually."));
            }
        }

        /// <summary>
        /// Compiles the user's Go project into a binary named <see cref="GoBinaryName"/>
        /// under <c><see cref="GoBinDir"/>/</c> in <paramref name="workingDirectory"/>.
        /// The host resolves this binary via <c>defaultExecutablePath</c> in
        /// <c>workers/native/worker.config.json</c>. Core Tools sets the Windows
        /// default executable path to match the <c>.exe</c> build output.
        /// </summary>
        public static async Task BuildProject(string workingDirectory = null)
        {
            workingDirectory ??= Environment.CurrentDirectory;
            var outputPath = LocalBinaryPath;

            ColoredConsole.WriteLine($"Building Go worker binary '{outputPath}'...");

            // Ensure bin/ exists so `go build -o bin/app` doesn't fail.
            Directory.CreateDirectory(Path.Combine(workingDirectory, GoBinDir));

            var stderrBuffer = new StringBuilder();
            var exe = new Executable("go", $"build -o \"{outputPath}\" .", workingDirectory: workingDirectory);
            var exitCode = await exe.RunAsync(
                l => ColoredConsole.WriteLine(l),
                e =>
                {
                    stderrBuffer.AppendLine(e);
                    ColoredConsole.Error.WriteLine(ErrorColor(e));
                });

            if (exitCode != 0)
            {
                if (LooksLikeStaleModules(stderrBuffer.ToString()))
                {
                    ColoredConsole.WriteLine(AdditionalInfoColor("Hint: run \"go mod tidy\" to update module dependencies, then re-run \"func start\"."));
                }

                throw new CliException($"Go build failed with exit code {exitCode}. See output above for details.");
            }
        }

        private static bool LooksLikeStaleModules(string stderr)
        {
            if (string.IsNullOrEmpty(stderr))
            {
                return false;
            }

            return stderr.Contains("missing go.sum entry", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("no required module provides package", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("inconsistent vendoring", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that the compiled Go worker binary exists in
        /// <paramref name="workingDirectory"/>. Throws a <see cref="CliException"/>
        /// with an actionable message when missing — used by <c>func start --no-build</c>.
        /// </summary>
        public static void AssertBinaryExists(string workingDirectory = null)
        {
            workingDirectory ??= Environment.CurrentDirectory;
            var binaryPath = Path.Combine(workingDirectory, LocalBinaryPath);

            if (!FileSystemHelpers.FileExists(binaryPath))
            {
                throw new CliException(
                    $"Could not find a built Go binary '{LocalBinaryPath}' in '{workingDirectory}'. " +
                    $"Run 'func start' without '--no-build' to compile, or run 'go build -o {LocalBinaryPath} .' manually.");
            }
        }

        /// <summary>
        /// Cross-compiles the user's Go project into a linux/amd64 binary named
        /// <see cref="GoBinaryName"/> in <paramref name="workingDirectory"/>. Used
        /// by <c>func pack</c> and <c>func publish</c> — the produced binary is the
        /// one that runs on the Linux Functions host, regardless of the dev OS.
        /// Targets linux/amd64 only.
        /// </summary>
        internal static async Task BuildForLinux(string workingDirectory = null)
        {
            workingDirectory ??= Environment.CurrentDirectory;

            var goVersion = await GetEnvironmentGoVersion();
            AssertGoVersion(goVersion);

            var outputPath = Path.Combine(GoBinDir, GoBinaryName);
            ColoredConsole.WriteLine($"Building Go worker binary '{outputPath}' for linux/amd64...");

            // Ensure bin/ exists so `go build -o bin/app` doesn't fail.
            Directory.CreateDirectory(Path.Combine(workingDirectory, GoBinDir));

            var env = new Dictionary<string, string>
            {
                ["CGO_ENABLED"] = "0",
                ["GOOS"] = "linux",
                ["GOARCH"] = "amd64",
            };

            var exe = new Executable("go", $"build -o \"{outputPath}\" .", workingDirectory: workingDirectory, environmentVariables: env);

            // Stream stderr inline (red) and let the thrown exception summarize on failure
            // rather than re-printing the captured output, so users don't see compiler errors twice.
            var exitCode = await exe.RunAsync(
                l => ColoredConsole.WriteLine(l),
                e => ColoredConsole.Error.WriteLine(ErrorColor(e)));

            if (exitCode != 0)
            {
                throw new CliException(
                    $"Go build for linux/amd64 failed with exit code {exitCode}. See output above for details.");
            }
        }

        /// <summary>
        /// Ensures the Go binary in <paramref name="workingDirectory"/> is a valid
        /// Linux x86_64 executable suitable for the Azure Functions Linux host.
        /// Validates the binary's structure by checking its ELF header format.
        /// Called by <c>func pack --no-build</c> to confirm the binary was properly
        /// cross-compiled before packaging for deployment.
        /// </summary>
        internal static void AssertLinuxAmd64Binary(string workingDirectory = null)
        {
            var displayPath = Path.Combine(GoBinDir, GoBinaryName);
            NativeWorkerHelpers.AssertLinuxAmd64Binary(
                workingDirectory,
                "Go",
                "Core Tools currently only supports linux/amd64 Go targets.",
                $"'CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -o {displayPath} .'");
        }

        /// <summary>
        /// Returns the explicit allowlist of files that should be included in the
        /// Go deployment zip. Centralizes the list so that <see cref="ZipHelper"/>
        /// (zip composition) and <c>func publish --list-included-files</c> stay in sync.
        /// </summary>
        internal static IEnumerable<string> GetPackFiles(string functionAppRoot)
        {
            return NativeWorkerHelpers.GetPackFiles(functionAppRoot);
        }

        /// <summary>
        /// Verifies the function app root has the files required to build/publish a Go app.
        /// <c>go.mod</c> is required; <c>host.json</c> is optional (a warning is emitted when it
        /// is absent). Routes through <see cref="PackValidationHelper"/> so <c>func pack</c> and
        /// <c>func publish</c> share the same validation flow and messages.
        /// Throws <see cref="CliException"/> on failure.
        /// </summary>
        internal static void AssertGoFunctionAppLayout(string functionAppRoot)
        {
            functionAppRoot ??= Environment.CurrentDirectory;

            var validations = new List<Action<string>>
            {
                dir => PackValidationHelper.RunRequiredFilesValidation(dir, new[] { GoModFileName }, "Validate go.mod"),
            };
            PackValidationHelper.RunValidations(functionAppRoot, validations);
        }

        private static async Task RunGoCommandAsync(string arguments, string errorMessage, bool throwOnFailure = true)
        {
            var exe = new Executable("go", arguments);
            var stderr = new StringBuilder();
            var exitCode = await exe.RunAsync(
                l =>
                {
                    if (GlobalCoreToolsSettings.IsVerbose)
                    {
                        ColoredConsole.WriteLine(VerboseColor(l));
                    }
                },
                e =>
                {
                    stderr.AppendLine(e);
                    if (GlobalCoreToolsSettings.IsVerbose)
                    {
                        ColoredConsole.WriteLine(VerboseColor(e));
                    }
                });

            if (exitCode != 0)
            {
                var stderrOutput = stderr.ToString().Trim();
                if (throwOnFailure)
                {
                    var detail = string.IsNullOrEmpty(stderrOutput) ? string.Empty : $" {stderrOutput}";
                    throw new CliException($"{errorMessage}{detail}");
                }

                ColoredConsole.WriteLine(WarningColor($"Warning: 'go {arguments}' exited with a non-zero code. You may need to run it manually."));
                if (!string.IsNullOrEmpty(stderrOutput))
                {
                    ColoredConsole.WriteLine(WarningColor(stderrOutput));
                }
            }
        }
    }
}
