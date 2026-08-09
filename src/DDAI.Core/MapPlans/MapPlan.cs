namespace DDAI.Core.MapPlans;

public enum MapOperationMode
{
    Add,
    Replace,
    Patch,
}

public sealed record MapCanvas(int Width, int Height);

public sealed record MapPlan
{
    public const string CurrentSchemaVersion = "1.0";

    public required string SchemaVersion { get; init; }

    public required string RequestId { get; init; }

    public long BaseRevision { get; init; }

    public MapOperationMode Mode { get; init; }

    public required MapCanvas Canvas { get; init; }
}
