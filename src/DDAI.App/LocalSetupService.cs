using System.Diagnostics;
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
    public string? AssetHelperRootOverride { get; init; }
    public string InstalledExecutable => Path.Combine(Path.GetFullPath(InstallRoot), "ddai.exe");
    public string MetadataPath => Path.Combine(Path.GetFullPath(InstallRoot), "install-metadata.json");
    public string AssetHelperReceiptPath => Path.Combine(
        Path.GetFullPath(DungeondraftUserDataDirectory),
        "ddai",
        "private",
        "asset-helper.json");
    public string AssetHelperRoot => Path.GetFullPath(AssetHelperRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DDAI",
        "helpers"));
    public string AssetHelperExecutablePath
    {
        get
        {
            var hash = LocalSetupService.HashFile(InstalledExecutable);
            return Path.Combine(AssetHelperRoot, "ddai-" + hash + ".exe");
        }
    }
    public string InstalledModDirectory => Path.Combine(Path.GetFullPath(ModsDirectory), "DDAI");
    public string DungeondraftConfigPath =>
        Path.Combine(Path.GetFullPath(DungeondraftUserDataDirectory), "config.ini");
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
    IReadOnlyList<ClientConfigUpdate> ConfigUpdates,
    DungeondraftConfigUpdate DungeondraftConfig,
    DungeondraftModConsolidationUpdate ModConsolidation);

public sealed record LocalDiagnosisResult(
    string State,
    string Code,
    string InstalledExecutable,
    string ModPath,
    string Message,
    DungeondraftConfigUpdate DungeondraftConfig,
    DungeondraftModConsolidationUpdate ModConsolidation);

public sealed record LocalUninstallResult(
    string State,
    string Code,
    string InstalledExecutable,
    string ModPath,
    IReadOnlyList<ClientConfigUpdate> ConfigUpdates,
    DungeondraftConfigUpdate DungeondraftConfig,
    DungeondraftModConsolidationUpdate ModConsolidation);

public sealed class LocalSetupException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IDungeondraftProcessProbe
{
    bool IsRunning();
}

public sealed class WindowsDungeondraftProcessProbe : IDungeondraftProcessProbe
{
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName("Dungeondraft");
        try
        {
            return processes.Any(process => !process.HasExited);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

public sealed class LocalSetupService
{
    private const string Owner = "org.ddai.connector";
    private const string ModUniqueId = "org.ddai.status_bridge";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
    private readonly TimeProvider timeProvider;
    private readonly IDungeondraftProcessProbe processProbe;

    public LocalSetupService(TimeProvider timeProvider)
        : this(timeProvider, new WindowsDungeondraftProcessProbe())
    {
    }

    public LocalSetupService(TimeProvider timeProvider, IDungeondraftProcessProbe processProbe)
    {
        this.timeProvider = timeProvider;
        this.processProbe = processProbe;
    }

    public LocalSetupResult Setup(LocalSetupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateSetupInputs(paths);
        var previousMetadata = TryReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable);
        var previousConfigReceipt = previousMetadata?.DungeondraftConfig;
        var historicalCustomSnapReceipt = GetCustomSnapReceipt(previousMetadata);
        var transaction = new DungeondraftConfigTransaction(timeProvider);
        DungeondraftConfigTransactionPlan? configPlan = null;
        DungeondraftConfigUpdate configUpdate;
        DungeondraftModConsolidationUpdate consolidationUpdate;
        if (processProbe.IsRunning())
        {
            configUpdate = ConfigUpdate("activation_pending", paths);
            consolidationUpdate = ConsolidationUpdate("deferred_dungeondraft_running");
        }
        else if (!File.Exists(paths.DungeondraftConfigPath))
        {
            configUpdate = ConfigUpdate("activation_pending_config_missing", paths);
            consolidationUpdate = ConsolidationUpdate("not_planned");
        }
        else
        {
            configPlan = transaction.PlanSetup(paths.DungeondraftConfigPath, paths.ModsDirectory);
            configUpdate = ConfigUpdate("planned", paths, configPlan.Ownership);
            consolidationUpdate = ConsolidationUpdate(
                historicalCustomSnapReceipt is null ? "not_required" : "retained_user_content",
                historicalCustomSnapReceipt);
        }

        var connectorState = InstallConnector(paths);
        WriteAssetHelperReceipt(paths);
        var modState = InstallMod(paths);
        var configs = ClientConfigMerger.Setup(paths.ConfigTargets, paths.InstalledExecutable, timeProvider);
        if (configPlan is not null)
        {
            configUpdate = transaction.Apply(configPlan);
            var configReceipt = previousConfigReceipt ?? configPlan.Ownership;
            try
            {
                if (previousMetadata?.DungeondraftConfig != configReceipt)
                {
                    WriteInstallMetadata(paths, configReceipt, previousMetadata?.ConsolidatedMods);
                }
                configUpdate = configUpdate with { Ownership = configReceipt };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                transaction.Restore(configPlan);
                throw new LocalSetupException(
                    "Dungeondraft activation was restored because its ownership receipt could not be persisted.",
                    exception);
            }
        }

        var state = connectorState == "installed" ? "installed"
            : connectorState == "repaired" || modState == "repaired" ? "repaired"
            : configs.Any(update => update.Changed) || modState == "installed" || consolidationUpdate.Changed ? "installed"
            : "already_current";

        if (configUpdate.State is "activation_pending" or "activation_pending_config_missing")
        {
            return new LocalSetupResult(
                "activation_pending",
                configUpdate.State == "activation_pending"
                    ? "activation_pending_dungeondraft_running"
                    : "activation_pending_config_missing",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                configs,
                configUpdate,
                consolidationUpdate);
        }

        return new LocalSetupResult(
            state,
            state == "already_current" ? "setup_already_current" : "setup_complete",
            paths.InstalledExecutable,
            paths.InstalledModDirectory,
            configs,
            configUpdate,
            consolidationUpdate);
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
                "The DDAI executable installation is missing or its ownership metadata is invalid.",
                ConfigUpdate("not_checked", paths),
                ConsolidationUpdate("not_checked"));
        }

        if (!Directory.Exists(paths.InstalledModDirectory) || !IsOwnedMod(paths.InstalledModDirectory))
        {
            return new LocalDiagnosisResult(
                "missing",
                "mod_missing_or_unowned",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "The DDAI mod is not installed in the configured per-user custom Mods root.",
                ConfigUpdate("not_checked", paths),
                ConsolidationUpdate("not_checked"));
        }

        if (HasFreshHeartbeat(paths.DungeondraftUserDataDirectory, paths.InstalledModDirectory))
        {
            return new LocalDiagnosisResult(
                "running",
                "runtime_heartbeat_fresh",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "Dungeondraft has loaded the DDAI mod and its heartbeat is fresh.",
                ConfigUpdate("runtime_observed", paths),
                ConsolidationUpdate("runtime_observed"));
        }

        if (processProbe.IsRunning())
        {
            return new LocalDiagnosisResult(
                "activation_pending",
                "activation_pending_dungeondraft_running",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "Dungeondraft is running without a fresh DDAI heartbeat. Close it normally before retrying setup; DDAI did not change the running application.",
                ConfigUpdate("activation_pending", paths),
                ConsolidationUpdate("deferred_dungeondraft_running"));
        }

        if (!File.Exists(paths.DungeondraftConfigPath))
        {
            return new LocalDiagnosisResult(
                "activation_pending",
                "activation_pending_config_missing",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "Dungeondraft config.ini is missing, so DDAI cannot activate the bridge automatically.",
                ConfigUpdate("activation_pending_config_missing", paths),
                ConsolidationUpdate("not_planned"));
        }

        var metadata = TryReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable);
        var consolidationReceipt = GetCustomSnapReceipt(metadata);
        var currentConsolidation = ConsolidationUpdate(
            consolidationReceipt is null ? "not_required" : "retained_user_content",
            consolidationReceipt);

        bool configured;
        try
        {
            configured = DungeondraftConfigEditor.IsConfigured(
                File.ReadAllBytes(paths.DungeondraftConfigPath),
                paths.ModsDirectory);
        }
        catch (Exception exception) when (exception is DungeondraftConfigException or IOException or UnauthorizedAccessException)
        {
            configured = false;
        }

        if (!configured)
        {
            return new LocalDiagnosisResult(
                "activation_pending",
                "activation_pending_config_mismatch",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                "Dungeondraft config.ini does not safely select the DDAI Mods root and bridge.",
                ConfigUpdate("activation_pending_config_mismatch", paths),
                currentConsolidation);
        }

        return new LocalDiagnosisResult(
            "configured",
            "configured_waiting_for_reload",
            paths.InstalledExecutable,
            paths.InstalledModDirectory,
            "Dungeondraft is configured for DDAI. Launch or reload it normally and wait for the runtime heartbeat.",
            ConfigUpdate("configured_waiting_for_reload", paths),
            currentConsolidation);
    }

    public LocalUninstallResult Uninstall(LocalSetupPaths paths, string currentExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateOwnedTargetsForUninstall(paths);
        if (processProbe.IsRunning())
        {
            return new LocalUninstallResult(
                "partial",
                "dungeondraft_running_configuration_retained",
                paths.InstalledExecutable,
                paths.InstalledModDirectory,
                [],
                ConfigUpdate("retained_dungeondraft_running", paths),
                ConsolidationUpdate("deferred_dungeondraft_running"));
        }

        var metadata = TryReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable);
        var consolidationReceipt = GetCustomSnapReceipt(metadata);
        var consolidationUpdate = ConsolidationUpdate(
            consolidationReceipt is null ? "retained_unproven_user_content" : "retained_user_content",
            consolidationReceipt);
        DungeondraftConfigUpdate configUpdate;
        if (metadata?.DungeondraftConfig is not null && File.Exists(paths.DungeondraftConfigPath))
        {
            var transaction = new DungeondraftConfigTransaction(timeProvider);
            configUpdate = transaction.Apply(
                transaction.PlanUninstall(paths.DungeondraftConfigPath, metadata.DungeondraftConfig));
        }
        else if (!File.Exists(paths.DungeondraftConfigPath))
        {
            configUpdate = ConfigUpdate("config_missing", paths, metadata?.DungeondraftConfig);
        }
        else
        {
            configUpdate = ConfigUpdate("retained_unproven_ownership", paths);
        }

        var configs = ClientConfigMerger.Uninstall(paths.ConfigTargets, paths.InstalledExecutable, timeProvider);

        if (Directory.Exists(paths.InstalledModDirectory))
        {
            Directory.Delete(paths.InstalledModDirectory, recursive: true);
        }

        DeleteOwnedAssetHelperReceipt(paths);

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
                configs,
                configUpdate,
                consolidationUpdate);
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
            configs,
            configUpdate,
            consolidationUpdate);
    }

    private static void ValidateSetupInputs(LocalSetupPaths paths)
    {
        if (!File.Exists(paths.SourceExecutable))
        {
            throw new LocalSetupException($"Source executable is missing: {paths.SourceExecutable}");
        }

        _ = ReadOwnedAssetHelperReceipt(paths);
        ValidateModManifest(paths.SourceModDirectory);
        if (Directory.Exists(paths.InstalledModDirectory))
        {
            ValidateModManifest(paths.InstalledModDirectory);
        }

        ClientConfigMerger.ValidateSetupOwnership(paths.ConfigTargets, paths.InstalledExecutable);

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
        var existingMetadata = metadataExists
            ? TryReadOwnedMetadata(paths.MetadataPath, paths.InstalledExecutable)
            : null;
        if (targetExists && !metadataExists)
        {
            throw new LocalSetupException($"Refusing to overwrite an executable without DDAI ownership metadata: {paths.InstalledExecutable}");
        }

        if (metadataExists && existingMetadata is null)
        {
            throw new LocalSetupException($"Refusing to modify invalid or foreign installation metadata: {paths.MetadataPath}");
        }

        if (targetExists && FilesEqual(paths.SourceExecutable, paths.InstalledExecutable))
        {
            return "already_current";
        }

        Directory.CreateDirectory(paths.InstallRoot);
        CopyFileAtomically(paths.SourceExecutable, paths.InstalledExecutable);
        var metadata = new InstallMetadata(
            Owner,
            "0.1.0",
            Path.GetFullPath(paths.InstalledExecutable),
            timeProvider.GetUtcNow(),
            existingMetadata?.DungeondraftConfig,
            existingMetadata?.ConsolidatedMods);
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
        => TryReadOwnedMetadata(metadataPath, executablePath) is not null;

    private static InstallMetadata? TryReadOwnedMetadata(string metadataPath, string executablePath)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<InstallMetadata>(File.ReadAllBytes(metadataPath), JsonOptions);
            return metadata is not null &&
                metadata.Owner == Owner &&
                Path.GetFullPath(metadata.ExecutablePath).Equals(Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase)
                ? metadata
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private bool HasFreshHeartbeat(string userDataDirectory, string installedModDirectory)
    {
        var installedModVersion = ReadModVersion(installedModDirectory);
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
                    root?["mod_version"]?.GetValue<string>() == installedModVersion &&
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
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new LocalSetupException($"DDAI mod manifest is invalid JSON: {manifestPath}", exception);
        }
    }

    private static string ReadModVersion(string directory)
    {
        var manifestPath = Path.Combine(directory, "ddai_bridge.ddmod");
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(manifestPath))?["version"]?.GetValue<string>() is { Length: > 0 } version
                ? version
                : throw new LocalSetupException($"DDAI mod manifest version is missing: {manifestPath}");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
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

    private static void WriteAssetHelperReceipt(LocalSetupPaths paths)
    {
        ValidateAssetHelperRoot(paths.AssetHelperRoot);
        _ = ReadOwnedAssetHelperReceipt(paths);
        Directory.CreateDirectory(paths.AssetHelperRoot);
        ValidateOrdinaryPath(paths.AssetHelperRoot);
        var executableHash = HashFile(paths.InstalledExecutable);
        var helperPath = Path.Combine(paths.AssetHelperRoot, "ddai-" + executableHash + ".exe");
        if (File.Exists(helperPath))
        {
            if (HashFile(helperPath) != executableHash)
            {
                throw new LocalSetupException("Refusing a conflicting content-addressed asset helper.");
            }
        }
        else
        {
            var temporary = helperPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = new FileStream(paths.InstalledExecutable, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }
                File.Move(temporary, helperPath, overwrite: false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        var receiptPath = paths.AssetHelperReceiptPath;
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        WriteJsonAtomically(
            receiptPath,
            new AssetHelperReceipt(
                "1.0",
                Owner,
                Path.GetFullPath(helperPath),
                executableHash));
    }

    private static AssetHelperReceipt? ReadOwnedAssetHelperReceipt(LocalSetupPaths paths)
    {
        if (!File.Exists(paths.AssetHelperReceiptPath))
        {
            return null;
        }

        AssetHelperReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<AssetHelperReceipt>(
                File.ReadAllBytes(paths.AssetHelperReceiptPath),
                JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new LocalSetupException("Refusing to replace invalid asset-helper ownership metadata.", exception);
        }

        try
        {
            ValidateAssetHelperRoot(paths.AssetHelperRoot);
            var expectedPath = receipt is null || !IsSha256(receipt.Sha256)
                ? string.Empty
                : Path.Combine(paths.AssetHelperRoot, "ddai-" + receipt.Sha256 + ".exe");
            if (receipt is null || receipt.SchemaVersion != "1.0" || receipt.Owner != Owner ||
                !Path.GetFullPath(receipt.ExecutablePath).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(expectedPath) || HashFile(expectedPath) != receipt.Sha256)
            {
                throw new LocalSetupException("Refusing to replace foreign asset-helper ownership metadata.");
            }

            return receipt;
        }
        catch (LocalSetupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new LocalSetupException("Refusing to replace invalid asset-helper ownership metadata.", exception);
        }
    }

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void DeleteOwnedAssetHelperReceipt(LocalSetupPaths paths)
    {
        if (!File.Exists(paths.AssetHelperReceiptPath))
        {
            return;
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<AssetHelperReceipt>(
                File.ReadAllBytes(paths.AssetHelperReceiptPath),
                JsonOptions);
            var expectedPath = receipt is null ? string.Empty : Path.Combine(paths.AssetHelperRoot, "ddai-" + receipt.Sha256 + ".exe");
            if (receipt is not null && receipt.Owner == Owner &&
                Path.GetFullPath(receipt.ExecutablePath).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(paths.AssetHelperReceiptPath);
                if (File.Exists(receipt.ExecutablePath) && HashFile(receipt.ExecutablePath) == receipt.Sha256)
                {
                    File.Delete(receipt.ExecutablePath);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ValidateAssetHelperRoot(string helperRoot)
    {
        var localAppData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(helperRoot));
        if (!root.StartsWith(localAppData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalSetupException("The asset helper root must remain under per-user LocalApplicationData.");
        }
    }

    private static void ValidateOrdinaryPath(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new LocalSetupException("The asset helper path cannot traverse a reparse point.");
            }
        }
    }

    private void WriteInstallMetadata(
        LocalSetupPaths paths,
        DungeondraftConfigOwnership? ownership,
        IReadOnlyList<DungeondraftModConsolidationReceipt>? consolidatedMods)
    {
        var metadata = new InstallMetadata(
            Owner,
            "0.1.0",
            Path.GetFullPath(paths.InstalledExecutable),
            timeProvider.GetUtcNow(),
            ownership,
            consolidatedMods);
        WriteJsonAtomically(paths.MetadataPath, metadata);
    }

    private static DungeondraftConfigUpdate ConfigUpdate(
        string state,
        LocalSetupPaths paths,
        DungeondraftConfigOwnership? ownership = null) =>
        new(state, paths.DungeondraftConfigPath, Changed: false, BackupPath: null, ownership);

    private static DungeondraftModConsolidationUpdate ConsolidationUpdate(
        string state,
        DungeondraftModConsolidationReceipt? receipt = null) =>
        new(state, Changed: false, receipt);

    private static DungeondraftModConsolidationReceipt? GetCustomSnapReceipt(InstallMetadata? metadata)
    {
        if (metadata?.ConsolidatedMods is null || metadata.ConsolidatedMods.Count == 0)
        {
            return null;
        }

        if (metadata.ConsolidatedMods.Count != 1 ||
            metadata.ConsolidatedMods[0].ModId != DungeondraftConfigEditor.CustomSnapModId)
        {
            throw new LocalSetupException("DDAI installation metadata contains ambiguous mod-consolidation ownership.");
        }

        return metadata.ConsolidatedMods[0];
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

    private sealed record InstallMetadata(
        string Owner,
        string Version,
        string ExecutablePath,
        DateTimeOffset InstalledAt,
        DungeondraftConfigOwnership? DungeondraftConfig = null,
        IReadOnlyList<DungeondraftModConsolidationReceipt>? ConsolidatedMods = null);

    private sealed record AssetHelperReceipt(
        string SchemaVersion,
        string Owner,
        string ExecutablePath,
        string Sha256);
}
