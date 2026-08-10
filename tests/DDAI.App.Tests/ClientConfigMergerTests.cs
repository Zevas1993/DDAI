using System.Text.Json.Nodes;
using DDAI.App;

namespace DDAI.App.Tests;

public sealed class ClientConfigMergerTests
{
    [Fact]
    public void Setup_PreservesUnrelatedClaudeAndGeminiContentAndSetsExactOwnedEntries()
    {
        using var sandbox = new ConfigSandbox();
        var claudeOriginal = """
            {
              "theme": "dark",
              "mcpServers": {
                "n8n-docs": { "command": "node", "args": ["docs.js"] },
                "One-Stop-Shop-N8N-MCP": { "command": "python", "env": { "KEEP": "yes" } },
                "MCP_DOCKER": { "command": "docker", "args": ["run", "mcp"] }
              }
            }
            """;
        var geminiOriginal = """
            { "security": { "auth": "keep" }, "mcpServers": { "other": { "command": "other.exe", "trust": true } } }
            """;
        File.WriteAllText(sandbox.ClaudePath, claudeOriginal);
        File.WriteAllText(sandbox.GeminiPath, geminiOriginal);
        var executable = Path.GetFullPath(Path.Combine(sandbox.Root, "install", "ddai.exe"));

        var results = ClientConfigMerger.Setup(sandbox.Targets, executable, new FixedTimeProvider());

        Assert.All(results, result => Assert.True(result.Changed));
        var claude = JsonNode.Parse(File.ReadAllText(sandbox.ClaudePath))!.AsObject();
        var gemini = JsonNode.Parse(File.ReadAllText(sandbox.GeminiPath))!.AsObject();
        Assert.Equal("dark", claude["theme"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(claudeOriginal)!["mcpServers"]!["n8n-docs"],
            claude["mcpServers"]!["n8n-docs"]));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(claudeOriginal)!["mcpServers"]!["One-Stop-Shop-N8N-MCP"],
            claude["mcpServers"]!["One-Stop-Shop-N8N-MCP"]));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(claudeOriginal)!["mcpServers"]!["MCP_DOCKER"],
            claude["mcpServers"]!["MCP_DOCKER"]));
        AssertOwnedEntry(claude["mcpServers"]!["ddai"]!, executable, expectTrust: false, trustPresent: false);
        Assert.Equal("keep", gemini["security"]!["auth"]!.GetValue<string>());
        AssertOwnedEntry(gemini["mcpServers"]!["ddai"]!, executable, expectTrust: false, trustPresent: true);
    }

    [Fact]
    public void Setup_CreatesTimestampedBackupsOnceAndSecondRunIsByteIdempotent()
    {
        using var sandbox = new ConfigSandbox();
        const string original = "{\"mcpServers\":{\"other\":{\"command\":\"keep\"}}}";
        File.WriteAllText(sandbox.ClaudePath, original);
        File.WriteAllText(sandbox.GeminiPath, original);
        var executable = Path.Combine(sandbox.Root, "ddai.exe");

        var first = ClientConfigMerger.Setup(sandbox.Targets, executable, new FixedTimeProvider());
        var claudeAfterFirst = File.ReadAllBytes(sandbox.ClaudePath);
        var second = ClientConfigMerger.Setup(sandbox.Targets, executable, new FixedTimeProvider());

        Assert.All(first, result => Assert.True(result.Changed));
        Assert.All(second, result => Assert.False(result.Changed));
        Assert.Equal(claudeAfterFirst, File.ReadAllBytes(sandbox.ClaudePath));
        var claudeBackups = Directory.GetFiles(Path.GetDirectoryName(sandbox.ClaudePath)!, "claude_desktop_config.json.ddai-backup-20260809T1200000000000Z-*.json");
        var geminiBackups = Directory.GetFiles(Path.GetDirectoryName(sandbox.GeminiPath)!, "settings.json.ddai-backup-20260809T1200000000000Z-*.json");
        Assert.Single(claudeBackups);
        Assert.Single(geminiBackups);
        Assert.Equal(original, File.ReadAllText(claudeBackups[0]));
        Assert.Equal(original, File.ReadAllText(geminiBackups[0]));
    }

    [Fact]
    public void Setup_InvalidJsonRollsBackAllTargetsBeforeAnyBackupOrWrite()
    {
        using var sandbox = new ConfigSandbox();
        const string claudeOriginal = "{\"mcpServers\":{\"other\":{\"command\":\"keep\"}}}";
        const string invalidGemini = "{ not-json";
        File.WriteAllText(sandbox.ClaudePath, claudeOriginal);
        File.WriteAllText(sandbox.GeminiPath, invalidGemini);

        Assert.Throws<ClientConfigException>(() =>
            ClientConfigMerger.Setup(sandbox.Targets, Path.Combine(sandbox.Root, "ddai.exe"), new FixedTimeProvider()));

        Assert.Equal(claudeOriginal, File.ReadAllText(sandbox.ClaudePath));
        Assert.Equal(invalidGemini, File.ReadAllText(sandbox.GeminiPath));
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*.ddai-backup-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(sandbox.Root, ".*.ddai-stage-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Uninstall_RemovesOnlyDdaiEntriesAndIsIdempotent()
    {
        using var sandbox = new ConfigSandbox();
        var existing = """
            { "mcpServers": { "ddai": { "command": "old.exe", "args": ["serve"] }, "other": { "command": "keep.exe" } }, "keep": 42 }
            """;
        File.WriteAllText(sandbox.ClaudePath, existing);
        File.WriteAllText(sandbox.GeminiPath, existing);

        var first = ClientConfigMerger.Uninstall(sandbox.Targets, new FixedTimeProvider());
        var second = ClientConfigMerger.Uninstall(sandbox.Targets, new FixedTimeProvider());

        Assert.All(first, result => Assert.True(result.Changed));
        Assert.All(second, result => Assert.False(result.Changed));
        foreach (var path in new[] { sandbox.ClaudePath, sandbox.GeminiPath })
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Null(root["mcpServers"]!["ddai"]);
            Assert.Equal("keep.exe", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
            Assert.Equal(42, root["keep"]!.GetValue<int>());
        }
    }

    private static void AssertOwnedEntry(JsonNode node, string executable, bool expectTrust, bool trustPresent)
    {
        Assert.Equal(Path.GetFullPath(executable), node["command"]!.GetValue<string>());
        Assert.Equal(["serve", "--stdio"], node["args"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal(trustPresent, node["trust"] is not null);
        if (trustPresent)
        {
            Assert.Equal(expectTrust, node["trust"]!.GetValue<bool>());
        }
    }

    private sealed class ConfigSandbox : IDisposable
    {
        public ConfigSandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "ddai-config-tests", Guid.NewGuid().ToString("N"));
            ClaudePath = Path.Combine(Root, "Claude", "claude_desktop_config.json");
            GeminiPath = Path.Combine(Root, ".gemini", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(GeminiPath)!);
            Targets =
            [
                new ClientConfigTarget(ClientKind.Claude, ClaudePath),
                new ClientConfigTarget(ClientKind.Gemini, GeminiPath),
            ];
        }

        public string Root { get; }
        public string ClaudePath { get; }
        public string GeminiPath { get; }
        public IReadOnlyList<ClientConfigTarget> Targets { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
