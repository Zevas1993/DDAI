using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DDAI.App;

public sealed record LocalSetupPaths(
    string SourceExecutable,
    string SourceModDirectory,
    string InstallRoot,
    string ModsDirectory,
    string DungeondraftUserDataDirectory,
    string ClaudeConfigPath,
    string GeminiConfigPath)
{
    public string InstalledExecutable => Path.Combine(Path.GetFullPath(InstallRoot), "ddai.exe");
    public string MetadataPath => Path.Combine(Path.GetFullPath(InstallRoot), "install-metadata.json");
    public string InstalledModDirectory => Path.Combine(Path.GetFullPath(ModsDirectory), "DDAI");
    public IReadOnlyList<ClientConfigTarget> ConfigTargets =>
    [
        new(ClientKind.Claude, Path.GetFullPath(ClaudeConfigPath)),
        new(ClientKind.Gemini, Path.GetFullPath(GeminiConfigPath)),
    ];
}

public sealed record LocalSetupResult(
    string State,
    string Code,
    string InstalledExecutable,
    string ModPath,
    IReadOnlyList<ClientConfigUpdate> ConfigUpdates);

public sealed record LocalDiagnosisResult(
    string State,
    string Code,
    string InstalledExecutable,
    string ModPath,
    string Message);

public sealed record LocalUninstallResult(
    string State,
    string Code,
    string InstalledExecutable,
    string ModPath,
    IReadOnlyList<ClientConfigUpdate> ConfigUpdates);

public sealed class LocalSetupException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class LocalSetupService(TimeProvider timeProvider)
{
    private const string Owner = "org.ddai.connector";
    private const string ModUniqueId = "org.ddai.status_bridge";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public LocalSetupResult Setup(LocalSetupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateSetupInputs(paths);
        var connectorState = InstallConnector(paths);
        var modState = InstallMod(paths);
        var configs = ClientConfigMerger.Setup(paths.ConfigTargets, paths.InstalledExecutable, timeProvider);
        var state = connectorState == "installed" ? "installed"
            : connectorState == "repaired" || modState == "repaired" ? "repaired"
            : configs.Any(update => update.Changed) || modState == "installed" ? "installed"
            : "already_current";
        return new LocalSetupResult(
            state,
            state == "already_current" ? "setup_already_current" : "setup_complete",
            paths.InstalledExecutable,
            paths.InstalledModDirectory,
            configs);
    }

    public LocalDiagnosisResult Diagnose(LocalSetupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!IsOwnedConnector(paths))
        {
            return new LocalDiagnosisResult(
                "missing",
                "connector_missing_or_unowned",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "The DDAI executable installation is missing or its ownership metadata is invalid.");
        }

        if (!Directory.Exists(paths.InstalledModDirectory) || !IsOwnedMod(paths.InstalledModDirectory))
        {
            return new LocalDiagnosisResult(
                "missing",
                "mod_missing_or_unowned",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "The DDAI mod is not installed in the configured per-user custom Mods root.");
        }

        if (HasFreshHeartbeat(paths.DungeondraftUserDataDirectory))
        {
            return new LocalDiagnosisResult(
                "running",
                "runtime_heartbeat_fresh",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "Dungeondraft has loaded the DDAI mod and its heartbeat is fresh.");
        }

        return new LocalDiagnosisResult(
            "installed_not_observed",
            "mods_directory_not_selected_or_mod_disabled",
            paths.InstalledExecutable,
            paths.InstalledModDirectory,
            "Select this custom Mods directory in Dungeondraft, enable DDAI, and reload through the normal UI. DDAI did not change the running application.");
    }

    public LocalUninstallResult Uninstall(LocalSetupPaths paths, string currentExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateOwnedTargetsForUninstall(paths);
        var configs = ClientConfigMerger.Uninstall(paths.ConfigTargets, timeProvider);

        if (Directory.Exists(paths.InstalledModDirectory))
        {
            Directory.Delete(paths.InstalledModDirectory, recursive: true);
        }

        var isRunningInstalledExecutable = !string.IsNullOrWhiteSpace(currentExecutablePath) &&
            Path.GetFullPath(currentExecutablePath).Equals(
                Path.GetFullPath(paths.InstalledExecutable),
                StringComparison.OrdinalIgnoreCase);
        if (isRunningInstalledExecutable)
        {
            return new LocalUninstallResult(
                "partial",
                "running_executable_retained",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                configs);
        }

        if (File.Exists(paths.InstalledExecutable))
        {
            File.Delete(paths.InstalledExecutable);
        }

        if (File.Exists(paths.MetadataPath))
        {
            File.Delete(paths.MetadataPath);
        }

        return new LocalUninstallResult(
            "uninstalled",
            "uninstall_complete",
            paths.InstalledExecutable,
            paths.InstalledModDirectory,
            configs);
    }

    private static void ValidateSetupInputs(LocalSetupPaths paths)
    {
        if (!File.Exists(paths.SourceExecutable))
        {
            throw new LocalSetupException($"Source executable is missing: {paths.SourceExecutable}");
        }

        ValidateModManifest(paths.SourceModDirectory);
        if (Directory.Exists(paths.InstalledModDirectory))
        {
            ValidateModManifest(paths.InstalledModDirectory);
        }

        foreach (var target in paths.ConfigTargets)
        {
            if (!File.Exists(target.Path))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllBytes(target.Path)) as JsonObject
                    ?? throw new JsonException("Configuration root must be an object.");
                if (root["mcpServers"] is not null && root["mcpServers"] is not JsonObject)
                {
                    throw new JsonException("mcpServers must be an object.");
                }
            }
            catch (JsonException exception)
            {
                throw new ClientConfigException($"Client configuration is invalid JSON and no setup files were installed: {target.Path}", exception);
            }
        }
    }

    private string InstallConnector(LocalSetupPaths paths)
    {
        var targetExists = File.Exists(paths.InstalledExecutable);
        var metadataExists = File.Exists(paths.MetadataPath);
        if (targetExists && !metadataExists)
        {
            throw new LocalSetupException($"Refusing to overwrite an executable without DDAI ownership metadata: {paths.InstalledExecutable}");
        }

        if (metadataExists && !ReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable))
        {
            throw new LocalSetupException($"Refusing to modify invalid or foreign installation metadata: {paths.MetadataPath}");
        }

        if (targetExists && FilesEqual(paths.SourceExecutable, paths.InstalledExecutable))
        {
            return "already_current";
        }

        Directory.CreateDirectory(paths.InstallRoot);
        CopyFileAtomically(paths.SourceExecutable, paths.InstalledExecutable);
        var metadata = new InstallMetadata(Owner, "0.1.0", Path.GetFullPath(paths.InstalledExecutable), timeProvider.GetUtcNow());
        WriteJsonAtomically(paths.MetadataPath, metadata);
        return targetExists || metadataExists ? "repaired" : "installed";
    }

    private string InstallMod(LocalSetupPaths paths)
    {
        var source = Path.GetFullPath(paths.SourceModDirectory);
        var target = Path.GetFullPath(paths.InstalledModDirectory);
        Directory.CreateDirectory(paths.ModsDirectory);
        if (Directory.Exists(target))
        {
            ValidateModManifest(target);
            if (DirectoriesEqual(source, target))
            {
                return "already_current";
            }
        }

        var stage = Path.Combine(paths.ModsDirectory, $".DDAI-stage-{Guid.NewGuid():N}");
        CopyDirectory(source, stage);
        if (!Directory.Exists(target))
        {
            Directory.Move(stage, target);
            return "installed";
        }

        var backupRoot = Path.Combine(paths.DungeondraftUserDataDirectory, "ddai", "mod-backups");
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(
            backupRoot,
            $"DDAI-{timeProvider.GetUtcNow().UtcDateTime:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}");
        Directory.Move(target, backup);
        try
        {
            Directory.Move(stage, target);
        }
        catch
        {
            Directory.Move(backup, target);
            throw;
        }

        return "repaired";
    }

    private static void ValidateOwnedTargetsForUninstall(LocalSetupPaths paths)
    {
        if (File.Exists(paths.InstalledExecutable) || File.Exists(paths.MetadataPath))
        {
            if (!File.Exists(paths.InstalledExecutable) ||
                !File.Exists(paths.MetadataPath) ||
                !ReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable))
            {
                throw new LocalSetupException("Refusing to delete an executable whose DDAI ownership cannot be proven.");
            }
        }

        if (Directory.Exists(paths.InstalledModDirectory))
        {
            ValidateModManifest(paths.InstalledModDirectory);
        }
    }

    private static bool IsOwnedConnector(LocalSetupPaths paths) =>
        File.Exists(paths.InstalledExecutable) &&
        File.Exists(paths.MetadataPath) &&
        ReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable);

    private static bool ReadOwnedMetadata(string metadataPath, string executablePath)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<InstallMetadata>(File.ReadAllBytes(metadataPath), JsonOptions);
            return metadata is not null &&
                metadata.Owner == Owner &&
                Path.GetFullPath(metadata.ExecutablePath).Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool HasFreshHeartbeat(string userDataDirectory)
    {
        var heartbeatDirectory = Path.Combine(userDataDirectory, "ddai", "runtime-heartbeats");
        for (var slot = 0; slot < 8; slot++)
        {
            var path = Path.Combine(heartbeatDirectory, $"heartbeat-slot-{slot}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllBytes(path));
                var timestamp = root?["timestamp"]?.GetValue<DateTimeOffset>();
                if (root?["schema_version"]?.GetValue<string>() == "1.0" &&
                    root?["mod_version"]?.GetValue<string>() == "0.1.0" &&
                    !string.IsNullOrWhiteSpace(root?["session_id"]?.GetValue<string>()) &&
                    timestamp is not null &&
                    timestamp <= timeProvider.GetUtcNow().AddSeconds(5) &&
                    timestamp >= timeProvider.GetUtcNow().AddSeconds(-30))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            {
            }
        }

        return false;
    }

    private static bool IsOwnedMod(string directory)
    {
        try
        {
            ValidateModManifest(directory);
            return true;
        }
        catch (LocalSetupException)
        {
            return false;
        }
    }

    private static void ValidateModManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, "ddai_bridge.ddmod");
        if (!File.Exists(manifestPath))
        {
            throw new LocalSetupException($"DDAI mod manifest is missing: {manifestPath}");
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllBytes(manifestPath));
            if (root?["unique_id"]?.GetValue<string>() != ModUniqueId ||
                root?["dd_version"]?.GetValue<string>() != "1.2.0.1")
            {
                throw new LocalSetupException($"Mod manifest is not the owned DDAI status bridge: {manifestPath}");
            }
        }
        catch (JsonException exception)
        {
            throw new LocalSetupException($"DDAI mod manifest is invalid JSON: {manifestPath}", exception);
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        return SHA256.HashData(File.ReadAllBytes(left)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(right)));
    }

    private static bool DirectoriesEqual(string source, string target)
    {
        var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var targetFiles = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(target, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return sourceFiles.SequenceEqual(targetFiles, StringComparer.Ordinal) &&
            sourceFiles.All(relative => FilesEqual(Path.Combine(source, relative), Path.Combine(target, relative)));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination, overwrite: false);
        }
    }

    private static void CopyFileAtomically(string source, string target)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void WriteJsonAtomically<T>(string target, T value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record InstallMetadata(string Owner, string Version, string ExecutablePath, DateTimeOffset InstalledAt);
}
