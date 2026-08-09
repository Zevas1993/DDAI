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

    public required long BaseRevision { get; init; }

    public required MapOperationMode Mode { get; init; }

    public required MapCanvas Canvas { get; init; }
}
