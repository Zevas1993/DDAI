namespace DDAI.App;

public enum DdaiCommand
{
    Serve,
    Status,
    Setup,
    Diagnose,
    Uninstall,
    AssetHelper,
}

public sealed record CliOptions(
    DdaiCommand Command,
    bool Stdio,
    bool Json,
    string MailboxRoot,
    TimeSpan Timeout,
    string SourceExecutable,
    string SourceModDirectory,
    string InstallRoot,
    string ModsDirectory,
    string DungeondraftUserDataDirectory,
    string ClaudeConfigPath,
    string GeminiConfigPath)
{
    public LocalSetupPaths SetupPaths => new(
        SourceExecutable,
        SourceModDirectory,
        InstallRoot,
        ModsDirectory,
        DungeondraftUserDataDirectory,
        ClaudeConfigPath,
        GeminiConfigPath);
}

public sealed class CliUsageException(string message) : Exception(message);

public static class CliParser
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new CliUsageException("A command is required: serve, status, setup, diagnose, uninstall, or asset-helper.");
        }

        var command = args[0] switch
        {
            "serve" => DdaiCommand.Serve,
            "status" => DdaiCommand.Status,
            "setup" => DdaiCommand.Setup,
            "diagnose" => DdaiCommand.Diagnose,
            "uninstall" => DdaiCommand.Uninstall,
            "asset-helper" => DdaiCommand.AssetHelper,
            _ => throw new CliUsageException($"Unknown command: {args[0]}"),
        };

        var stdio = false;
        var json = false;
        var mailboxRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dungeondraft",
            "ddai");
        var timeout = TimeSpan.FromSeconds(5);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installRoot = Path.Combine(localAppData, "DDAI");
        var sourceExecutable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ddai.exe");
        var sourceModDirectory = Path.Combine(AppContext.BaseDirectory, "mods", "DDAI");
        var modsDirectory = Path.Combine(installRoot, "DungeondraftMods");
        var dungeondraftUserData = Path.Combine(applicationData, "Dungeondraft");
        var claudeConfig = Path.Combine(applicationData, "Claude", "claude_desktop_config.json");
        var geminiConfig = Path.Combine(userProfile, ".gemini", "settings.json");

        for (var index = 1; index < args.Count; index++)
        {
            if (command == DdaiCommand.AssetHelper && args[index] != "--mailbox-root")
            {
                throw new CliUsageException("asset-helper accepts only --mailbox-root.");
            }

            switch (args[index])
            {
                case "--stdio":
                    stdio = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--mailbox-root":
                    mailboxRoot = Path.GetFullPath(NextValue(args, ref index, "--mailbox-root"));
                    break;
                case "--timeout-ms":
                    var text = NextValue(args, ref index, "--timeout-ms");
                    if (!int.TryParse(text, out var milliseconds) || milliseconds <= 0)
                    {
                        throw new CliUsageException("--timeout-ms must be a positive integer.");
                    }

                    timeout = TimeSpan.FromMilliseconds(milliseconds);
                    break;
                case "--source-exe":
                    sourceExecutable = FullPathValue(args, ref index, "--source-exe");
                    break;
                case "--source-mod-directory":
                    sourceModDirectory = FullPathValue(args, ref index, "--source-mod-directory");
                    break;
                case "--install-root":
                    installRoot = FullPathValue(args, ref index, "--install-root");
                    break;
                case "--mods-directory":
                    modsDirectory = FullPathValue(args, ref index, "--mods-directory");
                    break;
                case "--dungeondraft-user-data":
                    dungeondraftUserData = FullPathValue(args, ref index, "--dungeondraft-user-data");
                    break;
                case "--claude-config":
                    claudeConfig = FullPathValue(args, ref index, "--claude-config");
                    break;
                case "--gemini-config":
                    geminiConfig = FullPathValue(args, ref index, "--gemini-config");
                    break;
                default:
                    throw new CliUsageException($"Unknown option: {args[index]}");
            }
        }

        if (command == DdaiCommand.Serve && !stdio)
        {
            throw new CliUsageException("serve requires --stdio.");
        }

        if (command == DdaiCommand.Status && !json)
        {
            throw new CliUsageException("status requires --json.");
        }

        return new CliOptions(
            command,
            stdio,
            json,
            Path.GetFullPath(mailboxRoot),
            timeout,
            Path.GetFullPath(sourceExecutable),
            Path.GetFullPath(sourceModDirectory),
            Path.GetFullPath(installRoot),
            Path.GetFullPath(modsDirectory),
            Path.GetFullPath(dungeondraftUserData),
            Path.GetFullPath(claudeConfig),
            Path.GetFullPath(geminiConfig));
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        index++;
        if (index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new CliUsageException($"{option} requires a value.");
        }

        return args[index];
    }

    private static string FullPathValue(IReadOnlyList<string> args, ref int index, string option) =>
        Path.GetFullPath(NextValue(args, ref index, option));
}
