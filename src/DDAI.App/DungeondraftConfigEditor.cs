using System.Text;

namespace DDAI.App;

public sealed record DungeondraftConfigOwnership(
    string ManagedModsDirectory,
    bool PreviousModsDirectoryPresent,
    string? PreviousModsDirectoryLiteral,
    string ModId,
    string? PreviousModsDirectory = null);

public sealed record DungeondraftConfigEdit(
    byte[] OriginalBytes,
    byte[] ReplacementBytes,
    DungeondraftConfigOwnership Ownership)
{
    public bool Changed => !OriginalBytes.AsSpan().SequenceEqual(ReplacementBytes);
}

public sealed class DungeondraftConfigException(string message, Exception? inner = null)
    : Exception(message, inner);

public static class DungeondraftConfigEditor
{
    public const string DdaiModId = "org.ddai.status_bridge";
    public const string CustomSnapModId = "Lievven.Snappy_Mod";

    public static DungeondraftConfigEdit PlanSetup(byte[] originalBytes, string managedModsDirectory) =>
        PlanSetup(originalBytes, managedModsDirectory, [DdaiModId]);

    public static DungeondraftConfigEdit PlanSetup(
        byte[] originalBytes,
        string managedModsDirectory,
        IReadOnlyList<string> requiredModIds)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        if (requiredModIds is null)
        {
            throw new DungeondraftConfigException("At least one required Dungeondraft mod ID is required.");
        }

        var required = new List<string>(requiredModIds.Count);
        var requiredSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modId in requiredModIds)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                throw new DungeondraftConfigException("Required Dungeondraft mod IDs cannot be blank.");
            }

            if (requiredSet.Add(modId))
            {
                required.Add(modId);
            }
        }

        if (required.Count == 0)
        {
            throw new DungeondraftConfigException("At least one required Dungeondraft mod ID is required.");
        }

        var managedPath = NormalizeAbsolutePath(managedModsDirectory);
        var document = ConfigDocument.Parse(originalBytes);
        var mods = document.FindModsSection();

        string? previousLiteral = null;
        string? previousDirectory = null;
        var previousPresent = false;

        if (mods is null)
        {
            document.AppendModsSection(
                FormatArray(required),
                FormatString(managedPath));
        }
        else
        {
            var active = document.FindOwnedKey(mods.Value, "active_mods");
            var directory = document.FindOwnedKey(mods.Value, "mods_directory");

            if (active is null)
            {
                document.InsertOwnedKey(mods.Value, "active_mods", FormatArray(required));
            }
            else
            {
                var values = ParseStringArray(active.Value.Value);
                var result = new List<string>(values.Count + required.Count);
                var seenRequired = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values)
                {
                    if (!requiredSet.Contains(value))
                    {
                        result.Add(value);
                    }
                    else if (seenRequired.Add(value))
                    {
                        result.Add(value);
                    }
                }

                foreach (var modId in required)
                {
                    if (seenRequired.Add(modId))
                    {
                        result.Add(modId);
                    }
                }

                document.ReplaceValue(active.Value, FormatArray(result));
            }

            if (directory is null)
            {
                document.InsertOwnedKey(document.FindModsSection()!.Value, "mods_directory", FormatString(managedPath));
            }
            else
            {
                previousPresent = true;
                previousLiteral = directory.Value.Value;
                previousDirectory = ParseString(directory.Value.Value);
                document.ReplaceValue(directory.Value, FormatString(managedPath));
            }
        }

        var ownership = new DungeondraftConfigOwnership(
            managedPath,
            previousPresent,
            previousLiteral,
            DdaiModId,
            previousDirectory);
        return document.CreateEdit(originalBytes, ownership);
    }

    public static DungeondraftConfigEdit PlanUninstall(
        byte[] originalBytes,
        DungeondraftConfigOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(ownership);
        var managedPath = NormalizeAbsolutePath(ownership.ManagedModsDirectory);
        var document = ConfigDocument.Parse(originalBytes);
        var mods = document.FindModsSection();
        if (mods is null)
        {
            return document.CreateEdit(originalBytes, ownership);
        }

        var active = document.FindOwnedKey(mods.Value, "active_mods");
        var directory = document.FindOwnedKey(mods.Value, "mods_directory");
        if (active is not null)
        {
            var values = ParseStringArray(active.Value.Value)
                .Where(value => !string.Equals(value, ownership.ModId, StringComparison.Ordinal))
                .ToArray();
            document.ReplaceValue(active.Value, FormatArray(values));
        }

        if (directory is not null && PathsEqual(ParseString(directory.Value.Value), managedPath))
        {
            if (ownership.PreviousModsDirectoryPresent)
            {
                if (ownership.PreviousModsDirectoryLiteral is null)
                {
                    throw new DungeondraftConfigException("The ownership receipt is missing the previous mods directory literal.");
                }

                _ = ParseString(ownership.PreviousModsDirectoryLiteral);
                document.ReplaceValue(directory.Value, ownership.PreviousModsDirectoryLiteral);
            }
            else
            {
                document.RemoveLine(directory.Value.LineIndex);
            }
        }

        return document.CreateEdit(originalBytes, ownership);
    }

    public static bool IsConfigured(byte[] bytes, string managedModsDirectory) =>
        IsConfigured(bytes, managedModsDirectory, [DdaiModId]);

    public static bool IsConfigured(
        byte[] bytes,
        string managedModsDirectory,
        IReadOnlyList<string> requiredModIds)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (requiredModIds is null || requiredModIds.Count == 0 ||
            requiredModIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new DungeondraftConfigException("At least one nonblank required Dungeondraft mod ID is required.");
        }

        var managedPath = NormalizeAbsolutePath(managedModsDirectory);
        var document = ConfigDocument.Parse(bytes);
        var mods = document.FindModsSection();
        if (mods is null)
        {
            return false;
        }

        var active = document.FindOwnedKey(mods.Value, "active_mods");
        var directory = document.FindOwnedKey(mods.Value, "mods_directory");
        var activeIds = active is null ? [] : ParseStringArray(active.Value.Value);
        return active is not null
            && directory is not null
            && requiredModIds.Distinct(StringComparer.Ordinal)
                .All(required => activeIds.Count(value => string.Equals(value, required, StringComparison.Ordinal)) == 1)
            && PathsEqual(ParseString(directory.Value.Value), managedPath);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new DungeondraftConfigException("The managed mods directory must be an absolute path.");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DungeondraftConfigException("The managed mods directory is invalid.", exception);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            if (!Path.IsPathFullyQualified(left))
            {
                return false;
            }

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string FormatArray(IEnumerable<string> values) =>
        "[ " + string.Join(", ", values.Select(FormatString)) + " ]";

    private static string FormatString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static IReadOnlyList<string> ParseStringArray(string literal)
    {
        var parser = new LiteralParser(literal);
        return parser.ParseArray();
    }

    private static string ParseString(string literal)
    {
        var parser = new LiteralParser(literal);
        return parser.ParseSingleString();
    }

    private readonly record struct Section(int Start, int End);

    private readonly record struct OwnedValue(int LineIndex, int ValueStart, int ValueLength, string Value);

    private sealed class ConfigDocument
    {
        private readonly Encoding encoding;
        private readonly byte[] preamble;
        private readonly string newline;
        private readonly List<string> lines;

        private ConfigDocument(Encoding encoding, byte[] preamble, string newline, List<string> lines)
        {
            this.encoding = encoding;
            this.preamble = preamble;
            this.newline = newline;
            this.lines = lines;
        }

        public static ConfigDocument Parse(byte[] bytes)
        {
            var (encoding, preambleLength) = DetectEncoding(bytes);
            string text;
            try
            {
                text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            }
            catch (DecoderFallbackException exception)
            {
                throw new DungeondraftConfigException("Dungeondraft config.ini has an invalid text encoding.", exception);
            }

            if (text.Contains('\0'))
            {
                throw new DungeondraftConfigException("Dungeondraft config.ini contains a NUL character.");
            }

            var hasCrLf = text.Contains("\r\n", StringComparison.Ordinal);
            var withoutCrLf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
            if (withoutCrLf.Contains('\r'))
            {
                throw new DungeondraftConfigException("Dungeondraft config.ini contains a bare carriage return.");
            }

            var hasLf = withoutCrLf.Contains('\n');
            if (hasCrLf && hasLf)
            {
                throw new DungeondraftConfigException("Dungeondraft config.ini mixes CRLF and LF newlines.");
            }

            var newline = hasCrLf ? "\r\n" : "\n";
            var lines = text.Split(newline, StringSplitOptions.None).ToList();
            var document = new ConfigDocument(
                encoding,
                bytes.AsSpan(0, preambleLength).ToArray(),
                newline,
                lines);

            _ = document.FindModsSection();
            return document;
        }

        public Section? FindModsSection()
        {
            Section? found = null;
            for (var index = 0; index < lines.Count; index++)
            {
                if (!TryParseSection(lines[index], out var name))
                {
                    continue;
                }

                if (!string.Equals(name, "Mods", StringComparison.Ordinal))
                {
                    continue;
                }

                if (found is not null)
                {
                    throw new DungeondraftConfigException("Dungeondraft config.ini contains multiple [Mods] sections.");
                }

                var end = lines.Count;
                for (var next = index + 1; next < lines.Count; next++)
                {
                    if (TryParseSection(lines[next], out _))
                    {
                        end = next;
                        break;
                    }
                }

                found = new Section(index, end);
            }

            return found;
        }

        public OwnedValue? FindOwnedKey(Section section, string key)
        {
            OwnedValue? found = null;
            for (var index = section.Start + 1; index < section.End; index++)
            {
                var line = lines[index];
                var equals = line.IndexOf('=');
                if (equals < 0 || !string.Equals(line[..equals].Trim(), key, StringComparison.Ordinal))
                {
                    continue;
                }

                if (found is not null)
                {
                    throw new DungeondraftConfigException($"Dungeondraft config.ini contains duplicate {key} keys in [Mods].");
                }

                var start = equals + 1;
                while (start < line.Length && char.IsWhiteSpace(line[start]))
                {
                    start++;
                }

                var end = line.Length;
                while (end > start && char.IsWhiteSpace(line[end - 1]))
                {
                    end--;
                }

                var value = line[start..end];
                if (key == "active_mods")
                {
                    _ = ParseStringArray(value);
                }
                else
                {
                    _ = ParseString(value);
                }

                found = new OwnedValue(index, start, end - start, value);
            }

            return found;
        }

        public void ReplaceValue(OwnedValue owned, string replacement)
        {
            var line = lines[owned.LineIndex];
            lines[owned.LineIndex] = line[..owned.ValueStart] + replacement + line[(owned.ValueStart + owned.ValueLength)..];
        }

        public void InsertOwnedKey(Section section, string key, string value)
        {
            var index = section.End;
            if (index == lines.Count && lines.Count > 0 && lines[^1].Length == 0)
            {
                index--;
            }

            lines.Insert(index, $"{key}={value}");
        }

        public void RemoveLine(int lineIndex) => lines.RemoveAt(lineIndex);

        public void AppendModsSection(string activeMods, string modsDirectory)
        {
            if (lines.Count == 1 && lines[0].Length == 0)
            {
                lines.Clear();
            }

            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
                lines.Add(string.Empty);
            }
            else if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("[Mods]");
            lines.Add($"active_mods={activeMods}");
            lines.Add($"mods_directory={modsDirectory}");
            lines.Add(string.Empty);
        }

        public DungeondraftConfigEdit CreateEdit(byte[] originalBytes, DungeondraftConfigOwnership ownership)
        {
            var text = string.Join(newline, lines);
            var body = encoding.GetBytes(text);
            var replacement = new byte[preamble.Length + body.Length];
            preamble.CopyTo(replacement, 0);
            body.CopyTo(replacement, preamble.Length);
            return new DungeondraftConfigEdit(originalBytes.ToArray(), replacement, ownership);
        }

        private static bool TryParseSection(string line, out string name)
        {
            var trimmed = line.Trim();
            if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                name = trimmed[1..^1];
                return true;
            }

            name = string.Empty;
            return false;
        }

        private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return (new UTF8Encoding(false, true), 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return (new UnicodeEncoding(false, false, true), 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return (new UnicodeEncoding(true, false, true), 2);
            }

            return (new UTF8Encoding(false, true), 0);
        }
    }

    private sealed class LiteralParser(string text)
    {
        private int index;

        public IReadOnlyList<string> ParseArray()
        {
            SkipWhitespace();
            Expect('[');
            SkipWhitespace();
            var values = new List<string>();
            if (TryConsume(']'))
            {
                RequireEnd();
                return values;
            }

            while (true)
            {
                values.Add(ReadString());
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    RequireEnd();
                    return values;
                }

                Expect(',');
                SkipWhitespace();
                if (index >= text.Length || text[index] == ']')
                {
                    throw InvalidLiteral();
                }
            }
        }

        public string ParseSingleString()
        {
            SkipWhitespace();
            var value = ReadString();
            SkipWhitespace();
            RequireEnd();
            return value;
        }

        private string ReadString()
        {
            Expect('\"');
            var result = new StringBuilder();
            while (index < text.Length)
            {
                var current = text[index++];
                if (current == '\"')
                {
                    return result.ToString();
                }

                if (current < ' ')
                {
                    throw InvalidLiteral();
                }

                if (current != '\\')
                {
                    result.Append(current);
                    continue;
                }

                if (index >= text.Length)
                {
                    throw InvalidLiteral();
                }

                result.Append(text[index++] switch
                {
                    '\\' => '\\',
                    '\"' => '\"',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    _ => throw InvalidLiteral(),
                });
            }

            throw InvalidLiteral();
        }

        private void SkipWhitespace()
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }

        private void Expect(char expected)
        {
            if (!TryConsume(expected))
            {
                throw InvalidLiteral();
            }
        }

        private bool TryConsume(char value)
        {
            if (index < text.Length && text[index] == value)
            {
                index++;
                return true;
            }

            return false;
        }

        private void RequireEnd()
        {
            if (index != text.Length)
            {
                throw InvalidLiteral();
            }
        }

        private static DungeondraftConfigException InvalidLiteral() =>
            new("Dungeondraft config.ini contains invalid [Mods] value syntax.");
    }
}
