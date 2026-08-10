using System.Text.Json.Nodes;

namespace DDAI.App.Tests;

[Collection("Published executable")]
public sealed class DungeondraftActivationPublishedTests : IClassFixture<PublishedExecutableFixture>
{
    private readonly PublishedExecutableFixture fixture;

    public DungeondraftActivationPublishedTests(PublishedExecutableFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public void Setup_ActivatesByteSafelyOrDefersWhenDungeondraftIsRunning()
    {
        using var sandbox = new PublishedLifecycleSandbox(fixture.ExecutablePath);
        const string claude = "{\"mcpServers\":{\"other\":{\"command\":\"keep-claude.exe\"}}}";
        const string gemini = "{\"mcpServers\":{\"other\":{\"command\":\"keep-gemini.exe\",\"trust\":true}}}";
        File.WriteAllText(sandbox.ClaudePath, claude);
        File.WriteAllText(sandbox.GeminiPath, gemini);
        var originalConfig = File.ReadAllBytes(sandbox.ConfigPath);

        var first = sandbox.Run("setup");
        var result = JsonNode.Parse(first.StandardOutput)!;

        Assert.Equal(string.Empty, first.StandardError);
        Assert.Equal("keep-claude.exe", ReadOtherCommand(sandbox.ClaudePath));
        Assert.Equal("keep-gemini.exe", ReadOtherCommand(sandbox.GeminiPath));

        if (first.ExitCode == 2)
        {
            Assert.Equal("activation_pending_dungeondraft_running", result["code"]!.GetValue<string>());
            Assert.Equal(originalConfig, File.ReadAllBytes(sandbox.ConfigPath));
            Assert.Empty(Directory.GetFiles(sandbox.UserDataDirectory, "config.ini.ddai-backup-*.ini"));
            return;
        }

        Assert.Equal(0, first.ExitCode);
        Assert.Equal("updated", result["dungeondraft_config"]!["state"]!.GetValue<string>());
        var backupPath = result["dungeondraft_config"]!["backup_path"]!.GetValue<string>();
        Assert.Equal(originalConfig, File.ReadAllBytes(backupPath));
        var configured = File.ReadAllBytes(sandbox.ConfigPath);
        var configText = File.ReadAllText(sandbox.ConfigPath);
        Assert.Contains("Lievven.Snappy_Mod", configText);
        Assert.Contains(DungeondraftConfigEditor.DdaiModId, configText);
        Assert.Contains(sandbox.ModsDirectory.Replace("\\", "\\\\", StringComparison.Ordinal), configText);

        var second = sandbox.Run("setup");

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(string.Empty, second.StandardError);
        Assert.Equal("already_current", JsonNode.Parse(second.StandardOutput)!["dungeondraft_config"]!["state"]!.GetValue<string>());
        Assert.Equal(configured, File.ReadAllBytes(sandbox.ConfigPath));
        Assert.Single(Directory.GetFiles(sandbox.UserDataDirectory, "config.ini.ddai-backup-*.ini"));
    }

    private static string ReadOtherCommand(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["other"]!["command"]!.GetValue<string>();
}
