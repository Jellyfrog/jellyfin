# Metadata provider capability skip

Date: 2026-09-18
Branch: `release-12.z`
Status: design, awaiting review

## Problem

`MetadataService.ExecuteRemoteProviders` calls **every** enabled remote metadata
provider on every refresh. There is no early exit. Priority between providers is
expressed only at merge time: `MergeData(result, temp, [], replaceData: false, false)`
writes a field only when the target's copy is still empty, so the first provider
in the configured order wins each field and later providers backfill the holes.

The image pipeline does not work this way. `ItemImageProvider.RefreshFromProvider`
bails out **before** the network call when `ContainsImages(...)` reports that every
image type the provider supports is already satisfied.

The result is redundant upstream API calls. On a library scan with TMDb, OMDb and
TVDb enabled, providers two and three are queried for every item even when provider
one returned a complete record and nothing they return can be used.

## Goal

Give remote metadata providers the same capability-based early exit the image
providers have: skip a provider's network call when every field it could
contribute is already populated.

## Why the image mechanism does not port directly

`ContainsImages` works because the image pipeline has a closed world:

1. A bounded `ImageType` enum.
2. A per-provider capability list, `provider.GetSupportedImages(item)`.
3. A user-declared want-list, `savedOptions.GetLimit(type) > 0`, so "complete" is
   reachable.

Metadata has none of the three. `IRemoteMetadataProvider` declares only
`GetMetadata`, `MetadataOptions` has no per-field want-list, and
`MergeBaseItemData` touches roughly 30 fields, many of which are legitimately
empty forever (`Tagline`, `CriticRating`, `EndDate`, `OriginalTitle`). A naive
"every field is filled" test would never be true and the feature would be dead
code.

This design supplies (1) and (2). It deliberately does not add (3).

## Design

### 1. Vocabulary: extend `MetadataField`

`MediaBrowser.Model/Entities/MetadataField.cs` currently has 9 values and is used
for `BaseItem.LockedFields`. It guards 9 of the merges in `MergeBaseItemData`.
Extend it to cover the fields in-tree providers actually write.

Existing (9): `Cast`, `Genres`, `ProductionLocations`, `Studios`, `Tags`, `Name`,
`Overview`, `Runtime`, `OfficialRating`.

Append, generic (13): `PremiereDate`, `ProductionYear`, `CommunityRating`,
`CriticRating`, `Tagline`, `OriginalTitle`, `HomePageUrl`, `IndexNumber`,
`ParentIndexNumber`, `EndDate`, `RemoteTrailers`, `OriginalLanguage`,
`AlbumArtists`.

Append, type-specific (3): `CollectionName` (Movie), `Status` (Series),
`IndexNumberEnd` (Episode).

**Append only. Never insert or reorder.** `BaseItemMetadataField.Id` is an `int`
and the enum persists by ordinal, so reordering silently corrupts every stored
lock.

Deliberately excluded: `CustomRating` and `SortName` (user-set, no in-tree
provider writes them); `DateCreated`, `DateModified`, `IsLocked`, `LockedFields`,
`PreferredMetadataCountryCode`, `PreferredMetadataLanguage` (bookkeeping, merged
only under `mergeMetadataSettings`); `DisplayOrder`, `Video3DFormat`, `AirTime`
and `AirDays` (mergeable, but no in-tree remote provider writes them).

A provider that writes an excluded field must not implement the capability
interface until the corresponding value is appended. See the safety valve below.

Side effect: these values also become lockable in the UI, since `LockedFields`
shares the enum.

### 2. Capability interface

New file `MediaBrowser.Controller/Providers/IHasSupportedMetadataFields.cs`:

```csharp
public interface IHasSupportedMetadataFields
{
    IReadOnlyList<MetadataField> GetSupportedMetadataFields(BaseItem item);

    IReadOnlyList<string> GetSupportedProviderIdKeys(BaseItem item);
}
```

An optional marker interface, matching the existing `IHasOrder` and
`IHasItemChangeMonitor` idiom, rather than a member on `IRemoteMetadataProvider`.
A provider that does not implement it is never skipped and behaves exactly as
today. This keeps every third-party metadata plugin source and binary compatible,
which is required on a patch branch.

Both methods take the item because the field set is type-dependent:
`OmdbItemProvider` serves Series, Movie and Trailer through one generic
`GetResult<T>`.

#### Contract: declare a superset

Errors are asymmetric.

- **Over-declaring** a field makes the skip predicate harder to satisfy, so the
  provider runs more often than strictly necessary. Harmless.
- **Under-declaring** lets the provider be skipped while it still had something to
  contribute. Silent data loss.

So `GetSupportedMetadataFields` must return every field the provider could *ever*
write, as a superset over all languages, configurations and response shapes. When
in doubt, include the field.

This resolves the conditional cases found in the provider audit: OMDb writes
`Name`, `Genres` and `OfficialRating` only when the metadata language is English
(and `OfficialRating` also requires country `us`); `AudioDbAlbumProvider.Album`,
`MusicBrainzArtistProvider.Name` and `TmdbSeasonProvider.Name` are plugin-config
gated; all cast and crew sit behind `CastAndCrew` or the `MaxCast`/`MaxCrew` caps.
All are declared unconditionally.

#### Safety valve

A provider may implement `IHasSupportedMetadataFields` only if every field it
writes is expressible in `MetadataField`. If it writes anything outside the
vocabulary, either add the enum value first or leave the interface unimplemented.
An unimplemented provider is never skipped, so the failure mode is "no
optimisation", never "lost metadata".

### 3. Skip predicate

In `ExecuteRemoteProviders`, before calling `provider.GetMetadata(id, ...)`, skip
when **all** of the following hold:

1. The provider implements `IHasSupportedMetadataFields`.
2. Not identifying: `options.SearchResult is null`.
3. Every field in `GetSupportedMetadataFields(temp.Item)` is already non-empty in
   `temp`.
4. No declared field is `Overview` or `Tagline` while that value is currently a
   language fallback (`overviewIsFallback` / `taglineIsFallback`).
5. Every key in `GetSupportedProviderIdKeys(temp.Item)` is already present in
   `temp.Item.ProviderIds` and passes `ProviderIdsExtensions.IsValidProviderId`.

Log the skip at debug with the provider name and item, mirroring the existing
`"Running {Provider} for {Item}"` line.

`ExecuteRemoteProviders` does not currently receive `options`, so the identify
state must be threaded in, either by passing `options` or a precomputed
`bool allowSkip`.

#### Why condition 4 exists

Lines 981-999 track language fallbacks: a provider returning a non-preferred
language `Overview` marks it as a fallback, and a **later** provider whose result
matches the preferred language is allowed to overwrite it. A skip keyed only on
"Overview is non-empty" would stop at the fallback and never reach the
preferred-language provider. Only `Overview` and `Tagline` carry this tracking.

#### Why condition 5 exists

Each provider contributes its own `ProviderIds` keys (`Imdb`, `Tmdb`, `Tvdb`,
`MusicBrainzAlbum`) and they accumulate across providers at line 1307. Unlike
text, these are **not substitutable**: OMDb's IMDb id cannot be supplied by TMDb
having filled the Tagline. Skipping OMDb on an otherwise complete item would
permanently lose the IMDb id, breaking external links and the next refresh's
lookups.

This guard is also what makes the feature self-limiting in a desirable way. On a
first-ever scan nothing is skipped, because no ids are present yet.

#### Why `ReplaceAllMetadata` is *not* excluded

`ExecuteRemoteProviders` is called with `replaceData: false` hardcoded regardless
of `ReplaceAllMetadata`. The flag's uses (lines 803, 874, 902, 914) all sit
outside the remote loop: they control whether local providers run, whether the
remote phase runs at all, whether old metadata is erased, and how `temp` merges
back into the item at the end. Inside the loop a replace-all refresh is identical
to a normal one, so a provider that is redundant in one is redundant in the other.
Replace-all is a bulk library-wide operation, which is exactly where the wasted
calls hurt most.

#### Why identify *is* excluded

Condition 5 would make identify safe, so this is a cost decision rather than a
safety one. Identify is a manual, one-item-at-a-time repair where the saving is
negligible and the user has explicitly asked for a re-match. **Revisit if the
asymmetry seems arbitrary.**

#### Scope: intra-run only

`temp` starts empty at line 790 (`CreateNew()`), seeded only with `Path`, `Id`,
`ParentIndexNumber` and the language preferences. The item's persisted metadata is
merged in at line 905, *after* every remote provider has run.

So the predicate tests what **earlier providers in the same run** have supplied,
not what is already in the database. A re-refresh of a fully populated library
still calls the first provider, and skips later ones only once the first has
filled their fields.

Making the predicate consider persisted values would require seeding `temp` from
`metadata` up front, which changes merge semantics far beyond this feature and is
explicitly out of scope.

### 4. Field-presence test

One helper:

```csharp
private static bool HasField(MetadataResult<TItemType> result, MetadataField field)
```

A switch mapping each enum value to the same emptiness check `MergeBaseItemData`
already uses for it (`string.IsNullOrEmpty`, `.Length == 0`, `!.HasValue`,
`.Count == 0`). `Cast` maps to `result.People`.

Keeping this in sync with `MergeBaseItemData` is the main ongoing maintenance
burden. A test asserting every `MetadataField` value is handled guards against
drift when the enum grows.

## Provider declarations

From the audit of the 12 in-tree remote providers. Fields are supersets per the
contract above.

| Provider | Declared fields | Declared id keys |
|---|---|---|
| `AudioDbAlbumProvider` | Name, ProductionYear, Genres, Overview, AlbumArtists | AudioDbArtist, AudioDbAlbum, MusicBrainzAlbumArtist, MusicBrainzReleaseGroup |
| `AudioDbArtistProvider` | HomePageUrl, Genres, ProductionYear, ProductionLocations, Overview | AudioDbArtist, MusicBrainzArtist |
| `MusicBrainzAlbumProvider` | PremiereDate, ProductionYear, AlbumArtists, Genres, Tags, Studios | MusicBrainzAlbum, MusicBrainzReleaseGroup |
| `MusicBrainzArtistProvider` | Name, PremiereDate, ProductionYear, EndDate, ProductionLocations, Genres, Tags | MusicBrainzArtist |
| `OmdbItemProvider` | Name, OfficialRating, ProductionYear, CriticRating, CommunityRating, HomePageUrl, Genres, Overview, OriginalLanguage, Cast | Imdb |
| `OmdbEpisodeProvider` | IndexNumber, ParentIndexNumber, Name, OfficialRating, ProductionYear, CriticRating, CommunityRating, HomePageUrl, Genres, Overview, OriginalLanguage, Cast | Imdb |
| `TmdbBoxSetProvider` | Name, Overview | Tmdb |
| `TmdbMovieProvider` | Name, OriginalTitle, Overview, Tagline, ProductionLocations, CollectionName, CommunityRating, OfficialRating, PremiereDate, ProductionYear, Studios, Genres, Tags, RemoteTrailers, OriginalLanguage, Cast | Tmdb, Imdb, TmdbCollection |
| `TmdbPersonProvider` | Name, HomePageUrl, Overview, PremiereDate, EndDate, ProductionLocations | Tmdb, Imdb |
| `TmdbEpisodeProvider` | IndexNumber, ParentIndexNumber, IndexNumberEnd, Name, PremiereDate, ProductionYear, Overview, CommunityRating, RemoteTrailers, Cast | Tvdb, Imdb, TvRage |
| `TmdbSeasonProvider` | IndexNumber, Overview, PremiereDate, ProductionYear, Name, Cast | Tmdb, Tvdb |
| `TmdbSeriesProvider` | Name, OriginalTitle, CommunityRating, Overview, Studios, Genres, Tags, HomePageUrl, Runtime, Status, EndDate, PremiereDate, ProductionYear, OfficialRating, RemoteTrailers, OriginalLanguage, Cast | Tmdb, Imdb, TvRage, Tvdb |

Note: `TmdbEpisodeProvider` does **not** stamp a `Tmdb` id on the episode, and
`MusicBrainzAlbumProvider` never sets `Name`.

## Testing

Extend `tests/Jellyfin.Providers.Tests/Manager/MetadataServiceRefreshTests.cs`,
which already exercises this path:

1. Provider skipped when all declared fields and id keys are already satisfied.
2. Not skipped when one declared field is missing.
3. Not skipped when one declared id key is missing.
4. Not skipped when a declared id key is present but invalid.
5. Not skipped when `Overview` is currently a language fallback.
6. Not skipped when identifying.
7. Skipped under `ReplaceAllMetadata` when otherwise satisfied.
8. A provider not implementing the interface is never skipped.
9. Exhaustiveness: every `MetadataField` value is handled by `HasField`.

## Open questions for review

1. **Enum scope.** 16 appends, three of them type-specific (`CollectionName`,
   `Status`, `IndexNumberEnd`), land in an enum that also drives the
   lock UI. If that pollution is unacceptable, the contained alternative is a
   separate `ProvidedMetadataField` enum used only for capability declaration,
   leaving `MetadataField` alone for locking. This changes sections 1, 2 and 4
   only.
2. **Identify exclusion.** Justified on cost, not safety. Removing it simplifies
   the predicate by one condition.
3. **`HasField` drift.** The exhaustiveness test catches a missing enum value but
   cannot catch a check that disagrees with `MergeBaseItemData`. Worth considering
   whether the two can share one source of truth.

## Out of scope

- Seeding `temp` from persisted metadata to enable cross-refresh skipping.
- Any change to media segment providers, which have a separate and larger problem:
  results are unioned rather than merged, with no cross-provider gap-filling at
  all.
- A user-facing want-list for metadata fields.
