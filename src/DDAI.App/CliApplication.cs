using System.Text.Json;
using DDAI.Core.Mailbox;

namespace DDAI.App;

public sealed class CliApplication(TextWriter stdout, TextWriter stderr, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = CliParser.Parse(args);
            if (options.Command == DdaiCommand.Serve)
            {
                await McpStdioServer.RunAsync(options, timeProvider, cancellationToken);
                return 0;
            }

            if (options.Command == DdaiCommand.Status)
            {
                var status = await new DdaiStatusService(
                    new AtomicMailbox(options.MailboxRoot),
                    timeProvider).GetStatusAsync(options.Timeout, cancellationToken);
                await WriteJsonAsync(status);
                return status.Success ? 0 : 2;
            }

            var setupService = new LocalSetupService(timeProvider);
            if (options.Command == DdaiCommand.Setup)
            {
                await WriteJsonAsync(setupService.Setup(options.SetupPaths));
                return 0;
            }

            if (options.Command == DdaiCommand.Diagnose)
            {
                var diagnosis = setupService.Diagnose(options.SetupPaths);
                await WriteJsonAsync(diagnosis);
                return diagnosis.State == "running" ? 0 : 2;
            }

            var uninstall = setupService.Uninstall(options.SetupPaths, Environment.ProcessPath ?? string.Empty);
            await WriteJsonAsync(uninstall);
            return uninstall.State == "partial" ? 3 : 0;
        }
        catch (CliUsageException exception)
        {
            await stderr.WriteLineAsync(exception.Message);
            return 64;
        }
        catch (Exception exception) when (exception is ClientConfigException or LocalSetupException or IOException or UnauthorizedAccessException)
        {
            await WriteJsonAsync(new { state = "error", code = "operation_failed", message = exception.Message });
            await stderr.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private Task WriteJsonAsync<T>(T value) => stdout.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
}
