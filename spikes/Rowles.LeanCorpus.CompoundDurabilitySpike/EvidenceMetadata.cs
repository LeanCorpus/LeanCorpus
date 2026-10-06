using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rowles.LeanCorpus.Index.Indexer;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static class EvidenceMetadata
{
    public static (string ExperimentSha, string BaseSha) Write(string root, string platformId, string repository, Arguments arguments)
    {
        string experimentSha;
        string baseSha;
        string branch;
        string sourceProvenance;
        string sourceManifestSha = string.Empty;
        string sourceArchiveSha = string.Empty;
        if (Directory.Exists(Path.Combine(repository, ".git")) || File.Exists(Path.Combine(repository, ".git")))
        {
            experimentSha = RunGit(repository, "rev-parse", "HEAD").Trim();
            baseSha = RunGit(repository, "rev-parse", "HEAD^").Trim();
            branch = RunGit(repository, "branch", "--show-current").Trim();
            string status = RunGit(repository, "status", "--porcelain", "--untracked-files=all");
            if (!string.IsNullOrEmpty(status))
                throw new InvalidOperationException("Measured evidence requires a clean working tree.");
            sourceProvenance = "git-working-tree";
        }
        else
        {
            experimentSha = arguments.Required("experiment-sha");
            baseSha = arguments.Required("base-sha");
            branch = arguments.Required("branch");
            string sourceManifestPath = Path.GetFullPath(arguments.Required("source-manifest"));
            string sourceArchivePath = Path.GetFullPath(arguments.Required("source-archive"));
            (sourceManifestSha, sourceArchiveSha) = VerifySourceArchive(
                repository, sourceManifestPath, sourceArchivePath, experimentSha, baseSha, branch);
            File.Copy(sourceManifestPath, Path.Combine(root, "source-manifest.json"), overwrite: false);
            sourceProvenance = "verified-git-archive";
        }

        if (branch != "spike/compound-durability-confirmation")
            throw new InvalidOperationException($"Expected branch spike/compound-durability-confirmation, found '{branch}'.");

        string expectedPlatform = OperatingSystem.IsWindows() ? "windows-ntfs" : OperatingSystem.IsLinux() ? "linux-ext4" : "unsupported";
        if (!string.Equals(platformId, expectedPlatform, StringComparison.Ordinal))
            throw new InvalidOperationException($"Platform id '{platformId}' does not match the current OS '{expectedPlatform}'.");

        string fileSystem = GetFileSystem(root, repository);
        string expectedFileSystem = platformId == "windows-ntfs" ? "NTFS" : "ext4";
        if (!string.Equals(fileSystem, expectedFileSystem, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Evidence path uses '{fileSystem}', expected {expectedFileSystem} for {platformId}.");

        Assembly spikeAssembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("Cannot resolve the spike assembly.");
        string spikePath = Path.GetFullPath(spikeAssembly.Location);
        string corePath = Path.GetFullPath(typeof(IndexWriter).Assembly.Location);
        string spikeHash = HashFile(spikePath);
        string coreHash = HashFile(corePath);
        string sdkVersion = RunProcess("dotnet", ["--version"], repository).Trim();
        var assemblyHashes = new
        {
            experiment_sha = experimentSha,
            base_sha = baseSha,
            branch,
            working_tree_clean = true,
            source_provenance_kind = sourceProvenance,
            source_manifest_sha256 = sourceManifestSha,
            source_archive_sha256 = sourceArchiveSha,
            configuration = "Release",
            target_framework = "net10.0",
            spike_assembly = new { path = spikePath, sha256 = spikeHash },
            leancorpus_core_assembly = new { path = corePath, sha256 = coreHash },
            captured_utc = SpikeInfrastructure.CurrentUtc()
        };
        WriteJson(Path.Combine(root, "source-and-assembly-hashes.json"), assemblyHashes);

        var environment = new
        {
            environmentId = platformId + "-" + Environment.MachineName,
            platformId,
            experimentSha,
            baseSha,
            branch,
            workingTreeClean = true,
            capturedUtc = SpikeInfrastructure.CurrentUtc(),
            machineName = Environment.MachineName,
            operatingSystem = RuntimeInformation.OSDescription,
            operatingSystemVersion = Environment.OSVersion.VersionString,
            runtime = RuntimeInformation.FrameworkDescription,
            runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            sdk = sdkVersion,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            fileSystem,
            evidencePath = Path.GetFullPath(root),
            spikeAssemblySha256 = spikeHash,
            leancorpusCoreAssemblySha256 = coreHash
        };
        WriteJson(Path.Combine(root, "environment.json"), environment);
        WriteSourceMap(Path.Combine(root, "source-map.md"), experimentSha, baseSha, spikeHash, coreHash,
            sourceProvenance, sourceManifestSha, sourceArchiveSha);
        return (experimentSha, baseSha);
    }

    private static (string ManifestSha, string ArchiveSha) VerifySourceArchive(
        string repository,
        string manifestPath,
        string archivePath,
        string experimentSha,
        string baseSha,
        string branch)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("experiment_sha").GetString() != experimentSha
            || root.GetProperty("base_sha").GetString() != baseSha
            || root.GetProperty("branch").GetString() != branch
            || !root.GetProperty("working_tree_clean").GetBoolean())
            throw new InvalidDataException("The source archive manifest does not identify the requested clean commit and branch.");

        string archiveSha = HashFile(archivePath);
        if (!string.Equals(archiveSha, root.GetProperty("archive_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The transferred source archive SHA-256 does not match its provenance manifest.");

        string fullRoot = Path.GetFullPath(repository);
        string rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement entry in root.GetProperty("files").EnumerateArray())
        {
            string relativePath = entry.GetProperty("path").GetString()
                ?? throw new InvalidDataException("A source archive manifest path is null.");
            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || !expected.TryAdd(relativePath, entry.GetProperty("sha256").GetString() ?? string.Empty))
                throw new InvalidDataException($"The source archive manifest contains an invalid or duplicate path '{relativePath}'.");
        }

        foreach ((string relativePath, string expectedSha) in expected)
        {
            string fullPath = Path.Combine(fullRoot, relativePath);
            if (!File.Exists(fullPath) || !string.Equals(HashFile(fullPath), expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Committed source file '{relativePath}' differs from the verified archive.");
        }

        string[] actualPaths = Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(fullRoot, file).Split(Path.DirectorySeparatorChar)[0]
                .Equals("artifacts", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(fullRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedPaths = expected.Keys.Select(static path => path.Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal).ToArray();
        if (!actualPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal))
            throw new InvalidDataException("The Windows source directory contains missing, changed, or untracked files outside build artefacts.");

        return (HashFile(manifestPath), archiveSha);
    }

    private static void WriteSourceMap(
        string path,
        string experimentSha,
        string baseSha,
        string spikeHash,
        string coreHash,
        string sourceProvenance,
        string sourceManifestSha,
        string sourceArchiveSha)
    {
        var lines = new[]
        {
            "# Spike 1B source map",
            "",
            $"Experiment commit: {experimentSha}",
            $"Parent/base commit: {baseSha}",
            $"Spike assembly SHA-256: {spikeHash}",
            $"Rowles.LeanCorpus assembly SHA-256: {coreHash}",
            $"Source provenance: {sourceProvenance}",
            $"Source manifest SHA-256: {sourceManifestSha}",
            $"Source archive SHA-256: {sourceArchiveSha}",
            "",
            "## Owning projects and source",
            "",
            "- Harness and measured workload: spikes/Rowles.LeanCorpus.CompoundDurabilitySpike/ (Rowles.LeanCorpus.CompoundDurabilitySpike.csproj).",
            "- Existing disabled-by-default observation hooks: src/core/Rowles.LeanCorpus/Diagnostics/SpikeInstrumentation.cs, src/core/Rowles.LeanCorpus/Index/Indexer/CommitManager.cs, src/core/Rowles.LeanCorpus/Index/Indexer/SegmentFlusher.cs, src/core/Rowles.LeanCorpus/Index/Segment/SegmentFileSet.cs, and src/core/Rowles.LeanCorpus/Store/ compound, file-sync, directory-sync, and atomic-publication code.",
            "- Dataset generation: Rowles.DataForge.Workloads.LeanCorpusSearchProfile through DataForgeMaterialiser.GenerateToStream, seed 42 and 100,000 records.",
            "- No shipped API, package metadata, format, or production durability ordering is changed by this confirmation harness.",
            "",
            "## Architectural decisions read for this task",
            "",
            "- ADR005: docs/articles/ADRs/ADR005-dwpt-segment-flush.md",
            "- ADR007: docs/articles/ADRs/ADR007-merge-must-not-block-commit.md",
            "- ADR010: docs/articles/ADRs/ADR010-close-before-rename-migration.md",
            "- ADR024: docs/articles/ADRs/ADR024-memory-mapped-compound-segment-files.md",
            "- ADR029: docs/articles/ADRs/ADR029-platform-filesystem-durability.md",
            "- ADR036: docs/articles/ADRs/ADR036-commit-scoped-segment-state.md",
            "",
            "The instrumentation observes the actual production order: the default non-durable .cfs.tmp output is closed, registered dirty, renamed, and followed by loose-member deletion; a later durable Commit() persists the final .cfs candidate. No extra temporary-file persistence request or pre-rename flush is introduced."
        };
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string GetFileSystem(string path, string repository)
    {
        if (OperatingSystem.IsWindows())
        {
            string volumeRoot = Path.GetPathRoot(Path.GetFullPath(path))
                ?? throw new IOException($"Cannot resolve the volume containing '{path}'.");
            return new DriveInfo(volumeRoot).DriveFormat;
        }

        return RunProcess("findmnt", ["--noheadings", "--output", "FSTYPE", "--target", Path.GetFullPath(path)], repository).Trim();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));

    private static string RunGit(string repository, params string[] arguments)
        => RunProcess("git", arguments, repository);

    private static string RunProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'{fileName}' exited {process.ExitCode}: {error}");
        return output;
    }
}
