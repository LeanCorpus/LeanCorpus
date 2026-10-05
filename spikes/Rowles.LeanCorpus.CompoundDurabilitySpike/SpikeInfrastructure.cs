using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static class SpikeInfrastructure
{
    public static string BaseSha(Arguments arguments)
    {
        string configured = arguments.Optional("base-sha");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string repository = arguments.Optional("repo", FindRepositoryRoot());
        return RunGit(repository, "rev-parse", "HEAD").Trim();
    }

    public static string SpikeSha(Arguments arguments)
    {
        string configured = arguments.Optional("spike-sha");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string repository = arguments.Optional("repo", FindRepositoryRoot());
        byte[] diff = RunGitBytes(repository, "diff", "--binary", "HEAD");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(diff);
        byte[] untracked = RunGitBytes(repository, "ls-files", "--others", "--exclude-standard", "-z");
        foreach (string relativePath in Encoding.UTF8.GetString(untracked).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
            hash.AppendData(pathBytes);
            using var stream = new FileStream(Path.Combine(repository, relativePath), FileMode.Open, FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return "working-tree-sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string FindRepositoryRoot()
    {
        string? directory = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the LeanCorpus Git repository; pass --repo.");
    }

    public static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    public static async Task<ProcessResult> RunWorkerAsync(
        string root,
        string logPath,
        IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        if (!arguments.TryGetValue("root", out string? workerRoot)
            || !Path.GetFullPath(workerRoot).Equals(Path.GetFullPath(root), StringComparison.Ordinal))
            throw new ArgumentException("The child worker root must match the launch root.", nameof(arguments));
        string assemblyPath = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Could not resolve spike assembly path.");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = FindRepositoryRoot()
        };
        foreach (var (name, value) in arguments)
        {
            startInfo.ArgumentList.Add("--" + name);
            startInfo.ArgumentList.Add(value);
        }
        startInfo.ArgumentList.Insert(0, assemblyPath);
        if (environment is not null)
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start spike worker process.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        await File.WriteAllTextAsync(logPath,
            $"exit_code={process.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}", new UTF8Encoding(false));
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public static string NewRunId()
        => DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
           + "-" + Guid.NewGuid().ToString("N")[..8];

    public static double Milliseconds(long startTimestamp, long endTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMilliseconds;

    public static string CurrentUtc() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string RunGit(string repository, params string[] args)
        => Encoding.UTF8.GetString(RunGitBytes(repository, args));

    private static byte[] RunGitBytes(string repository, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repository
        };
        foreach (string argument in args)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git.");
        using var output = new MemoryStream();
        Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        string error = process.StandardError.ReadToEnd();
        copyTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.ToArray();
    }
}

internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class Csv
{
    public static void Create(string path, IReadOnlyList<string> columns)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteLine(stream, columns);
        stream.Flush(flushToDisk: true);
    }

    public static void Append(string path, IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        if (columns.Count != values.Count)
            throw new ArgumentException($"CSV row has {values.Count} values for {columns.Count} columns.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        WriteLine(stream, values.Select(Format).ToArray());
        stream.Flush(flushToDisk: true);
    }

    public static List<Dictionary<string, string>> Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        string headerLine = reader.ReadLine() ?? throw new InvalidDataException($"CSV '{path}' has no header.");
        string[] headers = ParseLine(headerLine).ToArray();
        var rows = new List<Dictionary<string, string>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = ParseLine(line).ToArray();
            if (fields.Length != headers.Length)
                throw new InvalidDataException($"CSV '{path}' has {fields.Length} fields; expected {headers.Length}.");
            var row = new Dictionary<string, string>(headers.Length, StringComparer.Ordinal);
            for (int index = 0; index < headers.Length; index++)
                row.Add(headers[index], fields[index]);
            rows.Add(row);
        }
        return rows;
    }

    private static void WriteLine(Stream stream, IReadOnlyList<string> fields)
    {
        var builder = new StringBuilder();
        for (int index = 0; index < fields.Count; index++)
        {
            if (index > 0)
                builder.Append(',');
            string field = fields[index];
            if (field.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                builder.Append('"');
                builder.Append(field.Replace("\"", "\"\"", StringComparison.Ordinal));
                builder.Append('"');
            }
            else
            {
                builder.Append(field);
            }
        }
        builder.Append('\n');
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        stream.Write(bytes);
    }

    private static IEnumerable<string> ParseLine(string line)
    {
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (quoted && current == '"' && index + 1 < line.Length && line[index + 1] == '"')
            {
                field.Append('"');
                index++;
            }
            else if (current == '"')
            {
                quoted = !quoted;
            }
            else if (current == ',' && !quoted)
            {
                yield return field.ToString();
                field.Clear();
            }
            else
            {
                field.Append(current);
            }
        }
        yield return field.ToString();
    }

    private static string Format(object? value)
    {
        string formatted = value switch
        {
            null => string.Empty,
            bool boolean => boolean ? "true" : "false",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
        return formatted.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}

internal sealed class CsvRowBuilder(IReadOnlyList<string> columns)
{
    private readonly Dictionary<string, object?> _values = columns.ToDictionary(
        static column => column,
        static _ => (object?)null,
        StringComparer.Ordinal);

    public void Set(string column, object? value)
        => _values[column] = value;

    public void Append(string path)
        => Csv.Append(path, columns, columns.Select(column => _values[column]).ToArray());
}
