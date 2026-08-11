using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
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
    string? Cursor = null,
    bool IncludeStagedGenerated = false);

public sealed record AssetSearchItem(
    string AssetRef,
    string Category,
    string DisplayName,
    string ResourceFingerprint,
    string? PackId,
    string? PackName,
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> Tags,
    string? PreviewHash,
    bool AllowThirdPartyUse,
    bool Generated,
    bool Placeable)
{
    internal static AssetSearchItem Create(AssetCatalogEntry entry, bool placeable) => new(
        entry.AssetRef,
        entry.Category,
        entry.DisplayName,
        entry.ResourceFingerprint,
        entry.PackId,
        entry.PackName,
        entry.SearchTerms,
        entry.Tags,
        entry.PreviewHash,
        entry.AllowThirdPartyUse,
        entry.Generated,
        placeable);
}

public sealed record AssetSearchResult(
    IReadOnlyList<AssetSearchItem> Items,
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
    private readonly Func<IReadOnlyList<AssetCatalogEntry>> getStagedGeneratedEntries;

    public AssetSearchService(AssetCatalogRepository catalogRepository)
    {
        ArgumentNullException.ThrowIfNull(catalogRepository);
        getCurrent = catalogRepository.GetCurrent;
        getStagedGeneratedEntries = catalogRepository.GetStagedGeneratedEntries;
    }

    internal AssetSearchService(Func<AcceptedAssetCatalog?> getCurrent) : this(getCurrent, () => [])
    {
    }

    internal AssetSearchService(
        Func<AcceptedAssetCatalog?> getCurrent,
        Func<IReadOnlyList<AssetCatalogEntry>> getStagedGeneratedEntries)
    {
        ArgumentNullException.ThrowIfNull(getCurrent);
        ArgumentNullException.ThrowIfNull(getStagedGeneratedEntries);
        this.getCurrent = getCurrent;
        this.getStagedGeneratedEntries = getStagedGeneratedEntries;
    }

    public AssetSearchResult Search(AssetSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var catalog = getCurrent()
            ?? throw new InvalidOperationException("The asset catalog is unavailable.");
        var criteria = SearchCriteria.Create(query);
        var candidates = catalog.Entries
            .Select(entry => new SearchCandidate(entry, true))
            .ToList();
        if (query.IncludeStagedGenerated)
        {
            var liveReferences = candidates.Select(candidate => candidate.Entry.AssetRef).ToHashSet(StringComparer.Ordinal);
            candidates.AddRange(getStagedGeneratedEntries()
                .Where(entry => !liveReferences.Contains(entry.AssetRef))
                .Select(entry => new SearchCandidate(entry, false)));
        }

        var entries = candidates
            .Where(candidate => criteria.MatchesFilters(candidate.Entry))
            .Where(candidate => criteria.MatchesQuery(candidate.Entry))
            .OrderBy(candidate => criteria.Rank(candidate.Entry))
            .ThenBy(candidate => candidate.Entry.AssetRef, StringComparer.Ordinal)
            .ToArray();
        var cursorFingerprint = CursorFingerprint(catalog.Manifest.CatalogFingerprint, candidates);
        var offset = DecodeCursor(query.Cursor, cursorFingerprint, entries.Length);
        var page = entries.Skip(offset).Take(criteria.Limit).ToArray();
        var nextOffset = checked(offset + page.Length);
        var nextCursor = nextOffset < entries.Length
            ? EncodeCursor(cursorFingerprint, nextOffset)
            : null;

        return new AssetSearchResult(
            page.Select(candidate => AssetSearchItem.Create(candidate.Entry, candidate.Placeable)).ToArray(),
            nextCursor,
            catalog.Manifest.CatalogRevision,
            catalog.Manifest.CatalogFingerprint,
            catalog.Live,
            catalog.Manifest.SnapshotAt);
    }

    private static string CursorFingerprint(string catalogFingerprint, IReadOnlyList<SearchCandidate> candidates)
    {
        var stagedEntries = candidates
            .Where(candidate => !candidate.Placeable)
            .OrderBy(candidate => candidate.Entry.AssetRef, StringComparer.Ordinal)
            .ToArray();
        if (stagedEntries.Length == 0)
        {
            return catalogFingerprint;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCursorString(hash, "staged-overlay-v2");
        AppendCursorString(hash, catalogFingerprint);
        AppendCursorInteger(hash, stagedEntries.Length);
        foreach (var candidate in stagedEntries)
        {
            var entry = candidate.Entry;
            AppendCursorString(hash, entry.AssetRef);
            AppendCursorString(hash, entry.Category);
            AppendCursorString(hash, entry.DisplayName);
            AppendCursorString(hash, entry.ResourceFingerprint);
            AppendCursorString(hash, entry.PackId);
            AppendCursorString(hash, entry.PackName);
            AppendCursorStrings(hash, entry.SearchTerms);
            AppendCursorStrings(hash, entry.Tags);
            AppendCursorString(hash, entry.PreviewHash);
            hash.AppendData([entry.AllowThirdPartyUse ? (byte)1 : (byte)0]);
            hash.AppendData([entry.Generated ? (byte)1 : (byte)0]);
            hash.AppendData([candidate.Placeable ? (byte)1 : (byte)0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendCursorStrings(IncrementalHash hash, IReadOnlyList<string> values)
    {
        AppendCursorInteger(hash, values.Count);
        foreach (var value in values)
        {
            AppendCursorString(hash, value);
        }
    }

    private static void AppendCursorString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendCursorInteger(hash, -1);
            return;
        }

        var bytes = StrictUtf8.GetBytes(value);
        AppendCursorInteger(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendCursorInteger(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
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

    private sealed record SearchCandidate(AssetCatalogEntry Entry, bool Placeable);
}
