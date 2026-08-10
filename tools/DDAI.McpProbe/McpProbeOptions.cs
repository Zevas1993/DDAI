using System.Globalization;

namespace DDAI.McpProbe;

public sealed record McpProbeOptions(
    string ExecutablePath,
    string MailboxRoot,
    TimeSpan Timeout,
    string ToolName,
    string? PlanFilePath)
{
    public static McpProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? executable = null;
        string? mailboxRoot = null;
        string? timeoutText = null;
        string? toolName = null;
        string? planFile = null;

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
                case "--tool":
                    toolName = value;
                    break;
                case "--plan-file":
                    planFile = value;
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

        toolName ??= "ddai_status";
        if (toolName is not "ddai_status" and not "ddai_validate_plan" and not "ddai_apply_plan")
        {
            throw new ArgumentException("--tool must be ddai_status, ddai_validate_plan, or ddai_apply_plan.", nameof(args));
        }

        if (toolName == "ddai_status")
        {
            if (planFile is not null)
            {
                throw new ArgumentException("--plan-file cannot be used with ddai_status.", nameof(args));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(planFile) || !Path.IsPathFullyQualified(planFile))
            {
                throw new ArgumentException("Plan tools require --plan-file with an absolute path.", nameof(args));
            }

            planFile = Path.GetFullPath(planFile);
            if (!File.Exists(planFile))
            {
                throw new ArgumentException("--plan-file must identify an existing regular file.", nameof(args));
            }
        }

        return new McpProbeOptions(
            executable,
            Path.GetFullPath(mailboxRoot),
            TimeSpan.FromMilliseconds(timeoutMs),
            toolName,
            planFile);
    }
}
