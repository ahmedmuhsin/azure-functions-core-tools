// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Interfaces;
using Colors.Net;
using static Azure.Functions.Cli.Common.OutputTheme;

namespace Azure.Functions.Cli.Helpers
{
    public enum WorkerRuntime
    {
        [DisplayString("none")]
        None,
        [DisplayString("dotnet")]
        Dotnet,
        [DisplayString("dotnet-isolated")]
        DotnetIsolated,
        [DisplayString("node")]
        Node,
        [DisplayString("python")]
        Python,
        [DisplayString("java")]
        Java,
        [DisplayString("powershell")]
        Powershell,
        [DisplayString("custom")]
        Custom,
        [DisplayString("go")]
        Go,
        [DisplayString("rust")]
        Rust
    }

    public static class WorkerRuntimeLanguageHelper
    {
        private static readonly IDictionary<WorkerRuntime, IEnumerable<string>> _availableWorkersRuntime = new Dictionary<WorkerRuntime, IEnumerable<string>>
        {
            { WorkerRuntime.DotnetIsolated, new[] { "dotnet-isolated", "c#-isolated", "csharp-isolated", "f#-isolated", "fsharp-isolated" } },
            { WorkerRuntime.Dotnet, new[] { "c#", "csharp", "f#", "fsharp" } },
            { WorkerRuntime.Node, new[] { "js", "javascript", "typescript", "ts" } },
            { WorkerRuntime.Python, new[] { "py" } },
            { WorkerRuntime.Go, new[] { "golang" } },
            { WorkerRuntime.Rust, new string[] { } },
            { WorkerRuntime.Java, new string[] { } },
            { WorkerRuntime.Powershell, new[] { "pwsh" } },
            { WorkerRuntime.Custom, new string[] { } },
        };

        private static readonly IDictionary<string, WorkerRuntime> _normalizeMap = _availableWorkersRuntime
            .SelectMany(p => p.Value.Select(v => new { key = v, value = p.Key }).Append(new { key = p.Key.ToString(), value = p.Key }))
            .ToDictionary(k => k.key, v => v.value, StringComparer.OrdinalIgnoreCase);

        private static readonly IDictionary<WorkerRuntime, string> _workerToDefaultLanguageMap = new Dictionary<WorkerRuntime, string>
        {
            { WorkerRuntime.Dotnet, Constants.Languages.CSharp },
            { WorkerRuntime.DotnetIsolated, Constants.Languages.CSharp },
            { WorkerRuntime.Node, Constants.Languages.JavaScript },
            { WorkerRuntime.Python, Constants.Languages.Python },
            { WorkerRuntime.Powershell, Constants.Languages.Powershell },
            { WorkerRuntime.Custom, Constants.Languages.Custom },
        };

        private static readonly IDictionary<string, IEnumerable<string>> _languageToAlias = new Dictionary<string, IEnumerable<string>>
        {
            // By default node should map to javascript
            { Constants.Languages.JavaScript, new[] { "js", "node" } },
            { Constants.Languages.TypeScript, new[] { "ts" } },
            { Constants.Languages.Python, new[] { "py" } },
            { Constants.Languages.Powershell, new[] { "pwsh" } },

            // By default dotnet should map to csharp
            { Constants.Languages.CSharp, new[] { "csharp", "dotnet", "dotnet-isolated", "dotnetIsolated" } },
            { Constants.Languages.FSharp, new[] { "fsharp" } },
            { Constants.Languages.Java, new string[] { } },
            { Constants.Languages.Custom, new string[] { } },
        };

        public static readonly IDictionary<string, string> WorkerRuntimeStringToLanguage = _languageToAlias
            .Select(p => p.Value.Select(v => new { key = v, value = p.Key }).Append(new { key = p.Key.ToString(), value = p.Key }))
            .SelectMany(i => i)
            .ToDictionary(k => k.key, v => v.value, StringComparer.OrdinalIgnoreCase);

        public static readonly IDictionary<WorkerRuntime, IEnumerable<string>> WorkerToSupportedLanguages = new Dictionary<WorkerRuntime, IEnumerable<string>>
        {
            { WorkerRuntime.Node, new[] { Constants.Languages.JavaScript, Constants.Languages.TypeScript } },
            { WorkerRuntime.Dotnet, new[] { Constants.Languages.CSharp, Constants.Languages.FSharp } },
            { WorkerRuntime.DotnetIsolated, new[] { Constants.Languages.CSharp, Constants.Languages.FSharp } },
        };

        public static IEnumerable<WorkerRuntime> AvailableWorkersList => _availableWorkersRuntime.Keys
            .Where(k => k != WorkerRuntime.Java);

        public static string AvailableWorkersRuntimeString =>
            string.Join(", ", _availableWorkersRuntime.Keys
                .Where(k => k != WorkerRuntime.Java)
                .Select(s => GetRuntimeMoniker(s)));

        public static string GetRuntimeMoniker(WorkerRuntime workerRuntime)
        {
            switch (workerRuntime)
            {
                case WorkerRuntime.None:
                    return "None";
                case WorkerRuntime.Dotnet:
                    return "dotnet";
                case WorkerRuntime.DotnetIsolated:
                    return "dotnet-isolated";
                case WorkerRuntime.Node:
                    return "node";
                case WorkerRuntime.Python:
                    return "python";
                case WorkerRuntime.Java:
                    return "java";
                case WorkerRuntime.Powershell:
                    return "powershell";
                case WorkerRuntime.Custom:
                    return "custom";
                case WorkerRuntime.Go:
                    return "go";
                case WorkerRuntime.Rust:
                    return "rust";
                default:
                    return "None";
            }
        }

        public static IDictionary<WorkerRuntime, string> GetWorkerToDisplayStrings()
        {
            IDictionary<WorkerRuntime, string> workerToDisplayStrings = new Dictionary<WorkerRuntime, string>();
            foreach (WorkerRuntime wr in AvailableWorkersList)
            {
                switch (wr)
                {
                    case WorkerRuntime.Dotnet:
                        workerToDisplayStrings[wr] = "dotnet (in-process model)";
                        break;
                    case WorkerRuntime.DotnetIsolated:
                        workerToDisplayStrings[wr] = "dotnet (isolated worker model)";
                        break;
                    default:
                        workerToDisplayStrings[wr] = wr.ToString();
                        break;
                }
            }

            return workerToDisplayStrings;
        }

        public static WorkerRuntime NormalizeWorkerRuntime(string workerRuntime)
        {
            if (string.IsNullOrWhiteSpace(workerRuntime))
            {
                throw new ArgumentNullException(nameof(workerRuntime), "Worker runtime cannot be null or empty.");
            }
            else if (_normalizeMap.ContainsKey(workerRuntime))
            {
                return _normalizeMap[workerRuntime];
            }
            else
            {
                throw new ArgumentException($"Worker runtime '{workerRuntime}' is not a valid option. Options are {AvailableWorkersRuntimeString}");
            }
        }

        public static string NormalizeLanguage(string languageString)
        {
            if (string.IsNullOrWhiteSpace(languageString))
            {
                throw new ArgumentNullException(nameof(languageString), "language can't be empty");
            }
            else if (WorkerRuntimeStringToLanguage.ContainsKey(languageString))
            {
                return WorkerRuntimeStringToLanguage[languageString];
            }
            else
            {
                throw new ArgumentException($"Language '{languageString}' is not available. Available language strings are {WorkerRuntimeStringToLanguage.Keys}");
            }
        }

        public static bool TryNormalizeLanguage(string languageString, out string normalized)
        {
            if (!string.IsNullOrWhiteSpace(languageString) && WorkerRuntimeStringToLanguage.ContainsKey(languageString))
            {
                normalized = WorkerRuntimeStringToLanguage[languageString];
                return true;
            }

            normalized = null;
            return false;
        }

        public static IEnumerable<string> LanguagesForWorker(WorkerRuntime worker)
        {
            return _normalizeMap.Where(p => p.Value == worker).Select(p => p.Key);
        }

        public static WorkerRuntime GetCurrentWorkerRuntimeLanguage(ISecretsManager secretsManager, bool refreshSecrets = false)
        {
            // FUNCTIONS_CLI_NATIVE_LANGUAGE is an optional override that lets a project declare its native language
            // without the resolver scanning the working directory for go.mod or Cargo.toml. Env var
            // wins; local.settings.json is the secondary source. See TryResolveNativeLanguage.
            if (TryResolveNativeLanguage(secretsManager, refreshSecrets, out var nativeRuntime))
            {
                if (GlobalCoreToolsSettings.IsVerbose)
                {
                    ColoredConsole.WriteLine(VerboseColor($"Resolving worker runtime to '{GetRuntimeMoniker(nativeRuntime)}' ({Constants.FunctionsCliNativeLanguage} is set)."));
                }

                return nativeRuntime;
            }

            string setting = Environment.GetEnvironmentVariable(Constants.FunctionsWorkerRuntime);

            if (string.IsNullOrWhiteSpace(setting))
            {
                // When FUNCTIONS_WORKER_RUNTIME isn't in the environment, check local.settings.json.
                // If secrets cannot be loaded (e.g. 'func pack' invoked from a parent directory
                // before cwd is changed), the GetSecrets exceptions will propagate to the caller.
                // Callers that want a silent None on missing secrets should catch CliException.
                setting = secretsManager?.GetSecrets(refreshSecrets)?.FirstOrDefault(s => s.Key.Equals(Constants.FunctionsWorkerRuntime, StringComparison.OrdinalIgnoreCase)).Value;
            }

            // The Functions host registers some workers under the literal "native" language
            // identifier (see workers/native/worker.config.json). Map "native" back to a
            // concrete WorkerRuntime using well-known project markers in the current directory.
            // Throws CliException with an actionable message when the setting is "native" but
            // no supported marker is found — callers that want a silent None should catch.
            if (string.Equals(setting, "native", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveNativeFromProjectMarkers();
            }

            try
            {
                WorkerRuntime workerRuntime = NormalizeWorkerRuntime(setting);
                if (GlobalCoreToolsSettings.IsVerbose)
                {
                    ColoredConsole.WriteLine(VerboseColor($"Resolving worker runtime to '{GetRuntimeMoniker(workerRuntime)}'."));
                }

                return workerRuntime;
            }
            catch
            {
                return WorkerRuntime.None;
            }
        }

        internal static WorkerRuntime SetWorkerRuntime(ISecretsManager secretsManager, string language)
        {
            var workerRuntime = NormalizeWorkerRuntime(language);
            var runtimeMoniker = GetRuntimeMoniker(workerRuntime);

            secretsManager.SetSecret(Constants.FunctionsWorkerRuntime, runtimeMoniker);

            ColoredConsole
                .WriteLine(WarningColor("Starting from 2.0.1-beta.26 it's required to set a language for your project in your settings."))
                .WriteLine(WarningColor($"Worker runtime '{runtimeMoniker}' has been set in '{SecretsManager.AppSettingsFilePath}'."));

            return workerRuntime;
        }

        /// <summary>
        /// Maps <c>FUNCTIONS_WORKER_RUNTIME=native</c> to a concrete <see cref="WorkerRuntime"/>
        /// by inspecting well-known project-marker files in the current directory. Throws
        /// <see cref="CliException"/> when no supported marker is found.
        /// </summary>
        /// <remarks>
        /// Marker -> runtime:
        /// <list type="bullet">
        ///   <item><c>go.mod</c> -> <see cref="WorkerRuntime.Go"/></item>
        ///   <item><c>Cargo.toml</c> -> <see cref="WorkerRuntime.Rust"/></item>
        /// </list>
        /// <c>go.mod</c> is checked first, so an existing Go app that also holds a Rust crate keeps
        /// resolving to Go. Add new native languages here as they are introduced. Invoked exclusively from
        /// <see cref="GetCurrentWorkerRuntimeLanguage"/> so callers never have to special-case
        /// the "native" literal. An app needs only FUNCTIONS_WORKER_RUNTIME=native and its marker file. The optional
        /// <see cref="Constants.FunctionsCliNativeLanguage"/> override is checked earlier in the resolver.
        /// </remarks>
        private static WorkerRuntime ResolveNativeFromProjectMarkers()
        {
            if (FileSystemHelpers.FileExists(Path.Combine(Environment.CurrentDirectory, GoHelpers.GoModFileName)))
            {
                if (GlobalCoreToolsSettings.IsVerbose)
                {
                    ColoredConsole.WriteLine(VerboseColor($"Resolving native worker runtime to 'go' ({GoHelpers.GoModFileName} found; consider setting {Constants.FunctionsCliNativeLanguage}=go in local.settings.json)."));
                }

                return WorkerRuntime.Go;
            }

            if (FileSystemHelpers.FileExists(Path.Combine(Environment.CurrentDirectory, RustHelpers.CargoTomlFileName)))
            {
                if (GlobalCoreToolsSettings.IsVerbose)
                {
                    ColoredConsole.WriteLine(VerboseColor($"Resolving native worker runtime to 'rust' ({RustHelpers.CargoTomlFileName} found)."));
                }

                return WorkerRuntime.Rust;
            }

            throw new CliException(
                $"FUNCTIONS_WORKER_RUNTIME is set to 'native' but no supported project marker ({GoHelpers.GoModFileName} or {RustHelpers.CargoTomlFileName}) was found in '{Environment.CurrentDirectory}'. " +
                "Run the command in the folder of the app, or run 'func init' to initialize a supported function app in this directory.");
        }

        /// <summary>
        /// Returns <c>true</c> when FUNCTIONS_CLI_NATIVE_LANGUAGE names a supported native language
        /// ("go" or "rust"), either in the process environment or in <c>local.settings.json</c>.
        /// It is an optional override for a project that sets it by hand, for example a folder that holds both
        /// <c>go.mod</c> and <c>Cargo.toml</c>.
        /// Env var wins; an unrecognized env value does not hide a recognized setting.
        /// local.settings.json access failures (e.g. command run from outside a project root)
        /// are treated as "not set".
        /// </summary>
        private static bool TryResolveNativeLanguage(ISecretsManager secretsManager, bool refreshSecrets, out WorkerRuntime nativeRuntime)
        {
            if (TryParseNativeLanguage(Environment.GetEnvironmentVariable(Constants.FunctionsCliNativeLanguage), out nativeRuntime))
            {
                return true;
            }

            if (secretsManager is null)
            {
                return false;
            }

            string fromSettings;
            try
            {
                fromSettings = secretsManager.GetSecrets(refreshSecrets)?.FirstOrDefault(s => s.Key.Equals(Constants.FunctionsCliNativeLanguage, StringComparison.OrdinalIgnoreCase)).Value;
            }
            catch (CliException)
            {
                return false;
            }

            return TryParseNativeLanguage(fromSettings, out nativeRuntime);
        }

        private static bool TryParseNativeLanguage(string value, out WorkerRuntime nativeRuntime)
        {
            if (string.Equals(value, "go", StringComparison.OrdinalIgnoreCase))
            {
                nativeRuntime = WorkerRuntime.Go;
                return true;
            }

            if (string.Equals(value, "rust", StringComparison.OrdinalIgnoreCase))
            {
                nativeRuntime = WorkerRuntime.Rust;
                return true;
            }

            nativeRuntime = WorkerRuntime.None;
            return false;
        }

        public static string GetDefaultTemplateLanguageFromWorker(WorkerRuntime worker)
        {
            if (!_workerToDefaultLanguageMap.ContainsKey(worker))
            {
                throw new ArgumentException($"Worker runtime '{worker}' is not a valid worker for a template.");
            }

            return _workerToDefaultLanguageMap[worker];
        }

        public static bool IsDotnet(WorkerRuntime worker)
        {
            return worker == WorkerRuntime.Dotnet || worker == WorkerRuntime.DotnetIsolated;
        }

        // Go and Rust both run as native workers: FUNCTIONS_WORKER_RUNTIME=native, the shared
        // workers/native/worker.config.json, and a compiled executable at bin/app.
        public static bool IsNative(WorkerRuntime worker)
        {
            return worker == WorkerRuntime.Go || worker == WorkerRuntime.Rust;
        }

        public static WorkerRuntime GetPlatformWorkerRuntime(WorkerRuntime worker)
        {
            // The Azure side has no Rust stack. A native app runs on the stack that Go uses, with
            // FUNCTIONS_WORKER_RUNTIME=native, the Flex runtime name 'go' and LinuxFxVersion 'Go|1.0'.
            // Unverified for Rust, because no Rust app has been run on Azure yet.
            return IsNative(worker) ? WorkerRuntime.Go : worker;
        }

        public static bool IsDotnetIsolated(WorkerRuntime worker)
        {
            return worker == WorkerRuntime.DotnetIsolated;
        }
    }
}
