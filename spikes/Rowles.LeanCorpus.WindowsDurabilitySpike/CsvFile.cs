using System.Globalization;
using System.Text;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike;

internal sealed class CsvFile : IDisposable
{
    private readonly StreamWriter _writer;

    internal CsvFile(string path, params string[] headers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WriteRow(headers);
    }

    internal void WriteRow(params object?[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i != 0)
                _writer.Write(',');
            string value = Convert.ToString(values[i], CultureInfo.InvariantCulture) ?? string.Empty;
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                _writer.Write('"');
                _writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
                _writer.Write('"');
            }
            else
            {
                _writer.Write(value);
            }
        }
        _writer.WriteLine();
    }

    public void Dispose() => _writer.Dispose();
}
