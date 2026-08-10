namespace DDAI.App.Assets;

public sealed record GeneratedAssetImportRequest(
    string IdempotencyKey,
    string Category,
    string Name,
    IReadOnlyList<string> Tags,
    int GridWidth,
    int GridHeight,
    string? ContentBase64,
    string? InboxToken);

public sealed record GeneratedAssetImportResult(
    string GeneratedAssetId,
    string ContentHash,
    string PreviewHash,
    int Width,
    int Height,
    string Category,
    string ActivationState,
    bool Duplicate);

public sealed class GeneratedAssetImportException(string code, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public string Code { get; } = code;
}
