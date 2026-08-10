namespace DDAI.Core.Assets;

public static class AssetCategory
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        "Terrain",
        "Patterns",
        "Patterns Colorable",
        "Caves",
        "Roofs",
        "Objects",
        "Walls",
        "Materials",
        "Portals",
        "Paths",
        "Lights",
        "Simple Tiles",
        "Smart Tiles",
        "Smart Tiles Double",
    ]);

    public static bool IsCanonical(string? category) =>
        category is not null && All.Contains(category, StringComparer.Ordinal);
}
