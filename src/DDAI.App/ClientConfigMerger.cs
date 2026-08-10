using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DDAI.App;

public enum ClientKind
{
    Claude,
    Gemini,
}

public sealed record ClientConfigTarget(ClientKind Kind, string Path);

public sealed record ClientConfigUpdate(ClientKind Kind, string Path, bool Changed, string? BackupPath);

public sealed class ClientConfigException(string message, Exception? innerException = null) : Exception(message, innerException);

public static class ClientConfigMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static IReadOnlyList<ClientConfigUpdate> Setup(
        IReadOnlyList<ClientConfigTarget> targets,
        string executablePath,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var absoluteExecutable = Path.GetFullPath(executablePath);
        return Apply(targets, timeProvider, (target, root) =>
        {
            var servers = GetServers(root, target.Path, create: true)!;
            var entry = CreateOwnedEntry(target.Kind, absoluteExecutable);
            if (servers.TryGetPropertyValue("ddai", out var existing) && !JsonNode.DeepEquals(existing, entry))
            {
                throw new ClientConfigException($"Refusing to overwrite a foreign or modified mcpServers.ddai entry: {target.Path}");
            }

            servers["ddai"] = entry;
        });
    }

    public static IReadOnlyList<ClientConfigUpdate> Uninstall(
        IReadOnlyList<ClientConfigTarget> targets,
        string executablePath,
        TimeProvider timeProvider) =>
        Apply(targets, timeProvider, (target, root) =>
        {
            var servers = GetServers(root, target.Path, create: false);
            var ownedEntry = CreateOwnedEntry(target.Kind, Path.GetFullPath(executablePath));
            if (servers is not null &&
                servers.TryGetPropertyValue("ddai", out var existing) &&
                JsonNode.DeepEquals(existing, ownedEntry))
            {
                servers.Remove("ddai");
            }
        });

    public static void ValidateSetupOwnership(
        IReadOnlyList<ClientConfigTarget> targets,
        string executablePath)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var absoluteExecutable = Path.GetFullPath(executablePath);
        foreach (var target in targets)
        {
            var path = Path.GetFullPath(target.Path);
            var root = ParseRoot(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            var servers = GetServers(root, path, create: false);
            var ownedEntry = CreateOwnedEntry(target.Kind, absoluteExecutable);
            if (servers is not null &&
                servers.TryGetPropertyValue("ddai", out var existing) &&
                !JsonNode.DeepEquals(existing, ownedEntry))
            {
                throw new ClientConfigException($"Refusing to overwrite a foreign or modified mcpServers.ddai entry: {path}");
            }
        }
    }

    private static IReadOnlyList<ClientConfigUpdate> Apply(
        IReadOnlyList<ClientConfigTarget> targets,
        TimeProvider timeProvider,
        Action<ClientConfigTarget, JsonObject> update)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var plans = new List<ConfigPlan>(targets.Count);
        try
        {
            foreach (var target in targets)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(target.Path);
                var path = Path.GetFullPath(target.Path);
                var originalBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                var root = ParseRoot(path, originalBytes);
                var before = root.DeepClone();
                update(target with { Path = path }, root);
                var changed = !JsonNode.DeepEquals(before, root);
                var replacement = changed ? Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions) + Environment.NewLine) : null;
                plans.Add(new ConfigPlan(target.Kind, path, originalBytes, replacement));
            }
        }
        catch (ClientConfigException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ClientConfigException("Client configuration could not be prepared; no files were changed.", exception);
        }

        if (plans.All(plan => plan.ReplacementBytes is null))
        {
            return plans.Select(plan => new ClientConfigUpdate(plan.Kind, plan.Path, false, null)).ToArray();
        }

        var timestamp = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            foreach (var plan in plans.Where(plan => plan.ReplacementBytes is not null))
            {
                var directory = Path.GetDirectoryName(plan.Path)!;
                Directory.CreateDirectory(directory);
                plan.StagePath = Path.Combine(directory, $".{Path.GetFileName(plan.Path)}.ddai-stage-{Guid.NewGuid():N}.tmp");
                WriteNewFile(plan.StagePath, plan.ReplacementBytes!);
                if (plan.OriginalBytes is not null)
                {
                    plan.BackupPath = $"{plan.Path}.ddai-backup-{timestamp}-{Guid.NewGuid():N}.json";
                    WriteNewFile(plan.BackupPath, plan.OriginalBytes);
                }
            }

            foreach (var plan in plans.Where(plan => plan.ReplacementBytes is not null))
            {
                VerifyUnchanged(plan);
                File.Move(plan.StagePath!, plan.Path, overwrite: true);
                plan.StagePath = null;
                plan.Applied = true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RollBack(plans);
            throw new ClientConfigException("Client configuration update failed and was rolled back.", exception);
        }
        finally
        {
            foreach (var stage in plans.Select(plan => plan.StagePath).Where(path => path is not null))
            {
                File.Delete(stage!);
            }
        }

        return plans.Select(plan => new ClientConfigUpdate(
            plan.Kind,
            plan.Path,
            plan.ReplacementBytes is not null,
            plan.BackupPath)).ToArray();
    }

    private static JsonObject ParseRoot(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            return new JsonObject();
        }

        try
        {
            var node = JsonNode.Parse(bytes) ?? throw new JsonException("The root JSON value is null.");
            return node as JsonObject ?? throw new JsonException("The client configuration root must be an object.");
        }
        catch (JsonException exception)
        {
            throw new ClientConfigException($"Client configuration is invalid JSON and was not changed: {path}", exception);
        }
    }

    private static JsonObject? GetServers(JsonObject root, string path, bool create)
    {
        if (root["mcpServers"] is null)
        {
            if (!create)
            {
                return null;
            }

            var created = new JsonObject();
            root["mcpServers"] = created;
            return created;
        }

        return root["mcpServers"] as JsonObject
            ?? throw new ClientConfigException($"mcpServers must be a JSON object and was not changed: {path}");
    }

    private static JsonObject CreateOwnedEntry(ClientKind kind, string absoluteExecutable)
    {
        var entry = new JsonObject
        {
            ["command"] = absoluteExecutable,
            ["args"] = new JsonArray("serve", "--stdio"),
        };
        if (kind == ClientKind.Gemini)
        {
            entry["trust"] = false;
        }

        return entry;
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void VerifyUnchanged(ConfigPlan plan)
    {
        if (plan.OriginalBytes is null)
        {
            if (File.Exists(plan.Path))
            {
                throw new IOException($"Client configuration appeared during the transaction: {plan.Path}");
            }

            return;
        }

        if (!File.Exists(plan.Path) || !File.ReadAllBytes(plan.Path).AsSpan().SequenceEqual(plan.OriginalBytes))
        {
            throw new IOException($"Client configuration changed during the transaction: {plan.Path}");
        }
    }

    private static void RollBack(IEnumerable<ConfigPlan> plans)
    {
        foreach (var plan in plans.Where(plan => plan.Applied).Reverse())
        {
            if (plan.OriginalBytes is null)
            {
                File.Delete(plan.Path);
            }
            else
            {
                var rollback = Path.Combine(Path.GetDirectoryName(plan.Path)!, $".{Path.GetFileName(plan.Path)}.ddai-rollback-{Guid.NewGuid():N}.tmp");
                WriteNewFile(rollback, plan.OriginalBytes);
                File.Move(rollback, plan.Path, overwrite: true);
            }
        }

        foreach (var backup in plans.Select(plan => plan.BackupPath).Where(path => path is not null))
        {
            File.Delete(backup!);
        }
    }

    private sealed class ConfigPlan(ClientKind kind, string path, byte[]? originalBytes, byte[]? replacementBytes)
    {
        public ClientKind Kind { get; } = kind;
        public string Path { get; } = path;
        public byte[]? OriginalBytes { get; } = originalBytes;
        public byte[]? ReplacementBytes { get; } = replacementBytes;
        public string? StagePath { get; set; }
        public string? BackupPath { get; set; }
        public bool Applied { get; set; }
    }
}
