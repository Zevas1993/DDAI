using System.Globalization;
using System.Text;
using DDAI.Core.Assets;

namespace DDAI.App.Assets;

public sealed record AssetSearchQuery(
    string? Query = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? PackIds = null,
    bool? Generated = null,
    bool? PreviewRequired = null,
    IReadOnlyList<string>? Tags = null,
    int? Limit = null,
    string? Cursor = null);

public sealed record AssetSearchResult(
    IReadOnlyList<AssetCatalogEntry> Items,
    string? NextCursor,
    long CatalogRevision,
    string CatalogFingerprint,
    bool Live,
    DateTimeOffset SnapshotAt);

public sealed class AssetSearchService
{
    public const int DefaultLimit = 20;
    public const int MaximumLimit = 100;
    public const int MaximumQueryScalars = 256;
    public const int MaximumTags = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Func<AcceptedAssetCatalog?> getCurrent;

    public AssetSearchService(AssetCatalogRepository catalogRepository)
    {
        ArgumentNullException.ThrowIfNull(catalogRepository);
        getCurrent = catalogRepository.GetCurrent;
    }

    internal AssetSearchService(Func<AcceptedAssetCatalog?> getCurrent)
    {
        ArgumentNullException.ThrowIfNull(getCurrent);
        this.getCurrent = getCurrent;
    }

    public AssetSearchResult Search(AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var catalog = getCurrent()
            ?? throw new InvalidOperationException("The asset catalog is unavailable.");
        var criteria = SearchCriteria.Create(query);
        var entries = catalog.Entries
            .Where(criteria.MatchesFilters)
            .Where(criteria.MatchesQuery)
            .OrderBy(entry => criteria.Rank(entry))
            .ThenBy(entry => entry.AssetRef, StringComparer.Ordinal)
            .ToArray();
        var offset = DecodeCursor(query.Cursor, catalog.Manifest.CatalogFingerprint, entries.Length);
        var page = entries.Skip(offset).Take(criteria.Limit).ToArray();
        var nextOffset = checked(offset + page.Length);
        var nextCursor = nextOffset < entries.Length
            ? EncodeCursor(catalog.Manifest.CatalogFingerprint, nextOffset)
            : null;

        return new AssetSearchResult(
            page,
            nextCursor,
            catalog.Manifest.CatalogRevision,
            catalog.Manifest.CatalogFingerprint,
            catalog.Live,
            catalog.Manifest.SnapshotAt);
    }

    private static int DecodeCursor(string? cursor, string fingerprint, int entryCount)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            if (cursor.Length == 0 || cursor.Length % 4 == 1 || cursor.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            {
                throw new ArgumentException("The cursor is malformed.", nameof(cursor));
            }

            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) & ~3, '=');
            var payload = StrictUtf8.GetString(Convert.FromBase64String(padded));
            var separator = payload.IndexOf('\n');
            if (separator <= 0 || separator != payload.LastIndexOf('\n') ||
                !string.Equals(payload[..separator], fingerprint, StringComparison.Ordinal) ||
                !int.TryParse(payload[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
                offset < 0)
            {
                throw new ArgumentException("The cursor is malformed or no longer applies to this catalog.", nameof(cursor));
            }

            if (offset >= entryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor), "The cursor offset is outside the result set.");
            }

            return offset;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The cursor is malformed.", nameof(cursor), exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("The cursor is malformed.", nameof(cursor), exception);
        }
    }

    private static string EncodeCursor(string fingerprint, int offset) =>
        Convert.ToBase64String(StrictUtf8.GetBytes($"{fingerprint}\n{offset}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record SearchCriteria(
        string Query,
        IReadOnlyList<string> Tokens,
        HashSet<string>? Categories,
        HashSet<string>? PackIds,
        bool? Generated,
        bool? PreviewRequired,
        HashSet<string>? Tags,
        int Limit)
    {
        public static SearchCriteria Create(AssetSearchQuery query)
        {
            if (query.Query is not null && query.Query.EnumerateRunes().Count() > MaximumQueryScalars)
            {
                throw new ArgumentOutOfRangeException(nameof(query), $"Query text cannot exceed {MaximumQueryScalars} Unicode scalars.");
            }

            if (query.Limit is < 1 or > MaximumLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(query), $"Limit must be between 1 and {MaximumLimit}.");
            }

            if (query.Tags?.Count > MaximumTags)
            {
                throw new ArgumentOutOfRangeException(nameof(query), $"At most {MaximumTags} tags can be requested.");
            }

            var categories = ToSet(query.Categories, category =>
            {
                if (!AssetCategory.IsCanonical(category))
                {
                    throw new ArgumentException("Every category must be canonical.", nameof(query));
                }

                return Normalize(category);
            });
            return new SearchCriteria(
                Normalize(query.Query),
                Tokenize(query.Query),
                categories,
                ToSet(query.PackIds, Normalize),
                query.Generated,
                query.PreviewRequired,
                ToSet(query.Tags, Normalize),
                query.Limit ?? DefaultLimit);
        }

        public bool MatchesFilters(AssetCatalogEntry entry) =>
            (Categories is null || Categories.Contains(Normalize(entry.Category))) &&
            (PackIds is null || (entry.PackId is not null && PackIds.Contains(Normalize(entry.PackId)))) &&
            (Generated is null || entry.Generated == Generated) &&
            (PreviewRequired is null || (entry.PreviewHash is not null) == PreviewRequired) &&
            (Tags is null || entry.Tags.Select(Normalize).Any(Tags.Contains));

        public bool MatchesQuery(AssetCatalogEntry entry) =>
            Tokens.All(token => SearchableFields(entry).Any(field => field.Contains(token, StringComparison.Ordinal)));

        public int Rank(AssetCatalogEntry entry)
        {
            if (Query.Length == 0)
            {
                return 0;
            }

            var displayName = Normalize(entry.DisplayName);
            if (string.Equals(displayName, Query, StringComparison.Ordinal))
            {
                return 0;
            }

            if (displayName.StartsWith(Query, StringComparison.Ordinal))
            {
                return 1;
            }

            if (entry.Tags.Select(Normalize).Any(tag => string.Equals(tag, Query, StringComparison.Ordinal)))
            {
                return 2;
            }

            return 3;
        }

        private static IEnumerable<string> SearchableFields(AssetCatalogEntry entry)
        {
            yield return Normalize(entry.DisplayName);
            yield return Normalize(entry.Category);
            if (entry.PackId is not null)
            {
                yield return Normalize(entry.PackId);
            }

            if (entry.PackName is not null)
            {
                yield return Normalize(entry.PackName);
            }

            foreach (var term in entry.SearchTerms)
            {
                yield return Normalize(term);
            }

            foreach (var tag in entry.Tags)
            {
                yield return Normalize(tag);
            }
        }

        private static HashSet<string>? ToSet(IReadOnlyList<string>? values, Func<string, string> normalize)
        {
            if (values is null || values.Count == 0)
            {
                return null;
            }

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("Filter values cannot be blank.", nameof(values));
                }

                result.Add(normalize(value));
            }

            return result;
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<string> Tokenize(string? value) =>
        Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
