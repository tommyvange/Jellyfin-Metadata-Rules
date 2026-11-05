using Jellyfin.Plugin.MetadataRules.Engine;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MetadataRules.Services;

/// <summary>Read-only suggestions from stored media values, independent of a rule's selected libraries.</summary>
internal static class RatingChoiceCatalog
{
    internal const int BatchSize = 512;

    public static IReadOnlyDictionary<string, IReadOnlyList<ValueChoice>> Read(ILibraryManager library, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Freeze the complete ID set rather than paginating mutable titles. Include alternate versions,
        // descendants, and other media types so suggestions cover every stored value in the library.
        var ids = library.GetItemIds(new InternalItemsQuery
        {
            Recursive = true, IncludeOwnedItems = true, IncludeAlternateVersions = true,
            GroupByPresentationUniqueKey = false, DtoOptions = DtoOptions.StoredColumnsOnly
        });
        var official = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ids.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Rating columns need no provider/image/user-data hydration. These instances are never saved.
            var items = library.GetItemList(new InternalItemsQuery
            {
                ItemIds = batch, IncludeOwnedItems = true, IncludeAlternateVersions = true,
                GroupByPresentationUniqueKey = false, DtoOptions = DtoOptions.StoredColumnsOnly
            });
            foreach (var item in items)
            {
                Add(official, item.OfficialRating);
                Add(custom, item.CustomRating);
            }
        }
        return new Dictionary<string, IReadOnlyList<ValueChoice>>
        {
            ["rating.official"] = Choices(official),
            ["rating.custom"] = Choices(custom)
        };
    }

    private static void Add(Dictionary<string, string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Comparisons are ordinal and case-insensitive. Choose a stable stored spelling for equivalent
        // values while preserving the exact string, including any meaningful whitespace or punctuation.
        if (!values.TryGetValue(value, out var existing) || string.CompareOrdinal(value, existing) < 0) values[value] = value;
    }

    private static IReadOnlyList<ValueChoice> Choices(Dictionary<string, string> values) => values.Values
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value, StringComparer.Ordinal)
        .Select(value => new ValueChoice(value, value))
        .ToArray();
}
