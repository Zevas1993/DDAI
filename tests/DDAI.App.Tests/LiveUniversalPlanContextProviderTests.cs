using System.Text.Json;
using DDAI.App;
using DDAI.App.Assets;
using DDAI.Core.Assets;
using System.Collections.Immutable;

namespace DDAI.App.Tests;

public sealed class LiveUniversalPlanContextProviderTests
{
    [Fact]
    public void TryReadCapabilities_AcceptsExactBoundedRuntimeContract()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            map_id = "map-session-001",
            map_job_revision = 42,
            level_ids = new[] { "level-0", "level-1" },
            certified_operation_types = new[] { "wall_polyline", "object_placement" },
        });

        Assert.True(LiveUniversalPlanContextProvider.TryReadCapabilities(payload, out var capabilities));
        Assert.Equal("map-session-001", capabilities!.MapId);
        Assert.Equal(42, capabilities.MapRevision);
        Assert.Equal(["level-0", "level-1"], capabilities.LevelIds);
        Assert.True(capabilities.RuntimeCertifiedOperations["wall_polyline"]);
    }

    [Theory]
    [InlineData("../unsafe", 42, "level-0", "wall_polyline")]
    [InlineData("map-session-001", -1, "level-0", "wall_polyline")]
    [InlineData("map-session-001", 42, "../unsafe", "wall_polyline")]
    [InlineData("map-session-001", 42, "level-0", "unknown_operation")]
    public void TryReadCapabilities_RejectsUnsafeOrUncertifiedRuntimeData(
        string mapId,
        long revision,
        string level,
        string operation)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            map_id = mapId,
            map_job_revision = revision,
            level_ids = new[] { level },
            certified_operation_types = new[] { operation },
        });

        Assert.False(LiveUniversalPlanContextProvider.TryReadCapabilities(payload, out _));
    }

    [Fact]
    public void TryReadCapabilities_RejectsDuplicatesAndOversizeArraysBeforeTrust()
    {
        var duplicate = JsonSerializer.SerializeToElement(new
        {
            map_id = "map-session-001",
            map_job_revision = 42,
            level_ids = new[] { "level-0", "level-0" },
            certified_operation_types = new[] { "wall_polyline" },
        });
        var oversize = JsonSerializer.SerializeToElement(new
        {
            map_id = "map-session-001",
            map_job_revision = 42,
            level_ids = Enumerable.Range(0, 101).Select(index => "level-" + index).ToArray(),
            certified_operation_types = new[] { "wall_polyline" },
        });

        Assert.False(LiveUniversalPlanContextProvider.TryReadCapabilities(duplicate, out _));
        Assert.False(LiveUniversalPlanContextProvider.TryReadCapabilities(oversize, out _));
    }

    [Fact]
    public void TryReadCapabilities_RejectsDuplicateSecurityCriticalProperties()
    {
        using var document = JsonDocument.Parse("""
            {"map_id":"map-session-001","map_id":"map-session-002","map_job_revision":42,"level_ids":["level-0"],"certified_operation_types":["wall_polyline"]}
            """);

        Assert.False(LiveUniversalPlanContextProvider.TryReadCapabilities(document.RootElement, out _));
    }

    [Fact]
    public void CatalogSnapshotRemainedStable_RejectsReplacementDuringStatusAcquisition()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FixedTimeProvider(now);
        var original = AcceptedCatalog(7, new string('a', 64), now, time);
        var replacement = AcceptedCatalog(8, new string('b', 64), now, time);

        Assert.False(LiveUniversalPlanContextProvider.CatalogSnapshotRemainedStable(original, replacement));
        Assert.True(LiveUniversalPlanContextProvider.CatalogSnapshotRemainedStable(
            original,
            AcceptedCatalog(7, new string('a', 64), now, time)));
    }

    private static AcceptedAssetCatalog AcceptedCatalog(
        long revision,
        string fingerprint,
        DateTimeOffset snapshotAt,
        TimeProvider timeProvider) => new(
            new AssetCatalogManifest(
                AssetCatalogManifest.CurrentSchemaVersion,
                "session-001",
                revision,
                fingerprint,
                snapshotAt,
                true,
                new Dictionary<string, int>(),
                [],
                []),
            ImmutableArray<AssetCatalogEntry>.Empty,
            timeProvider);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
