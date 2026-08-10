namespace DDAI.App.Assets;

public sealed record AssetCatalogPublicationAdvice(
    bool Success,
    long? CatalogRevision,
    int? SlotIndex,
    string? ErrorCode);

public sealed class AssetCatalogPublicationAdvisor
{
    private const long MaximumGodotJsonInteger = 9_007_199_254_740_991L;
    private readonly AssetCatalogRepository repository;

    public AssetCatalogPublicationAdvisor(string catalogRoot, TimeProvider timeProvider)
    {
        repository = new AssetCatalogRepository(catalogRoot, timeProvider);
    }

    public AssetCatalogPublicationAdvice InspectForPublication(long wallClockRevision)
    {
        if (wallClockRevision is < 0 or > MaximumGodotJsonInteger)
        {
            return Failed("catalog_revision_invalid");
        }

        IReadOnlyList<CatalogPointerInspection> inspections;
        try
        {
            inspections = repository.InspectPointers();
            AssetCatalogRepository.EnsureNoRevisionConflict(
                inspections.Where(pointer => pointer.Catalog is not null).Select(pointer => pointer.Catalog!));
        }
        catch (InvalidDataException)
        {
            return Failed("catalog_pointer_conflict");
        }

        var valid = inspections.Where(pointer => pointer.Catalog is not null).ToArray();
        var highestRevision = valid.Length == 0
            ? -1
            : valid.Max(pointer => pointer.Catalog!.Manifest.CatalogRevision);
        if (highestRevision >= MaximumGodotJsonInteger)
        {
            return Failed("catalog_revision_exhausted");
        }

        var revision = Math.Max(wallClockRevision, checked(highestRevision + 1));
        var slots = inspections.Where(pointer => pointer.SlotIndex >= 0).OrderBy(pointer => pointer.SlotIndex).ToArray();
        var invalid = slots.FirstOrDefault(pointer => pointer.Catalog is null);
        int slotIndex;
        if (invalid is not null)
        {
            slotIndex = invalid.SlotIndex;
        }
        else
        {
            slotIndex = slots
                .OrderBy(pointer => pointer.Catalog!.Manifest.CatalogRevision)
                .ThenBy(pointer => pointer.SlotIndex)
                .First().SlotIndex;
        }

        return new AssetCatalogPublicationAdvice(true, revision, slotIndex, null);
    }

    private static AssetCatalogPublicationAdvice Failed(string errorCode) => new(false, null, null, errorCode);
}
