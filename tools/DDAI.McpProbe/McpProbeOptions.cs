using System.Globalization;

namespace DDAI.McpProbe;

public sealed record McpProbeOptions(
    string ExecutablePath,
    string MailboxRoot,
    TimeSpan Timeout)
{
    public static McpProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? executable = null;
        string? mailboxRoot = null;
        string? timeoutText = null;

        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (!seen.Add(name))
            {
                throw new ArgumentException($"Option '{name}' may be specified only once.", nameof(args));
            }

            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"Option '{name}' requires a value.", nameof(args));
            }

            var value = args[index + 1];
            switch (name)
            {
                case "--exe":
                    executable = value;
                    break;
                case "--mailbox-root":
                    mailboxRoot = value;
                    break;
                case "--timeout-ms":
                    timeoutText = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.", nameof(args));
            }
        }

        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("--exe must be an absolute path.", nameof(args));
        }

        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable))
        {
            throw new ArgumentException("--exe must identify an existing regular file.", nameof(args));
        }

        if (string.IsNullOrWhiteSpace(mailboxRoot) || !Path.IsPathFullyQualified(mailboxRoot))
        {
            throw new ArgumentException("--mailbox-root must be an absolute path.", nameof(args));
        }

        if (!int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutMs) || timeoutMs <= 0)
        {
            throw new ArgumentException("--timeout-ms must be a positive integer.", nameof(args));
        }

        return new McpProbeOptions(
            executable,
            Path.GetFullPath(mailboxRoot),
            TimeSpan.FromMilliseconds(timeoutMs));
    }
}
