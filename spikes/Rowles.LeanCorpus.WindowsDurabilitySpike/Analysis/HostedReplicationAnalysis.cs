using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

internal static class HostedReplicationAnalysis
{
    internal static int Run(SpikeArguments arguments)
    {
        string localPath = Path.GetFullPath(arguments.Required("local-summary"));
        string localInput = Path.GetFullPath(arguments.Required("local-input"));
        string windowsPath = Path.GetFullPath(arguments.Required("windows-summary"));
        string ubuntuPath = Path.GetFullPath(arguments.Required("ubuntu-summary"));
        string windowsInput = Path.GetFullPath(arguments.Required("windows-input"));
        string ubuntuInput = Path.GetFullPath(arguments.Required("ubuntu-input"));
        string output = Path.GetFullPath(arguments.Required("output"));

        using JsonDocument local = Load(localPath);
        using JsonDocument windows = Load(windowsPath);
        using JsonDocument ubuntu = Load(ubuntuPath);
        JsonElement localRoot = local.RootElement;
        JsonElement windowsRoot = windows.RootElement;
        JsonElement ubuntuRoot = ubuntu.RootElement;
        LocalInputAudit localAudit = AuditLocalInput(localInput);
        ValidateSingleEnvironment(windowsInput, "hosted_windows_2025", expectedLaunches: 3);
        ValidateSingleEnvironment(ubuntuInput, "hosted_ubuntu_24_04", expectedLaunches: 3);

        RunnerAudit windowsRunners = AuditHostedRunners(windowsInput, "hosted_windows_2025");
        RunnerAudit ubuntuRunners = AuditHostedRunners(ubuntuInput, "hosted_ubuntu_24_04");
        bool workflowHashConsistent = windowsRunners.Launches.Concat(ubuntuRunners.Launches)
            .Select(static row => row.WorkflowFileSha256).Distinct(StringComparer.Ordinal).Count() == 1 &&
            windowsRunners.Launches.Concat(ubuntuRunners.Launches)
                .All(static row => row.WorkflowFileSha256 != "unknown");
        bool experimentShaConsistent = localAudit.Launches.Count == 5 && windowsRunners.Launches.Count == 3 &&
            localAudit.Launches.Select(static row => row.ExperimentSha).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 &&
            windowsRunners.Launches.Concat(ubuntuRunners.Launches).All(row =>
                string.Equals(row.GithubSha, localAudit.Launches[0].ExperimentSha, StringComparison.OrdinalIgnoreCase));
        bool localComplete = localRoot.GetProperty("matrix_complete").GetBoolean() &&
                             localRoot.GetProperty("environment_stable").GetBoolean() && localAudit.Complete && experimentShaConsistent;
        bool windowsComplete = windowsRoot.GetProperty("matrix_complete").GetBoolean() &&
                               windowsRoot.GetProperty("environment_stable").GetBoolean() && windowsRunners.Complete &&
                               workflowHashConsistent && experimentShaConsistent;
        string localClass = localRoot.GetProperty("primary_classification").GetString() ?? "unstable_or_inconclusive";
        string windowsClass = windowsRoot.GetProperty("primary_classification").GetString() ?? "unstable_or_inconclusive";
        bool scalingAgrees = localComplete && windowsComplete && CardinalityDirectionsAgree(localRoot, windowsRoot);
        string localDominantPhase = DominantPhase(localRoot, localClass);
        string windowsDominantPhase = DominantPhase(windowsRoot, windowsClass);
        string disposition = !localComplete || !windowsComplete ||
                            localClass is "unstable_or_inconclusive" || windowsClass is "unstable_or_inconclusive"
            ? "replication_inconclusive"
            : localClass == windowsClass && scalingAgrees
                ? "replicated"
                : localDominantPhase == windowsDominantPhase && scalingAgrees
                    ? "directionally_replicated"
                    : "environment_sensitive";

        bool ubuntuComplete = ubuntuRoot.GetProperty("matrix_complete").GetBoolean() && ubuntuRunners.Complete &&
                              workflowHashConsistent && experimentShaConsistent;
        object ubuntuControl = InterpretUbuntuControl(localRoot, ubuntuRoot, localComplete, ubuntuComplete);
        var report = new
        {
            schema_version = 1,
            hosted_replication_disposition = disposition,
            local_environment = "local_windows_vm",
            hosted_windows_environment = "hosted_windows_2025",
            ubuntu_control_environment = "hosted_ubuntu_24_04",
            local_matrix_complete = localComplete,
            windows_matrix_complete = windowsComplete,
            ubuntu_matrix_complete = ubuntuComplete,
            local_primary_classification = localClass,
            hosted_windows_primary_classification = windowsClass,
            local_dominant_phase = localDominantPhase,
            hosted_windows_dominant_phase = windowsDominantPhase,
            cardinality_direction_agrees_for_a_c_d = scalingAgrees,
            cardinality_direction_comparison = CompareCardinalityDirections(localRoot, windowsRoot),
            local_cardinality_spearman = localRoot.GetProperty("cardinality_spearman"),
            hosted_windows_cardinality_spearman = windowsRoot.GetProperty("cardinality_spearman"),
            ubuntu_d_cardinality_spearman = ubuntuRoot.GetProperty("cardinality_spearman"),
            ubuntu_control = ubuntuControl,
            local_environment_audit = localAudit,
            hosted_windows_runner_audit = windowsRunners,
            ubuntu_runner_audit = ubuntuRunners,
            raw_timings_pooled = false,
            source_and_assembly_sha256 = new
            {
                local = HashFile(localPath),
                hosted_windows = HashFile(windowsPath),
                ubuntu_control = HashFile(ubuntuPath)
            },
            workflow_file_sha256 = ReadWorkflowHash(windowsInput),
            workflow_hash_consistent_across_all_six_jobs = workflowHashConsistent,
            experiment_sha_consistent_across_local_and_hosted = experimentShaConsistent,
            generated_utc = DateTimeOffset.UtcNow
        };

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        WriteNew(output, FormatMarkdown(report, disposition, localClass, windowsClass,
            localDominantPhase, windowsDominantPhase, scalingAgrees, ubuntuControl));
        WriteNew(Path.ChangeExtension(output, ".json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Hosted replication disposition: {disposition}; Ubuntu control complete: {ubuntuComplete}.");
        return windowsComplete && localComplete && ubuntuComplete ? 0 : 1;
    }

    private static JsonDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required mechanism analysis JSON is missing.", path);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static LocalInputAudit AuditLocalInput(string input)
    {
        string[] paths = Directory.EnumerateFiles(input, "environment.json", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        if (paths.Length != 5)
            throw new InvalidDataException($"Local mechanism analysis requires five launch environments, found {paths.Length}.");

        var rows = new List<LocalEnvironmentIdentity>();
        foreach (string path in paths)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            string expectedSha = Get(root, "experiment_sha");
            string githubSha = Get(root, "github_sha");
            string hashesPath = Path.Combine(Path.GetDirectoryName(path)!, "source-and-assembly-hashes.json");
            using JsonDocument hashes = JsonDocument.Parse(File.ReadAllText(hashesPath));
            JsonElement hashRoot = hashes.RootElement;
            rows.Add(new LocalEnvironmentIdentity(
                Get(root, "environment_id"), Get(root, "environment_class"), expectedSha,
                Get(root, "github_ref"), Get(root, "operating_system"), Get(root, "windows_edition"),
                Get(root, "windows_build"), Get(root, "cpu_model"), Get(root, "logical_processors"),
                Get(root, "ram_bytes"), Get(root, "filesystem"), Get(root, "hypervisor"),
                Get(root, "libvirt_cache_mode"), Get(root, "libvirt_io_mode"),
                Get(hashRoot.GetProperty("spike_assembly"), "sha256"),
                Get(hashRoot.GetProperty("core_assembly"), "sha256"),
                expectedSha != "unknown" && (githubSha == "unknown" ||
                    string.Equals(expectedSha, githubSha, StringComparison.OrdinalIgnoreCase))));
        }
        bool complete = rows.Select(static row => row.EnvironmentId).Distinct(StringComparer.Ordinal).Count() == 5 &&
                        rows.All(static row => row.EnvironmentClass == "local_windows_vm" && row.ShaMatches &&
                                               row.ExperimentSha != "unknown" && row.OperatingSystem != "unknown" &&
                                               row.WindowsEdition != "unknown" && row.WindowsBuild != "unknown" &&
                                               row.CpuModel != "unknown" && row.LogicalProcessors != "unknown" &&
                                               row.RamBytes != "unknown" && row.Filesystem == "NTFS" &&
                                               row.Hypervisor != "unknown" && row.LibvirtCacheMode != "unknown" &&
                                               row.LibvirtIoMode != "unknown" && row.SpikeAssemblySha256 != "unknown" &&
                                               row.CoreAssemblySha256 != "unknown") &&
                        rows.Select(static row => row.ExperimentSha).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 &&
                        rows.Select(static row => row.SpikeAssemblySha256).Distinct(StringComparer.Ordinal).Count() == 1 &&
                        rows.Select(static row => row.CoreAssemblySha256).Distinct(StringComparer.Ordinal).Count() == 1;
        return new LocalInputAudit(rows.Count, complete, rows);
    }

    private static void ValidateSingleEnvironment(string input, string expected, int expectedLaunches)
    {
        string[] paths = Directory.EnumerateFiles(input, "environment.json", SearchOption.AllDirectories).ToArray();
        string[] environments = paths.Select(path =>
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return Get(document.RootElement, "environment_class");
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (environments.Length != 1 || environments[0] != expected || paths.Length != expectedLaunches)
            throw new InvalidDataException($"Expected {expectedLaunches} inputs of only {expected}, found {paths.Length} inputs for {string.Join(", ", environments)}.");
    }

    private static RunnerAudit AuditHostedRunners(string input, string expectedEnvironment)
    {
        string[] paths = Directory.EnumerateFiles(input, "environment.json", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal).ToArray();
        var rows = new List<RunnerIdentity>();
        foreach (string path in paths)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            string environment = Get(root, "environment_class");
            if (environment != expectedEnvironment)
                throw new InvalidDataException($"Environment file '{path}' identifies {environment}, expected {expectedEnvironment}.");
            string expectedSha = Get(root, "experiment_sha");
            string githubSha = Get(root, "github_sha");
            bool shaMatches = expectedSha != "unknown" && string.Equals(expectedSha, githubSha, StringComparison.OrdinalIgnoreCase);
            string hashesPath = Path.Combine(Path.GetDirectoryName(path)!, "source-and-assembly-hashes.json");
            using JsonDocument hashes = JsonDocument.Parse(File.ReadAllText(hashesPath));
            string spikeHash = hashes.RootElement.GetProperty("spike_assembly").GetProperty("sha256").GetString() ?? "unknown";
            string coreHash = hashes.RootElement.GetProperty("core_assembly").GetProperty("sha256").GetString() ?? "unknown";
            rows.Add(new RunnerIdentity(
                Get(root, "environment_id"),
                Get(root, "github_run_id"),
                Get(root, "github_run_attempt"),
                Get(root, "github_job"),
                githubSha,
                Get(root, "github_ref"),
                Get(root, "runner_os"),
                Get(root, "runner_arch"),
                Get(root, "runner_name"),
                Get(root, "runner_environment"),
                Get(root, "runner_image_os"),
                Get(root, "runner_image_version"),
                Get(root, "cpu_model"),
                Get(root, "logical_processors"),
                Get(root, "ram_bytes"),
                Get(root, "filesystem"),
                Get(root, "dotnet_sdk"),
                Get(root, "dotnet_runtime"),
                Get(root, "workflow_file_sha256"),
                spikeHash,
                coreHash,
                shaMatches));
        }

        string expectedOs = expectedEnvironment == "hosted_windows_2025" ? "Windows" : "Linux";
        bool complete = rows.Count == 3 &&
                        rows.Select(static row => row.EnvironmentId).Distinct(StringComparer.Ordinal).Count() == 3 &&
                        rows.Select(static row => row.GithubJob).Distinct(StringComparer.Ordinal).Count() == 3 &&
                        rows.Select(static row => row.RunnerName).Distinct(StringComparer.Ordinal).Count() == 3 &&
                        rows.All(row => row.ShaMatches && row.GithubRunId != "unknown" && row.GithubRunAttempt != "unknown" &&
                                        row.GithubRef == "refs/heads/spike/windows-durability-publication" &&
                                        row.RunnerOs == expectedOs && row.RunnerArch != "unknown" &&
                                        row.RunnerName != "unknown" && row.RunnerEnvironment != "unknown" &&
                                        row.RunnerImageOs != "unknown" && row.RunnerImageVersion != "unknown" &&
                                        row.CpuModel != "unknown" && row.LogicalProcessors != "unknown" &&
                                        row.RamBytes != "unknown" && row.Filesystem != "unknown" &&
                                        row.DotnetSdk != "unknown" && row.DotnetRuntime != "unknown" &&
                                        row.WorkflowFileSha256 != "unknown" &&
                                        row.SpikeAssemblySha256 != "unknown" && row.CoreAssemblySha256 != "unknown");
        complete &= rows.Select(static row => row.WorkflowFileSha256).Distinct(StringComparer.Ordinal).Count() == 1;
        complete &= rows.Select(static row => row.SpikeAssemblySha256).Distinct(StringComparer.Ordinal).Count() == 1;
        complete &= rows.Select(static row => row.CoreAssemblySha256).Distinct(StringComparer.Ordinal).Count() == 1;
        complete &= rows.Select(static row => row.GithubSha).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return new RunnerAudit(expectedEnvironment, rows.Count, complete, rows);
    }

    private static bool CardinalityDirectionsAgree(JsonElement local, JsonElement hosted)
    {
        Dictionary<string, int> localDirections = CardinalityDirectionMap(local);
        Dictionary<string, int> hostedDirections = CardinalityDirectionMap(hosted);
        return localDirections.Count == 6 && hostedDirections.Count == 6 && localDirections.All(pair =>
            hostedDirections.TryGetValue(pair.Key, out int direction) && direction == pair.Value);
    }

    private static string[][] CompareCardinalityDirections(JsonElement local, JsonElement hosted)
    {
        Dictionary<string, (long Payload, string Variant, double Rho)> a = CardinalityValues(local);
        Dictionary<string, (long Payload, string Variant, double Rho)> b = CardinalityValues(hosted);
        return a.Keys.Union(b.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(key =>
            {
                bool hasLocal = a.TryGetValue(key, out var localValue);
                bool hasHosted = b.TryGetValue(key, out var hostedValue);
                return new[]
                {
                    hasLocal ? localValue.Payload.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown",
                    hasLocal ? localValue.Variant : "unknown",
                    hasLocal ? localValue.Rho.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "unknown",
                    hasHosted ? hostedValue.Rho.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "unknown",
                    hasLocal && hasHosted && Direction(localValue.Rho) == Direction(hostedValue.Rho) ? "agree" : "differ_or_missing"
                };
            }).ToArray();
    }

    private static Dictionary<string, int> CardinalityDirectionMap(JsonElement report)
        => CardinalityValues(report).ToDictionary(static pair => pair.Key, static pair => Direction(pair.Value.Rho), StringComparer.Ordinal);

    private static Dictionary<string, (long Payload, string Variant, double Rho)> CardinalityValues(JsonElement report)
    {
        var values = new Dictionary<string, (long, string, double)>(StringComparer.Ordinal);
        foreach (JsonElement item in report.GetProperty("cardinality_spearman").EnumerateArray())
        {
            string variant = item.GetProperty("variant").GetString() ?? string.Empty;
            if (variant is not "A_current_full" and not "C_flush_only_preopened" and not "D_leancorpus_wrapper")
                continue;
            long payload = item.GetProperty("payload_bytes").GetInt64();
            double rho = item.GetProperty("spearman_file_count_median_ms").GetDouble();
            values.Add($"{payload}|{variant}", (payload, variant, rho));
        }
        return values;
    }

    private static string DominantPhase(JsonElement report, string classification)
    {
        if (classification == "wrapper_dominated")
            return "wrapper";
        if (classification == "unstable_or_inconclusive")
            return "unknown";
        var shares = report.GetProperty("phase_shares").EnumerateArray()
            .Select(item => (Flush: item.GetProperty("median_flush_share").GetDouble(),
                Handle: item.GetProperty("median_open_close_share").GetDouble())).ToArray();
        if (shares.Length == 0)
            return "unknown";
        double flush = MechanismStatistics.Median(shares.Select(static row => row.Flush).ToArray());
        double handle = MechanismStatistics.Median(shares.Select(static row => row.Handle).ToArray());
        return Math.Abs(flush - handle) < 0.05 ? "mixed" : flush > handle ? "flush" : "handle";
    }

    private static object InterpretUbuntuControl(JsonElement local, JsonElement ubuntu, bool localComplete, bool ubuntuComplete)
    {
        var localD = CorrelationsByPayload(local, "D_leancorpus_wrapper");
        var ubuntuD = CorrelationsByPayload(ubuntu, "D_leancorpus_wrapper");
        bool positive = localComplete && ubuntuComplete && localD.Count == 2 && ubuntuD.Count == 2 &&
                        localD.All(pair => pair.Value > 0 && ubuntuD.TryGetValue(pair.Key, out double rho) && rho > 0);
        bool muchWeaker = positive && localD.Any(pair => ubuntuD[pair.Key] < pair.Value * 0.5d);
        string interpretation = !localComplete || !ubuntuComplete
            ? "control_incomplete"
            : positive
                ? muchWeaker
                    ? "general_per_file_effect_with_weaker_ubuntu_magnitude"
                    : "general_per_file_durability_effect"
                : "windows_specific_mechanism_or_magnitude_better_supported";
        return new { interpretation, positive_cardinality_direction_for_both_payloads = positive,
            ubuntu_is_much_weaker_than_local_windows = muchWeaker,
            local_windows_d_spearman = localD, ubuntu_d_spearman = ubuntuD };
    }

    private static Dictionary<long, double> CorrelationsByPayload(JsonElement report, string variant)
    {
        var result = new Dictionary<long, double>();
        foreach (JsonElement item in report.GetProperty("cardinality_spearman").EnumerateArray())
        {
            if (item.GetProperty("variant").GetString() != variant)
                continue;
            result.Add(item.GetProperty("payload_bytes").GetInt64(),
                item.GetProperty("spearman_file_count_median_ms").GetDouble());
        }
        return result;
    }

    private static int Direction(double value) => double.IsNaN(value) || value == 0 ? 0 : value > 0 ? 1 : -1;

    private static string Get(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) ? value.ToString() : "unknown";

    private static string ReadWorkflowHash(string input)
    {
        string[] paths = Directory.EnumerateFiles(input, "environment.json", SearchOption.AllDirectories).ToArray();
        if (paths.Length == 0)
            return "unknown";
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(paths[0]));
        return Get(document.RootElement, "workflow_file_sha256");
    }

    private static string FormatMarkdown(object report, string disposition, string localClass, string hostedClass,
        string localPhase, string hostedPhase, bool scalingAgrees, object ubuntuControl)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Hosted mechanism replication summary");
        builder.AppendLine();
        builder.AppendLine($"Hosted Windows replication disposition: **{disposition}**.");
        builder.AppendLine();
        builder.AppendLine("Local VM and hosted runner timings are analysed within their own environments. Raw samples are not pooled.");
        builder.AppendLine();
        builder.AppendLine("| Measure | Local Windows VM | Hosted windows-2025 |");
        builder.AppendLine("|---|---|---|");
        builder.AppendLine($"| Primary A-D classification | {localClass} | {hostedClass} |");
        builder.AppendLine($"| Dominant phase | {localPhase} | {hostedPhase} |");
        builder.AppendLine($"| A/C/D cardinality direction agrees | {scalingAgrees} | {scalingAgrees} |");
        builder.AppendLine();
        builder.AppendLine("## Runner provenance");
        builder.AppendLine();
        builder.AppendLine("Each hosted launch must identify a distinct GitHub job and runner name, the frozen SHA, runner image, CPU, memory, filesystem, runtime and assembly hashes. See the JSON report for every captured field.");
        builder.AppendLine();
        builder.AppendLine("## Ubuntu control");
        builder.AppendLine();
        builder.AppendLine("Ubuntu runs D_leancorpus_wrapper only. Its file-count correlations control the wording of Windows specificity and do not change the Windows replication disposition.");
        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine(JsonSerializer.Serialize(ubuntuControl, new JsonSerializerOptions { WriteIndented = true }));
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## Machine-readable details");
        builder.AppendLine();
        builder.AppendLine("The JSON companion preserves environment-separated cardinality correlations, completeness and workflow provenance.");
        return builder.ToString();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteNew(string path, string contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    private sealed record LocalInputAudit(int LaunchCount, bool Complete, IReadOnlyList<LocalEnvironmentIdentity> Launches);
    private sealed record LocalEnvironmentIdentity(string EnvironmentId, string EnvironmentClass, string ExperimentSha,
        string GithubRef, string OperatingSystem, string WindowsEdition, string WindowsBuild, string CpuModel,
        string LogicalProcessors, string RamBytes, string Filesystem, string Hypervisor, string LibvirtCacheMode,
        string LibvirtIoMode, string SpikeAssemblySha256, string CoreAssemblySha256, bool ShaMatches);
    private sealed record RunnerAudit(string Environment, int LaunchCount, bool Complete, IReadOnlyList<RunnerIdentity> Launches);
    private sealed record RunnerIdentity(string EnvironmentId, string GithubRunId, string GithubRunAttempt, string GithubJob,
        string GithubSha, string GithubRef, string RunnerOs, string RunnerArch, string RunnerName, string RunnerEnvironment,
        string RunnerImageOs, string RunnerImageVersion, string CpuModel, string LogicalProcessors, string RamBytes,
        string Filesystem, string DotnetSdk, string DotnetRuntime, string WorkflowFileSha256,
        string SpikeAssemblySha256, string CoreAssemblySha256, bool ShaMatches);
}
