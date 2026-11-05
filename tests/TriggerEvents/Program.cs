using Jellyfin.Plugin.MetadataRules.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

int assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
    Console.WriteLine("PASS " + message);
}
var itemId = Guid.NewGuid(); var seriesId = Guid.NewGuid(); var seasonId = Guid.NewGuid(); var primaryId = Guid.NewGuid();
// No LibraryManager/provider is initialized: capture must read copied model IDs only.
var episode = new Episode { Id = itemId, SeriesId = seriesId, SeasonId = seasonId, PrimaryVersionId = primaryId };
var snapshot = InstantItemEvent.Capture(episode);
Check(snapshot.ItemId == itemId && snapshot.Eligible, "existing episode event is eligible without a prior pending item");
Check(snapshot.OwnerIds.ToHashSet().SetEquals(new[] { seriesId, seasonId, primaryId }), "episode event invalidates series, season, and primary-version owners");
episode.Id = Guid.NewGuid(); episode.SeriesId = Guid.NewGuid(); episode.SeasonId = Guid.Empty; episode.PrimaryVersionId = null;
Check(snapshot.ItemId == itemId && snapshot.SeriesId == seriesId && snapshot.SeasonId == seasonId && snapshot.PrimaryVersionId == primaryId, "captured IDs remain detached when the mutable event item changes");
Check(snapshot.OwnerIds.Count() == 3, "detached removal snapshot retains every aggregate owner");
var season = InstantItemEvent.Capture(new Season { Id = seasonId, SeriesId = seriesId });
Check(season.Eligible && season.OwnerIds.SequenceEqual(new[] { seriesId }), "season event invalidates its series directly");
var alternateMovie = InstantItemEvent.Capture(new Movie { Id = itemId, PrimaryVersionId = primaryId });
Check(alternateMovie.Eligible && alternateMovie.OwnerIds.SequenceEqual(new[] { primaryId }), "alternate movie event invalidates its owning primary movie");
var duplicates = InstantItemEvent.Capture(new Episode { Id = itemId, PrimaryVersionId = seriesId, SeriesId = seriesId, SeasonId = seriesId });
Check(duplicates.OwnerIds.SequenceEqual(new[] { seriesId }), "aggregate owner IDs are deduplicated");
var self = InstantItemEvent.Capture(new Episode { Id = itemId, PrimaryVersionId = itemId, SeriesId = itemId, SeasonId = Guid.Empty });
Check(self.PrimaryVersionId is null && !self.OwnerIds.Any(), "empty and self owner IDs cannot create recursive instant queues");
var early = InstantItemEvent.Capture(new Episode { Id = itemId });
Check(early.Eligible && !early.OwnerIds.Any(), "early metadata event with missing parent IDs is safe for worker reconciliation");
var other = InstantItemEvent.Capture(new Video { Id = itemId, PrimaryVersionId = primaryId });
Check(!other.Eligible && other.OwnerIds.SequenceEqual(new[] { primaryId }), "noncandidate video evidence can invalidate a primary item without becoming a candidate");
Console.WriteLine($"{assertions} trigger event assertions passed.");
