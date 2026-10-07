namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

internal static class CsvReader
{
    internal static IEnumerable<string[]> Read(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        bool header = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (header)
            {
                header = false;
                continue;
            }
            yield return ParseRow(line);
        }
    }

    internal static string[] ParseRow(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (quoted)
            {
                if (current == '"' && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (current == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(current);
                }
            }
            else if (current == '"')
            {
                quoted = true;
            }
            else if (current == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(current);
            }
        }
        if (quoted)
            throw new InvalidDataException("Unclosed quoted CSV field.");
        fields.Add(field.ToString());
        return fields.ToArray();
    }
}
