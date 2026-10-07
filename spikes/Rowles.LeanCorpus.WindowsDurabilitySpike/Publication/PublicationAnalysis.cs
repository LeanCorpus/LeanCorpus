using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Publication;

internal static class PublicationAnalysis
{
    private static readonly string[] Candidates = ["P0", "P1", "P2"];
    private static readonly string[] Representations = ["loose", "compound"];
    private static readonly string[] CommonProcessFailpoints =
    [
        "after_segment_files_complete", "after_data_files_persisted", "after_commit_marker_tmp_written",
        "after_commit_marker_tmp_persisted", "before_publication_call", "after_publication_call",
        "after_directory_persist_attempt", "after_commit_return"
    ];
    private static readonly string[] CompoundProcessFailpoints =
    ["after_compound_tmp_close_before_rename", "after_compound_rename", "after_loose_members_deleted"];
    private static readonly string[] HardResetFailpoints =
    ["after_commit_marker_tmp_persisted", "after_publication_call", "after_directory_persist_attempt", "after_commit_return"];
    private static readonly string[] CompoundHardResetFailpoints =
    ["after_compound_rename", "after_loose_members_deleted"];
    private static readonly string[] FaultOperations = ["temp_marker_file_persist", "publication_call", "directory_persist"];

    internal static void ValidateContract()
    {
        var publication = new List<PublicationSample>();
        foreach (string candidate in Candidates)
        foreach (string representation in Representations)
        foreach (int launch in Enumerable.Range(1, 5))
        for (int observation = 0; observation <= 5; observation++)
        {
            double time = candidate switch { "P1" => 10d, "P0" => 20d, _ => 30d };
            publication.Add(FixturePublicationSample(launch, candidate, representation, observation,
                observation == 0, time));
        }
        if (!HasCompleteLatencyMatrix(publication) || HasCompleteLatencyMatrix(publication.Skip(1).ToArray()))
            throw new InvalidOperationException("Publication analysis must require one warm-up and five measured rows per cell and launch.");
        if (!HasStableAdvantage(publication, "P1", "P0", "loose") ||
            HasStableAdvantage(publication, "P0", "P1", "loose"))
            throw new InvalidOperationException("Publication leave-one-launch-out comparison must retain five independent omissions.");

        foreach (string termination in new[] { "process_crash", "hard_reset" })
        {
            RecoverySample[] recovery = ExpectedRecoveryCells(termination)
                .SelectMany(cell => Enumerable.Range(1, 3).Select(trial => new RecoverySample(
                    $"fixture-{cell.Candidate}-{cell.Representation}-{cell.Failpoint}-{trial}", termination,
                    cell.Candidate, cell.Representation, cell.Failpoint, trial, true,
                    cell.Failpoint == "after_commit_return" ? "new_generation" : "none_or_old",
                    cell.Failpoint == "after_commit_return" ? 10_000 : 0,
                    cell.Failpoint == "after_commit_return" ? 14 : 0,
                    true, cell.Failpoint == "after_commit_return", true, string.Empty))).ToArray();
            if (!HasCompleteRecoveryMatrix(recovery, termination) ||
                HasCompleteRecoveryMatrix(recovery.Skip(1).ToArray(), termination))
                throw new InvalidOperationException($"The {termination} matrix must contain three trials for every applicable candidate, representation and failpoint.");
        }

        FaultSample[] faults = (from candidate in Candidates
                                from representation in Representations
                                from operation in FaultOperations
                                from edge in new[] { "before", "after" }
                                select new FaultSample("fixture", candidate, representation, operation, edge,
                                    true, false, "none_or_old", 0, true, true, true, string.Empty)).ToArray();
        if (!HasCompleteFaultMatrix(faults) || HasCompleteFaultMatrix(faults.Skip(1).ToArray()))
            throw new InvalidOperationException("The deterministic fault matrix must retain exactly both edges for all 18 operation cells.");

        (string Cell, string Candidate, string Representation)[] traceCells =
        [
            .. (from candidate in Candidates from representation in Representations
                select ($"{candidate}-{representation}", candidate, representation)),
            ("mechanism-A_current_full-8MiB-16", "A_current_full", "8MiB-16"),
            ("mechanism-D_leancorpus_wrapper-8MiB-16", "D_leancorpus_wrapper", "8MiB-16"),
            ("mechanism-A_current_full-82MiB-64", "A_current_full", "82MiB-64"),
            ("mechanism-D_leancorpus_wrapper-82MiB-64", "D_leancorpus_wrapper", "82MiB-64")
        ];
        TraceSample[] traces = traceCells.Select(cell => new TraceSample(cell.Cell, "trace.etl",
            new string('a', 64), cell.Candidate, cell.Representation, 1, 1,
            "CreateFileW -> FlushFileBuffers -> close", "passed", true, string.Empty)).ToArray();
        if (!HasCompleteTraceMatrix(traces) || HasCompleteTraceMatrix(traces.Skip(1).ToArray()))
            throw new InvalidOperationException("ETW validation requires six publication cells and four representative mechanism cells.");

        CandidateAssessment[] admissible = Candidates.Select(candidate => new CandidateAssessment(candidate,
            true, true, true, true, true, true, true, string.Empty)).ToArray();
        if (SelectCandidate(admissible, publication)?.Candidate != "P1")
            throw new InvalidOperationException("A candidate with a stable faster direction in both representations must win the latency tie-break.");
    }

    private static PublicationSample FixturePublicationSample(int launch, string candidate, string representation,
        int observation, bool warmUp, double confirmationMs)
        => new(
            Launch: launch, Seed: (uint)(20261400 + launch), Order: observation,
            Candidate: candidate, Representation: representation, Observation: observation, WarmUp: warmUp,
            DatasetSha256: "fixture", CommitSucceeded: true, CommitCallMs: 10, DurabilitySyncMs: 9,
            TempMarkerPersistMs: 1, PublicationApi: "MoveFileExW", PublicationFlags: "0x00000009",
            SameVolumeCheck: candidate == "P0" ? "same_parent_directory" : "same_volume",
            PublicationCallMs: 2, PublicationToConfirmationMs: confirmationMs,
            DirectoryPersistMs: candidate == "P1" ? 0 : 1, DirectoryPersistNotApplicable: candidate == "P1",
            MoveFileExReturned: "true", MoveFileExError: "0", FilePersistRequests: 1,
            FilePersistSuccess: 1, FilePersistFailed: 0, FilePersistElapsedMs: 9,
            DirectoryPersistRequests: candidate == "P1" ? 0 : 1,
            DirectoryPersistSuccess: candidate == "P1" ? 0 : 1, DirectoryPersistUnsupported: 0,
            DirectoryPersistFailed: 0, DirectoryPersistElapsedMs: candidate == "P1" ? 0 : 1,
            AtomicReplaceCount: 1, RetryCount: 0, RetryDelayMs: 0, DurabilityCandidateFiles: 10,
            DurabilityCandidateBytes: 100, CompoundPackMemberCount: 0, CompoundPackInputBytes: 0,
            CompoundPackOutputBytes: 0, CompoundPackMs: 0, Error: string.Empty);

    internal static int Run(SpikeArguments arguments)
    {
        string root = Path.GetFullPath(arguments.Required("input"));
        string mechanismSummaryPath = Path.GetFullPath(arguments.Required("mechanism-summary"));
        string hostedSummaryPath = Path.GetFullPath(arguments.Required("hosted-summary"));
        string candidateValidationPath = Path.GetFullPath(arguments.Required("candidate-validation"));
        string semanticsPath = Path.GetFullPath(arguments.Required("semantics"));
        string output = Path.GetFullPath(arguments.Required("output"));

        using JsonDocument mechanismSummary = JsonDocument.Parse(File.ReadAllText(mechanismSummaryPath));
        using JsonDocument hostedSummary = JsonDocument.Parse(File.ReadAllText(hostedSummaryPath));
        using JsonDocument candidateValidation = JsonDocument.Parse(File.ReadAllText(candidateValidationPath));
        string semantics = File.ReadAllText(semanticsPath);
        List<PublicationSample> publication = ReadPublicationSamples(root);
        List<RecoverySample> recovery = ReadRecoverySamples(root);
        List<FaultSample> faults = ReadFaultSamples(root);
        List<TraceSample> traces = ReadTraceSamples(root);

        PublicationCellSummary[] latency = SummariseLatency(publication);
        object[] directoryOutcomes = SummariseDirectoryOutcomes(publication);
        object[] recoveryMatrix = SummariseRecovery(recovery);
        object[] faultMatrix = SummariseFaults(faults);
        object[] launchSensitivity = SummariseLaunchSensitivity(publication);
        object[] leaveOneLaunchOut = SummariseLeaveOneLaunchOut(publication);

        bool candidateGatePassed = candidateValidation.RootElement.TryGetProperty("passed", out JsonElement candidatePassed) && candidatePassed.GetBoolean();
        bool latencyComplete = HasCompleteLatencyMatrix(publication);
        bool processComplete = HasCompleteRecoveryMatrix(recovery, "process_crash");
        bool resetComplete = HasCompleteRecoveryMatrix(recovery, "hard_reset");
        bool faultsComplete = HasCompleteFaultMatrix(faults);
        bool tracesComplete = HasCompleteTraceMatrix(traces);
        bool semanticsComplete = HasRequiredSemantics(semantics);
        bool documentedContract = semanticsComplete && semantics.Contains("documents the complete same-volume P0/P1/P2", StringComparison.OrdinalIgnoreCase);

        CandidateAssessment[] assessments = Candidates.Select(candidate => AssessCandidate(
            candidate, publication, recovery, faults, candidateGatePassed, latencyComplete,
            processComplete, resetComplete, faultsComplete, semanticsComplete)).ToArray();
        CandidateAssessment[] admissible = assessments.Where(static item => item.CorrectnessAdmissible).ToArray();
        CandidateAssessment? selected = SelectCandidate(admissible, publication);
        string disposition = selected is null || !latencyComplete || !processComplete || !resetComplete ||
                            !faultsComplete || !tracesComplete || !semanticsComplete
            ? "no_defensible_strict_claim"
            : selected.Candidate switch
            {
                "P0" => "current_path",
                "P1" => "write_through_only",
                "P2" => "write_through_plus_directory_persist",
                _ => "no_defensible_strict_claim"
            };
        string claimScope = disposition == "no_defensible_strict_claim"
            ? processComplete ? "process_crash_only" : "no_strict_claim"
            : documentedContract ? "documented_contract" : "tested_vm_reset_contract";

        JsonElement mechanismRoot = mechanismSummary.RootElement;
        JsonElement hostedRoot = hostedSummary.RootElement;
        string primaryClass = GetString(mechanismRoot, "primary_classification");
        string hostedDisposition = GetString(hostedRoot, "hosted_replication_disposition");
        string ubuntuInterpretation = hostedRoot.TryGetProperty("ubuntu_control", out JsonElement ubuntu)
            ? GetString(ubuntu, "interpretation") : "control_incomplete";
        var report = new
        {
            schema_version = 1,
            publication_disposition = disposition,
            claim_scope = claimScope,
            selected_candidate = selected?.Candidate ?? "none",
            selection_reason = selected?.SelectionReason ?? "No candidate has complete admissible process-crash and hard-reset evidence plus deterministic failure evidence.",
            evidence_complete = new
            {
                candidate_validation = candidateGatePassed,
                latency_matrix = latencyComplete,
                process_crash_matrix = processComplete,
                hard_reset_matrix = resetComplete,
                fault_injection_matrix = faultsComplete,
                publication_etw_traces = tracesComplete,
                publication_semantics = semanticsComplete,
                documented_contract = documentedContract
            },
            spike_2a_bottleneck_classification = primaryClass,
            spike_2a_phase_decomposition = mechanismRoot.GetProperty("phase_summaries"),
            spike_2a_wrapper_overhead = mechanismRoot.GetProperty("matched_d_minus_a"),
            publication_latency = latency,
            directory_persistence_outcomes = directoryOutcomes,
            process_kill_recovery_matrix = recoveryMatrix.Where(item => GetObjectString(item, "termination_class") == "process_crash").ToArray(),
            hard_reset_recovery_matrix = recoveryMatrix.Where(item => GetObjectString(item, "termination_class") == "hard_reset").ToArray(),
            deterministic_failure_injection_matrix = faultMatrix,
            launch_sensitivity = launchSensitivity,
            leave_one_launch_out = leaveOneLaunchOut,
            candidate_assessments = assessments,
            hosted_windows_replication_disposition = hostedDisposition,
            ubuntu_hosted_control_interpretation = ubuntuInterpretation,
            environment_sensitive = hostedDisposition is "environment_sensitive" or "replication_inconclusive",
            local_windows_vm_limitation = "The primary Windows guest is hosted by the Debian machine. Reset results apply only to the tested guest, hypervisor, host storage and cache configuration; they are not physical power-loss proof.",
            semantics_basis = new
            {
                documented_contract = documentedContract,
                semantic_document_sha256 = HashFile(semanticsPath),
                summary = "The referenced API documentation does not establish the complete same-volume publication sequence as a general crash-atomic or physical-power-loss contract."
            },
            trace_matrix = traces,
            input_sha256 = new
            {
                mechanism_summary = HashFile(mechanismSummaryPath),
                hosted_replication_summary = HashFile(hostedSummaryPath),
                candidate_validation = HashFile(candidateValidationPath),
                publication_semantics = HashFile(semanticsPath)
            },
            generated_utc = DateTimeOffset.UtcNow
        };

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        WriteNew(output, FormatMarkdown(report, mechanismRoot, latency, directoryOutcomes,
            recoveryMatrix, faultMatrix, launchSensitivity, leaveOneLaunchOut, assessments,
            disposition, claimScope, selected?.Candidate ?? "none", hostedDisposition,
            ubuntuInterpretation, tracesComplete, documentedContract));
        string jsonOutput = Path.ChangeExtension(output, ".json");
        WriteNew(jsonOutput, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        WriteAnalysisProvenance(root, mechanismSummaryPath, hostedSummaryPath, candidateValidationPath,
            semanticsPath, output, jsonOutput);
        Console.WriteLine($"Publication disposition: {disposition}; claim scope: {claimScope}; selected candidate: {selected?.Candidate ?? "none"}.");
        return disposition == "no_defensible_strict_claim" ? 1 : 0;
    }

    private static List<PublicationSample> ReadPublicationSamples(string root)
    {
        var rows = new List<PublicationSample>();
        foreach (string path in InputPaths(root, "publication-trials.csv"))
        foreach (string[] row in CsvReader.Read(path))
        {
            if (row.Length != 42)
                throw new InvalidDataException($"Malformed publication trial row in '{path}'. Expected 42 columns, found {row.Length}.");
            rows.Add(new PublicationSample(
                I(row[0]), U(row[1]), I(row[2]), row[3], row[4], I(row[5]), B(row[6]), row[7], B(row[8]),
                D(row[9]), D(row[10]), D(row[11]), row[12], row[13], row[14], D(row[17]), D(row[18]), D(row[19]),
                B(row[20]), row[21], row[22], I(row[23]), I(row[24]), I(row[25]), D(row[26]), I(row[27]), I(row[28]),
                I(row[29]), I(row[30]), D(row[31]), I(row[32]), I(row[33]), D(row[34]), L(row[35]), L(row[36]),
                I(row[37]), L(row[38]), L(row[39]), D(row[40]), row[41]));
        }
        return rows;
    }

    private static List<RecoverySample> ReadRecoverySamples(string root)
    {
        var rows = new List<RecoverySample>();
        foreach (string path in InputPaths(root, "recovery-trials.csv"))
        foreach (string[] row in CsvReader.Read(path))
        {
            if (row.Length != 21)
                throw new InvalidDataException($"Malformed recovery row in '{path}'. Expected 21 columns, found {row.Length}.");
            rows.Add(new RecoverySample(row[0], row[1], row[2], row[3], row[4], I(row[5]), B(row[7]),
                row[9], I(row[10]), I(row[11]), B(row[13]), B(row[14]), B(row[17]), row[18]));
        }
        return rows;
    }

    private static List<FaultSample> ReadFaultSamples(string root)
    {
        var rows = new List<FaultSample>();
        foreach (string path in InputPaths(root, "fault-injection.csv"))
        foreach (string[] row in CsvReader.Read(path))
        {
            if (row.Length != 19)
                throw new InvalidDataException($"Malformed fault-injection row in '{path}'. Expected 19 columns, found {row.Length}.");
            rows.Add(new FaultSample(row[0], row[1], row[2], row[3], row[4], B(row[6]), B(row[7]),
                row[9], I(row[10]), B(row[13]), B(row[14]), B(row[15]), row[16]));
        }
        return rows;
    }

    private static List<TraceSample> ReadTraceSamples(string root)
    {
        string[] paths = InputPaths(root, "trace-index.csv").ToArray();
        var rows = new List<TraceSample>();
        foreach (string path in paths)
        foreach (string[] row in CsvReader.Read(path))
        {
            if (row.Length != 11)
                throw new InvalidDataException($"Malformed trace-index row in '{path}'. Expected 11 columns, found {row.Length}.");
            rows.Add(new TraceSample(row[0], row[1], row[2], row[3], row[4], I(row[5]), I(row[6]), row[7], row[8], B(row[9]), row[10]));
        }
        return rows;
    }

    private static PublicationCellSummary[] SummariseLatency(IReadOnlyList<PublicationSample> rows)
        => (from candidate in Candidates
            from representation in Representations
            let measured = rows.Where(row => row.Candidate == candidate && row.Representation == representation && !row.WarmUp).ToArray()
            let successful = measured.Where(static row => row.CommitSucceeded).ToArray()
            let values = successful.Select(static row => row.PublicationToConfirmationMs).Where(double.IsFinite).ToArray()
            let ci = MechanismStatistics.BootstrapMedian95(values, SeedFor($"{candidate}|{representation}"))
            select new PublicationCellSummary(candidate, representation, measured.Length,
                measured.Length - successful.Length, successful.Length,
                Metric(successful, static row => row.CommitCallMs), Metric(successful, static row => row.DurabilitySyncMs),
                Metric(successful, static row => row.TempMarkerPersistMs), Metric(successful, static row => row.PublicationCallMs),
                Metric(successful, static row => row.DirectoryPersistMs), new MetricSummary(values.Length,
                    MechanismStatistics.Median(values), MechanismStatistics.Percentile(values, 0.25),
                    MechanismStatistics.Percentile(values, 0.75), ci.Lower, ci.Upper)))
            .ToArray();

    private static MetricSummary Metric(IReadOnlyList<PublicationSample> rows, Func<PublicationSample, double> get)
    {
        double[] values = rows.Select(get).Where(double.IsFinite).ToArray();
        (double lower, double upper) = MechanismStatistics.BootstrapMedian95(values, SeedFor(string.Join('|', values)));
        return new MetricSummary(values.Length, MechanismStatistics.Median(values),
            MechanismStatistics.Percentile(values, 0.25), MechanismStatistics.Percentile(values, 0.75), lower, upper);
    }

    private static object[] SummariseDirectoryOutcomes(IReadOnlyList<PublicationSample> rows)
        => (from candidate in Candidates
            from representation in Representations
            let cell = rows.Where(row => row.Candidate == candidate && row.Representation == representation && !row.WarmUp).ToArray()
            select (object)new
            {
                candidate_id = candidate,
                representation,
                not_applicable = cell.Count(static row => row.DirectoryPersistNotApplicable),
                requests = cell.Sum(static row => row.DirectoryPersistRequests),
                succeeded = cell.Sum(static row => row.DirectoryPersistSuccess),
                unsupported_or_skipped = cell.Sum(static row => row.DirectoryPersistUnsupported),
                failed = cell.Sum(static row => row.DirectoryPersistFailed),
                elapsed_ms = cell.Sum(static row => row.DirectoryPersistElapsedMs)
            }).ToArray();

    private static object[] SummariseRecovery(IReadOnlyList<RecoverySample> rows)
        => rows.GroupBy(static row => (row.TerminationClass, row.Candidate, row.Representation, row.Failpoint))
            .OrderBy(static group => group.Key.TerminationClass, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Candidate, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Representation, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Failpoint, StringComparer.Ordinal)
            .Select(group => (object)new
            {
                termination_class = group.Key.TerminationClass,
                candidate_id = group.Key.Candidate,
                representation = group.Key.Representation,
                failpoint = group.Key.Failpoint,
                attempts = group.Count(),
                contract_passed = group.Count(static row => row.ContractPassed),
                new_generation = group.Count(static row => row.GenerationClass == "new_generation"),
                none_or_old_generation = group.Count(static row => row.GenerationClass == "none_or_old"),
                unexpected_generation = group.Count(static row => row.GenerationClass == "unexpected_generation"),
                partial_or_corrupt = group.Count(static row => !row.ReferencedFilesReadable || !row.DeepValidationPassed),
                errors = group.Count(static row => row.Error.Length > 0)
            }).ToArray();

    private static object[] SummariseFaults(IReadOnlyList<FaultSample> rows)
        => rows.GroupBy(static row => (row.Candidate, row.Representation, row.Operation))
            .OrderBy(static group => group.Key.Candidate, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Representation, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Operation, StringComparer.Ordinal)
            .Select(group => (object)new
            {
                candidate_id = group.Key.Candidate,
                representation = group.Key.Representation,
                operation = group.Key.Operation,
                attempts = group.Count(),
                injected = group.Count(static row => row.Injected),
                commit_reported_success = group.Count(static row => row.CommitReportedSuccess),
                recovery_contract_passed = group.Count(static row => row.RecoveryContractPassed),
                contract_passed = group.Count(static row => row.ContractPassed),
                generations = group.Select(static row => row.GenerationClass).ToArray(),
                errors = group.Where(static row => row.Error.Length > 0).Select(static row => row.Error).ToArray()
            }).ToArray();

    private static object[] SummariseLaunchSensitivity(IReadOnlyList<PublicationSample> rows)
        => rows.Where(static row => !row.WarmUp && row.CommitSucceeded)
            .GroupBy(static row => (row.Launch, row.Candidate, row.Representation))
            .OrderBy(static group => group.Key.Launch)
            .ThenBy(static group => group.Key.Candidate, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Representation, StringComparer.Ordinal)
            .Select(group => (object)new
            {
                launch = group.Key.Launch,
                candidate_id = group.Key.Candidate,
                representation = group.Key.Representation,
                observations = group.Count(),
                median_publication_to_confirmation_ms = MechanismStatistics.Median(group.Select(static row => row.PublicationToConfirmationMs).ToArray()),
                median_commit_call_ms = MechanismStatistics.Median(group.Select(static row => row.CommitCallMs).ToArray())
            }).ToArray();

    private static object[] SummariseLeaveOneLaunchOut(IReadOnlyList<PublicationSample> rows)
    {
        var output = new List<object>();
        foreach (string representation in Representations)
        foreach (string candidate in Candidates)
        foreach (string comparator in Candidates.Where(value => value != candidate))
        {
            double[] fullCandidate = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == candidate && row.Representation == representation)
                .Select(static row => row.PublicationToConfirmationMs).ToArray();
            double[] fullComparator = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == comparator && row.Representation == representation)
                .Select(static row => row.PublicationToConfirmationMs).ToArray();
            double fullDifference = MechanismStatistics.Median(fullCandidate) - MechanismStatistics.Median(fullComparator);
            int[] launches = rows.Where(row => row.Candidate == candidate && row.Representation == representation && !row.WarmUp)
                .Select(static row => row.Launch).Distinct().Order().ToArray();
            var omittedDifferences = new List<double>();
            foreach (int launch in launches)
            {
                double[] a = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == candidate &&
                    row.Representation == representation && row.Launch != launch).Select(static row => row.PublicationToConfirmationMs).ToArray();
                double[] b = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == comparator &&
                    row.Representation == representation && row.Launch != launch).Select(static row => row.PublicationToConfirmationMs).ToArray();
                if (a.Length > 0 && b.Length > 0)
                    omittedDifferences.Add(MechanismStatistics.Median(a) - MechanismStatistics.Median(b));
            }
            int fullDirection = Direction(fullDifference);
            bool stable = omittedDifferences.Count == 5 && fullDirection != 0 && omittedDifferences.All(value => Direction(value) == fullDirection);
            output.Add(new
            {
                representation,
                candidate_id = candidate,
                comparator_candidate_id = comparator,
                median_difference_ms = fullDifference,
                omitted_launches = omittedDifferences.Count,
                omitted_launch_differences_ms = omittedDifferences,
                direction_stable = stable
            });
        }
        return output.ToArray();
    }

    private static CandidateAssessment AssessCandidate(string candidate,
        IReadOnlyList<PublicationSample> publication, IReadOnlyList<RecoverySample> recovery,
        IReadOnlyList<FaultSample> faults, bool candidateGate, bool latencyComplete,
        bool processComplete, bool resetComplete, bool faultsComplete, bool semanticsComplete)
    {
        bool cellLatency = Representations.All(representation => publication.Count(row => row.Candidate == candidate &&
            row.Representation == representation && !row.WarmUp && row.CommitSucceeded) == 25);
        bool sameVolume = publication.Where(row => row.Candidate == candidate && !row.WarmUp)
            .All(static row => row.Candidate == "P0"
                ? row.SameVolumeCheck == "same_parent_directory"
                : row.SameVolumeCheck == "same_volume");
        bool processPass = RecoveryCandidatePasses(recovery, "process_crash", candidate);
        bool resetPass = RecoveryCandidatePasses(recovery, "hard_reset", candidate);
        bool faultPass = FaultCandidatePasses(faults, candidate);
        bool admissible = candidateGate && latencyComplete && cellLatency && sameVolume && processComplete &&
                          resetComplete && processPass && resetPass && faultsComplete && faultPass && semanticsComplete;
        return new CandidateAssessment(candidate, candidateGate, cellLatency, sameVolume,
            processPass, resetPass, faultPass, admissible,
            admissible ? "Complete process-crash, hard-reset and injected-failure matrices passed for both representations." :
                "One or more correctness, provenance, matrix completeness, semantics or recovery gates are missing or failed.");
    }

    private static CandidateAssessment? SelectCandidate(IReadOnlyList<CandidateAssessment> admissible,
        IReadOnlyList<PublicationSample> publication)
    {
        if (admissible.Count == 0)
            return null;
        if (admissible.Count == 1)
            return admissible[0] with { SelectionReason = "Only candidate satisfying the complete declared recovery and deterministic-failure contract." };

        CandidateAssessment[] stable = admissible.Where(candidate => admissible
            .Where(other => other.Candidate != candidate.Candidate)
            .All(other => Representations.All(representation => HasStableAdvantage(
                publication, candidate.Candidate, other.Candidate, representation)))).ToArray();
        if (stable.Length > 0)
        {
            CandidateAssessment winner = stable.OrderBy(candidate => MechanismStatistics.Median(publication
                .Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == candidate.Candidate)
                .Select(static row => row.PublicationToConfirmationMs).ToArray())).First();
            return winner with { SelectionReason = "Lowest median publication-to-confirmation time with a faster leave-one-launch-out direction against every other admissible candidate in both representations." };
        }

        CandidateAssessment simple = admissible.OrderBy(item => SimplicityRank(item.Candidate))
            .ThenBy(candidate => MechanismStatistics.Median(publication
                .Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == candidate.Candidate)
                .Select(static row => row.PublicationToConfirmationMs).ToArray())).First();
        return simple with { SelectionReason = "No candidate has a faster leave-one-launch-out direction across both representations; selected the simplest admissible sequence, then lower median confirmation time." };
    }

    private static bool HasStableAdvantage(IReadOnlyList<PublicationSample> rows, string candidate,
        string comparator, string representation)
    {
        double[] fullCandidate = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == candidate && row.Representation == representation)
            .Select(static row => row.PublicationToConfirmationMs).ToArray();
        double[] fullComparator = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == comparator && row.Representation == representation)
            .Select(static row => row.PublicationToConfirmationMs).ToArray();
        if (fullCandidate.Length != 25 || fullComparator.Length != 25 ||
            MechanismStatistics.Median(fullCandidate) >= MechanismStatistics.Median(fullComparator))
            return false;
        for (int launch = 1; launch <= 5; launch++)
        {
            double[] a = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == candidate &&
                row.Representation == representation && row.Launch != launch).Select(static row => row.PublicationToConfirmationMs).ToArray();
            double[] b = rows.Where(row => !row.WarmUp && row.CommitSucceeded && row.Candidate == comparator &&
                row.Representation == representation && row.Launch != launch).Select(static row => row.PublicationToConfirmationMs).ToArray();
            if (a.Length != 20 || b.Length != 20 || MechanismStatistics.Median(a) >= MechanismStatistics.Median(b))
                return false;
        }
        return true;
    }

    private static bool RecoveryCandidatePasses(IReadOnlyList<RecoverySample> rows, string termination, string candidate)
    {
        foreach (string representation in Representations)
        {
            IEnumerable<string> expected = termination == "process_crash"
                ? CommonProcessFailpoints.Where(point => candidate != "P1" || point != "after_directory_persist_attempt")
                    .Concat(representation == "compound" ? CompoundProcessFailpoints : [])
                : HardResetFailpoints.Where(point => candidate != "P1" || point != "after_directory_persist_attempt")
                    .Concat(representation == "compound" ? CompoundHardResetFailpoints : []);
            foreach (string failpoint in expected)
            {
                RecoverySample[] cell = rows.Where(row => row.TerminationClass == termination && row.Candidate == candidate &&
                    row.Representation == representation && row.Failpoint == failpoint).ToArray();
                if (cell.Length != 3 || cell.Any(static row => !row.ContractPassed))
                    return false;
                if (termination == "hard_reset" && failpoint == "after_commit_return" &&
                    cell.Any(static row => row.GenerationClass != "new_generation" || row.DocumentCount != 10_000 ||
                                           row.FixedLookupHits != 14 || !row.ReferencedFilesReadable || !row.DeepValidationPassed))
                    return false;
            }
        }
        return true;
    }

    private static bool FaultCandidatePasses(IReadOnlyList<FaultSample> rows, string candidate)
        => Representations.All(representation => FaultOperations.All(operation =>
        {
            FaultSample[] cell = rows.Where(row => row.Candidate == candidate && row.Representation == representation &&
                row.Operation == operation).ToArray();
            return cell.Length == 2 && cell.Select(static row => row.Edge).Order(StringComparer.Ordinal)
                       .SequenceEqual(new[] { "after", "before" }, StringComparer.Ordinal) &&
                   cell.All(static row => row.Injected && !row.CommitReportedSuccess && row.RecoveryContractPassed && row.ContractPassed);
        }));

    private static bool HasCompleteLatencyMatrix(IReadOnlyList<PublicationSample> rows)
        => Candidates.All(candidate => Representations.All(representation => Enumerable.Range(1, 5).All(launch =>
        {
            PublicationSample[] cell = rows.Where(row => row.Candidate == candidate && row.Representation == representation && row.Launch == launch).ToArray();
            return cell.Count(static row => row.WarmUp) == 1 && cell.Count(static row => !row.WarmUp) == 5 &&
                   cell.All(static row => row.CommitSucceeded && double.IsFinite(row.PublicationToConfirmationMs));
        })));

    private static bool HasCompleteRecoveryMatrix(IReadOnlyList<RecoverySample> rows, string termination)
    {
        var expected = ExpectedRecoveryCells(termination).ToArray();
        foreach (var key in expected)
        {
            RecoverySample[] cell = rows.Where(row => row.TerminationClass == termination && row.Candidate == key.Candidate &&
                row.Representation == key.Representation && row.Failpoint == key.Failpoint).ToArray();
            if (cell.Length != 3)
                return false;
        }
        return rows.Count(row => row.TerminationClass == termination) == expected.Length * 3;
    }

    private static IEnumerable<(string Candidate, string Representation, string Failpoint)> ExpectedRecoveryCells(string termination)
    {
        foreach (string candidate in Candidates)
        foreach (string representation in Representations)
        {
            IEnumerable<string> failpoints = termination == "process_crash"
                ? CommonProcessFailpoints.Where(point => candidate != "P1" || point != "after_directory_persist_attempt")
                    .Concat(representation == "compound" ? CompoundProcessFailpoints : [])
                : HardResetFailpoints.Where(point => candidate != "P1" || point != "after_directory_persist_attempt")
                    .Concat(representation == "compound" ? CompoundHardResetFailpoints : []);
            foreach (string failpoint in failpoints)
                yield return (candidate, representation, failpoint);
        }
    }

    private static bool HasCompleteFaultMatrix(IReadOnlyList<FaultSample> rows)
        => rows.Count == 36 && Candidates.All(candidate => Representations.All(representation => FaultOperations.All(operation =>
        {
            FaultSample[] cell = rows.Where(row => row.Candidate == candidate && row.Representation == representation &&
                row.Operation == operation).ToArray();
            return cell.Length == 2 && cell.All(static row => row.Injected);
        })));

    private static bool HasCompleteTraceMatrix(IReadOnlyList<TraceSample> rows)
    {
        string[] publicationCells = (from candidate in Candidates
                                     from representation in Representations
                                     select $"{candidate}-{representation}").ToArray();
        string[] mechanismCells =
        [
            "mechanism-A_current_full-8MiB-16",
            "mechanism-D_leancorpus_wrapper-8MiB-16",
            "mechanism-A_current_full-82MiB-64",
            "mechanism-D_leancorpus_wrapper-82MiB-64"
        ];
        string[] expected = publicationCells.Concat(mechanismCells).ToArray();
        return rows.Count == expected.Length && expected.All(cell => rows.Count(row => row.CellId == cell && row.Success &&
            row.SequenceValidation == "passed" && row.TraceFile.Length > 0 && row.Sha256.Length == 64) == 1);
    }

    private static bool HasRequiredSemantics(string semantics)
        => new[] { "File.Move", "MoveFileExW", "MOVEFILE_REPLACE_EXISTING", "MOVEFILE_WRITE_THROUGH",
                "CreateFileW", "FlushFileBuffers", "ERROR_ACCESS_DENIED", "2026-10-07" }
            .All(token => semantics.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static int SimplicityRank(string candidate) => candidate switch { "P1" => 0, "P0" => 1, "P2" => 2, _ => 3 };
    private static int Direction(double value) => double.IsFinite(value) && value != 0 ? value > 0 ? 1 : -1 : 0;
    private static uint SeedFor(string value) => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);
    private static int I(string value) => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static long L(string value) => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static uint U(string value) => uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static double D(string value) => double.Parse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    private static bool B(string value) => bool.Parse(value);
    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) ? value.GetString() ?? "unknown" : "unknown";

    private static string GetObjectString(object value, string property)
        => (string)(value.GetType().GetProperty(property)?.GetValue(value) ?? "");

    private static IEnumerable<string> InputPaths(string root, string fileName)
    {
        string top = Path.Combine(root, fileName);
        return File.Exists(top) ? [top] : Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal);
    }

    private static void WriteAnalysisProvenance(string root, params string[] summaries)
    {
        string path = Path.Combine(root, "analysis-provenance.json");
        if (File.Exists(path))
            throw new IOException("Analysis provenance already exists and will not be replaced.");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFullPath(file), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            .Append(summaries[0]).Append(summaries[1]).Append(summaries[2]).Append(summaries[3]).Append(summaries[4]).Append(summaries[5])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists).OrderBy(static file => file, StringComparer.Ordinal)
            .Select(file => new { path = Path.GetRelativePath(root, file).Replace('\\', '/'), sha256 = HashFile(file) }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            analysis_commit_sha = Environment.GetEnvironmentVariable("SPIKE_EXPERIMENT_SHA") ?? "unknown",
            inputs_and_outputs = files,
            generated_utc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    private static string FormatMarkdown(object report, JsonElement mechanism, IReadOnlyList<PublicationCellSummary> latency,
        IReadOnlyList<object> directory, IReadOnlyList<object> recovery, IReadOnlyList<object> faults,
        IReadOnlyList<object> launches, IReadOnlyList<object> loo, IReadOnlyList<CandidateAssessment> candidates,
        string disposition, string claimScope, string selected, string hosted, string ubuntu,
        bool tracesComplete, bool documentedContract)
    {
        var b = new StringBuilder();
        b.AppendLine("# Spike 2 publication summary\n");
        b.AppendLine($"Publication disposition: **{disposition}**. Claim scope: **{claimScope}**. Selected candidate: **{selected}**.\n");
        b.AppendLine("## Spike 2A bottleneck classification\n");
        b.AppendLine($"Primary classification: **{GetString(mechanism, "primary_classification")}**. Local and hosted timings remain separate.\n");
        AppendJson(b, "Phase decomposition by payload and file count", mechanism.GetProperty("phase_summaries"));
        AppendJson(b, "Matched wrapper overhead (D minus A)", mechanism.GetProperty("matched_d_minus_a"));
        b.AppendLine("## Publication latency\n");
        b.AppendLine("| Candidate | Representation | Measured | Failed | Commit median ms | Durability median ms | Temp marker persist median ms | Publication API median ms | Directory persist median ms | Confirmation median ms | Confirmation IQR ms | Bootstrap 95% CI ms |");
        b.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (PublicationCellSummary cell in latency)
            b.AppendLine($"| {cell.Candidate} | {cell.Representation} | {cell.Measured} | {cell.Failed} | {cell.CommitCall.MedianMs:F3} | {cell.DurabilitySync.MedianMs:F3} | {cell.TempMarkerPersist.MedianMs:F3} | {cell.PublicationCall.MedianMs:F3} | {cell.DirectoryPersist.MedianMs:F3} | {cell.PublicationToConfirmation.MedianMs:F3} | {cell.PublicationToConfirmation.IqrMs:F3} | [{cell.PublicationToConfirmation.CiLowerMs:F3}, {cell.PublicationToConfirmation.CiUpperMs:F3}] |");
        AppendJson(b, "Directory persistence outcomes", directory);
        AppendJson(b, "Process-kill recovery matrix", recovery.Where(item => GetObjectString(item, "termination_class") == "process_crash").ToArray());
        AppendJson(b, "Hard-reset recovery matrix", recovery.Where(item => GetObjectString(item, "termination_class") == "hard_reset").ToArray());
        AppendJson(b, "Deterministic failure-injection matrix", faults);
        AppendJson(b, "Launch-level sensitivity", launches);
        AppendJson(b, "Leave-one-launch-out candidate comparisons", loo);
        AppendJson(b, "Candidate admissibility", candidates);
        b.AppendLine("## Publication disposition and documentation basis\n");
        b.AppendLine($"Disposition: **{disposition}**. Claim scope: **{claimScope}**. Selected candidate: **{selected}**.\n");
        b.AppendLine(documentedContract
            ? "The semantics evidence supports the complete tested same-volume sequence."
            : "The cited API documentation does not establish the complete same-volume sequence as a general crash-atomic or physical-power-loss contract.");
        b.AppendLine("Empirical hard-reset results apply to the tested guest, hypervisor, host storage and cache configuration. They do not prove physical power-loss survival.\n");
        b.AppendLine("## Hosted replication\n");
        b.AppendLine($"Hosted Windows replication disposition: **{hosted}**. Ubuntu control interpretation: **{ubuntu}**. Mechanism conclusions are environment-sensitive when hosted replication reports `environment_sensitive` or `replication_inconclusive`.\n");
        b.AppendLine("## ETW traces\n");
        b.AppendLine($"All six publication cells and four representative mechanism cells have successful ordered traces: **{tracesComplete}**.\n");
        b.AppendLine("## Local VM limitation\n");
        b.AppendLine("The local Windows guest is hosted by the Debian machine. It provides controlled guest-reset evidence for that VM and storage stack, not an independent physical Windows host or physical power-loss evidence.\n");
        return b.ToString();
    }

    private static void AppendJson(StringBuilder builder, string title, object value)
    {
        builder.AppendLine($"## {title}\n");
        builder.AppendLine("```json");
        builder.AppendLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        builder.AppendLine("```\n");
    }

    private static void AppendJson(StringBuilder builder, string title, JsonElement value)
    {
        builder.AppendLine($"## {title}\n");
        builder.AppendLine("```json");
        builder.AppendLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        builder.AppendLine("```\n");
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

    private sealed record MetricSummary(int N, double MedianMs, double LowerQuartileMs, double UpperQuartileMs,
        double CiLowerMs, double CiUpperMs)
    {
        internal double IqrMs => UpperQuartileMs - LowerQuartileMs;
    }

    private sealed record PublicationCellSummary(string Candidate, string Representation, int Measured,
        int Failed, int Successful, MetricSummary CommitCall, MetricSummary DurabilitySync,
        MetricSummary TempMarkerPersist, MetricSummary PublicationCall, MetricSummary DirectoryPersist,
        MetricSummary PublicationToConfirmation);

    private sealed record PublicationSample(int Launch, uint Seed, int Order, string Candidate, string Representation,
        int Observation, bool WarmUp, string DatasetSha256, bool CommitSucceeded, double CommitCallMs,
        double DurabilitySyncMs, double TempMarkerPersistMs, string PublicationApi, string PublicationFlags,
        string SameVolumeCheck, double PublicationCallMs, double PublicationToConfirmationMs, double DirectoryPersistMs,
        bool DirectoryPersistNotApplicable, string MoveFileExReturned, string MoveFileExError, int FilePersistRequests,
        int FilePersistSuccess, int FilePersistFailed, double FilePersistElapsedMs, int DirectoryPersistRequests,
        int DirectoryPersistSuccess, int DirectoryPersistUnsupported, int DirectoryPersistFailed,
        double DirectoryPersistElapsedMs, int AtomicReplaceCount, int RetryCount, double RetryDelayMs,
        long DurabilityCandidateFiles, long DurabilityCandidateBytes, int CompoundPackMemberCount,
        long CompoundPackInputBytes, long CompoundPackOutputBytes, double CompoundPackMs, string Error);

    private sealed record RecoverySample(string TrialId, string TerminationClass, string Candidate, string Representation,
        string Failpoint, int Trial, bool FailpointReached, string GenerationClass, int DocumentCount,
        int FixedLookupHits, bool ReferencedFilesReadable, bool DeepValidationPassed, bool ContractPassed, string Error);

    private sealed record FaultSample(string TrialId, string Candidate, string Representation, string Operation,
        string Edge, bool Injected, bool CommitReportedSuccess, string GenerationClass, int DocumentCount,
        bool DeepValidationPassed, bool RecoveryContractPassed, bool ContractPassed, string Error);

    private sealed record TraceSample(string CellId, string TraceFile, string Sha256, string Candidate,
        string Representation, int ExpectedCreateFileCount, int ExpectedFlushFileBuffersCount,
        string ObservedCallSequence, string SequenceValidation, bool Success, string Error);

    private sealed record CandidateAssessment(string Candidate, bool CandidateGatePassed, bool LatencyMatrixPassed,
        bool SameVolumePassed, bool ProcessCrashPassed, bool HardResetPassed, bool FaultInjectionPassed,
        bool CorrectnessAdmissible, string SelectionReason);
}
