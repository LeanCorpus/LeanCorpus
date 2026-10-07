using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using Rowles.LeanCorpus.Index.Indexer;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Mechanisms;

internal static class EnvironmentSnapshot
{
    internal static void Write(string evidenceDirectory, string environmentClass, string dataRoot, string orderFile, int launch)
    {
        string? experimentSha = Environment.GetEnvironmentVariable("SPIKE_EXPERIMENT_SHA");
        string githubSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown";
        string? providedSha = experimentSha ?? Environment.GetEnvironmentVariable("EXPERIMENT_SHA");
        if (string.IsNullOrWhiteSpace(providedSha) || providedSha == "unknown")
            throw new InvalidOperationException("Set SPIKE_EXPERIMENT_SHA to the committed experiment SHA before measurement.");
        EnsureFrozenCheckout(providedSha);
        if (environmentClass.StartsWith("hosted_", StringComparison.Ordinal) &&
            !string.Equals(githubSha, providedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"GITHUB_SHA '{githubSha}' does not equal experiment_sha '{providedSha}'.");

        string? volumeRoot = null;
        string? fileSystem = null;
        string? volumeSerial = null;
        uint? allocationUnit = null;
        if (OperatingSystem.IsWindows())
        {
            var root = new StringBuilder(32768);
            if (NativeVolumeInformation.GetVolumePathName(dataRoot, root, (uint)root.Capacity))
            {
                volumeRoot = root.ToString();
                var label = new StringBuilder(256);
                var fileSystemName = new StringBuilder(256);
                if (NativeVolumeInformation.GetVolumeInformation(volumeRoot, label, (uint)label.Capacity,
                        out uint serial, out _, out _, fileSystemName, (uint)fileSystemName.Capacity))
                {
                    volumeSerial = serial.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
                    fileSystem = fileSystemName.ToString();
                }
                if (NativeVolumeInformation.GetDiskFreeSpace(volumeRoot, out uint sectors, out uint bytesPerSector,
                        out _, out _))
                    allocationUnit = sectors * bytesPerSector;
            }
        }
        else if (Path.GetPathRoot(dataRoot) is string rootPath && Directory.Exists(rootPath))
        {
            var drive = new DriveInfo(rootPath);
            volumeRoot = drive.RootDirectory.FullName;
            fileSystem = drive.DriveFormat;
        }

        var environment = new
        {
            schema_version = 1,
            environment_id = $"{environmentClass}-launch-{launch}",
            environment_class = environmentClass,
            experiment_sha = providedSha,
            github_run_id = ReadEnvironment("GITHUB_RUN_ID"),
            github_run_attempt = ReadEnvironment("GITHUB_RUN_ATTEMPT"),
            github_job = ReadEnvironment("SPIKE_GITHUB_JOB"),
            github_job_id = ReadEnvironment("GITHUB_JOB"),
            github_sha = githubSha,
            github_ref = ReadEnvironment("GITHUB_REF"),
            runner_os = ReadEnvironment("RUNNER_OS"),
            runner_arch = ReadEnvironment("RUNNER_ARCH"),
            runner_name = ReadEnvironment("RUNNER_NAME"),
            runner_environment = ReadEnvironment("RUNNER_ENVIRONMENT"),
            runner_image_os = ReadEnvironment("ImageOS"),
            runner_image_version = ReadEnvironment("ImageVersion"),
            operating_system = RuntimeInformation.OSDescription,
            os_architecture = RuntimeInformation.OSArchitecture.ToString(),
            process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            runtime_version = Environment.Version.ToString(),
            dotnet_runtime = RuntimeInformation.FrameworkDescription,
            sdk_version = ReadEnvironment("DOTNET_SDK_VERSION"),
            dotnet_sdk = ReadEnvironment("DOTNET_SDK_VERSION"),
            processor_count = Environment.ProcessorCount,
            logical_processors = Environment.ProcessorCount,
            cpu_model = ReadEnvironment("SPIKE_CPU_MODEL"),
            physical_memory_bytes = ReadEnvironment("SPIKE_RAM_BYTES"),
            ram_bytes = ReadEnvironment("SPIKE_RAM_BYTES"),
            windows_edition = ReadEnvironment("SPIKE_WINDOWS_EDITION"),
            windows_build = ReadEnvironment("SPIKE_WINDOWS_BUILD"),
            power_plan = ReadEnvironment("SPIKE_POWER_MODE"),
            power_mode = ReadEnvironment("SPIKE_POWER_MODE"),
            defender_state = ReadEnvironment("SPIKE_DEFENDER_STATE"),
            defender_and_filter_drivers = ReadEnvironment("SPIKE_FILTER_STATE"),
            other_filter_driver_or_antivirus_state_if_known = ReadEnvironment("SPIKE_FILTER_STATE"),
            volume_root = volumeRoot ?? "unknown",
            file_system = fileSystem ?? "unknown",
            filesystem = fileSystem ?? "unknown",
            volume_serial = volumeSerial ?? "unknown",
            allocation_unit_bytes = allocationUnit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
            allocation_unit_size = allocationUnit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
            virtual_disk_type = ReadEnvironment("SPIKE_VIRTUAL_DISK_TYPE"),
            virtual_controller = ReadEnvironment("SPIKE_VIRTUAL_CONTROLLER"),
            disk_bus = ReadEnvironment("SPIKE_DISK_BUS"),
            disk_controller_and_bus = "unknown",
            hypervisor = ReadEnvironment("SPIKE_HYPERVISOR"),
            host_os = ReadEnvironment("SPIKE_HOST_OS"),
            host_storage_description = ReadEnvironment("SPIKE_HOST_STORAGE"),
            host_cache_policy_if_known = ReadEnvironment("SPIKE_HOST_CACHE_POLICY"),
            guest_write_cache_policy_if_known = ReadEnvironment("SPIKE_GUEST_CACHE_POLICY"),
            hypervisor_and_host = ReadEnvironment("SPIKE_HYPERVISOR"),
            host_cache_mode = ReadEnvironment("SPIKE_HOST_CACHE_POLICY"),
            guest_cache_mode = ReadEnvironment("SPIKE_GUEST_CACHE_POLICY"),
            backing_file_and_host_filesystem = ReadEnvironment("SPIKE_BACKING_STORE"),
            libvirt_cache_mode = ReadEnvironment("SPIKE_LIBVIRT_CACHE_MODE"),
            libvirt_io_mode = ReadEnvironment("SPIKE_LIBVIRT_IO_MODE"),
            discard_mode = ReadEnvironment("SPIKE_DISCARD_MODE"),
            detect_zeroes_mode = ReadEnvironment("SPIKE_DETECT_ZEROES_MODE"),
            backing_store_type = ReadEnvironment("SPIKE_BACKING_STORE_TYPE"),
            backing_store_path_or_identifier = ReadEnvironment("SPIKE_BACKING_STORE"),
            host_filesystem_for_vm_image = ReadEnvironment("SPIKE_HOST_FILESYSTEM"),
            reset_method = ReadEnvironment("SPIKE_RESET_METHOD"),
            snapshot_id = ReadEnvironment("SPIKE_SNAPSHOT_ID"),
            data_root = dataRoot,
            order_file_sha256 = HashFile(orderFile),
            workflow_file_sha256 = HashWorkflowFile(),
            workflow_ref = ReadEnvironment("GITHUB_WORKFLOW_REF"),
            captured_utc = DateTimeOffset.UtcNow
        };
        var options = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        File.WriteAllText(Path.Combine(evidenceDirectory, "environment.json"),
            JsonSerializer.Serialize(environment, options) + "\n", new UTF8Encoding(false));

        Assembly spikeAssembly = Assembly.GetExecutingAssembly();
        Assembly coreAssembly = typeof(IndexWriter).Assembly;
        var hashes = new
        {
            experiment_sha = providedSha,
            github_sha = githubSha,
            workflow_file_sha256 = HashWorkflowFile(),
            spike_assembly = new { name = spikeAssembly.GetName().Name, path = spikeAssembly.Location, sha256 = HashFile(spikeAssembly.Location) },
            core_assembly = new { name = coreAssembly.GetName().Name, path = coreAssembly.Location, sha256 = HashFile(coreAssembly.Location) }
        };
        File.WriteAllText(Path.Combine(evidenceDirectory, "source-and-assembly-hashes.json"),
            JsonSerializer.Serialize(hashes, options) + "\n", new UTF8Encoding(false));
    }

    private static string ReadEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : "unknown";

    private static string HashWorkflowFile()
    {
        string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        string path = Path.Combine(workspace, ".github", "workflows", "build.yml");
        return File.Exists(path) ? HashFile(path) : "unknown";
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureFrozenCheckout(string experimentSha)
    {
        string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory;
        string actualSha = RunGit(workspace, "rev-parse", "HEAD").Trim();
        if (!string.Equals(actualSha, experimentSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checkout SHA '{actualSha}' does not match frozen experiment SHA '{experimentSha}'.");
        string dirty = RunGit(workspace, "status", "--porcelain", "--untracked-files=all");
        if (!string.IsNullOrWhiteSpace(dirty))
            throw new InvalidOperationException("The source checkout must be clean before collecting Spike 2 measurements.");
    }

    private static string RunGit(string workspace, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(workspace);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start Git provenance check.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git provenance check failed: {error.Trim()}");
        return output;
    }
}

internal static class NativeVolumeInformation
{
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpace(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);
}
