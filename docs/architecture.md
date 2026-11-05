# Architecture and verification

Metadata Rules uses one pipeline for preview, manual runs, nightly scheduling, full-scan reconciliation, and instant events:

```text
Frozen candidate IDs → detached MediaFacts → three-valued evaluation → ActionPlan
                                                                  ↓
                                                     Preview or serialized execution
                                                                  ↓
                                                  Per-item journal and run reports
```

## Engine and server boundary

`Engine/Models.cs` defines versioned rule DTOs, typed values, fact availability, condition traces, and action plans. `FieldRegistry` provides the editor and validator with the same field types, operators, applicability, choices, and dependencies. `RuleEvaluator` preserves three-valued logic, correlated quantifiers, and strict first-branch precedence. `ActionPlanner` expands explicit preference/refresh scopes, preserves descendant overrides, and computes inherited effects from detached ancestry.

For `item.originalLanguage` and `series.originalLanguage`, confirmed `FactState.Absent` is a nonmatch (`False`) for `eq`, `neq`, `in`, and `notIn`. This permits lower-priority branches to evaluate without treating absence as a known language. `NotReady`, `ReadError`, and `Unsupported` remain `Unknown`, while `isMissing` can deliberately match confirmed absence. Other fields retain their existing absent-comparison semantics; this is a field-specific original-language policy.

Age rating (`rating.official`) and custom age rating (`rating.custom`) remain independent text fields with stable IDs. Their editor suggestions come from distinct values in stored library metadata, and users can still enter other text values. Matching uses each field's actual stored string rather than inferring a numerical age or merging the two rating fields.

`IMediaAdapter` isolates Jellyfin APIs. The v12.4 implementation freezes candidate IDs using `ILibraryManager.GetItemIds`; an empty global library selection returns no library-wide candidates. It reads stored original language and media streams, enumerates static local versions, and aggregates actual episodes without playback resolution or media probing. Metadata arrays that are not populated yet remain unavailable; a probed file with no subtitles yields a known empty collection. Enum facts become strings, and version file facts describe the selected version.

Before persistence the adapter requests a fresh entity with all DTO fields, images, and user data. Jellyfin's `StoredColumnsOnly` instances must never be saved because saving rewrites their owned rows. Preference persistence calls `ILibraryManager.UpdateItemAsync` directly, avoiding `Video.UpdateToRepositoryAsync` copying unrelated metadata onto local alternate versions. The complete saved instance is registered back into Jellyfin's cache so provider/descendant inheritance sees the new locale.

Refreshes use awaited `IProviderManager.RefreshSingleItem`, with explicit descendant IDs managed by the planner. Replacement sets full metadata mode, `ReplaceAllMetadata`, `ForceSave`, and `RemoveOldMetadata`; image replacement is enabled only by its separate action. Locked items are checked again immediately before persistence/refresh. Trickplay regeneration and remote content probing are disabled.

## Jobs, triggers, and recovery

`RunCoordinator` owns a bounded channel and one worker. It serializes writes/refreshes, coalesces full automatic reconciliations, waits for full scans to finish, rechecks current plans, and deduplicates targets. Applying a preview retains its candidate scope and revision; changed or truncated proposals require another preview. Job counts describe evaluated plans, while item outcomes describe completed action stages.

The nightly adapter supplies a default daily 03:00 trigger in server local time. The post-scan callback enqueues and returns immediately, avoiding a deadlock while Jellyfin still reports the scan as running. `InstantService` subscribes with hosted-service lifetime, captures detached event IDs, debounces pending work, and invalidates season/series/primary-version owners. It uses bounded retries and does not treat `ItemAdded` as proof that metadata is ready.

`RunStore` atomically persists summaries and journal entries in the plugin's own data directory. The journal separates preference persistence from refresh completion, retains recent locale/branch transitions for cycle detection, and marks uncertain stages interrupted after restart. Report retention does not evict decision keys. This suppresses repeated destructive replacement without claiming exactly-once provider effects. Configuration remains XML-compatible by storing the nested rules as JSON, validating every save, and using optimistic revisions.

## Security and resource limits

The custom controller applies Jellyfin's `RequiresElevation` policy to every route, including schema, facts inspection, validation, and previews. IDs are validated before use as storage paths. Request size, rule size/depth/node count, candidate count, job concurrency, instant queues, history, result retention, and pagination have bounds. Media-derived UI content uses text nodes. Rules support typed operators and cannot execute arbitrary code.

The controller exposes schema/configuration, facts inspection, validation, previews/runs, job summaries, filtered/paginated result rows, item traces, and cancellation under `/MetadataRules`. Jellyfin's authenticated dashboard client supplies credentials.

## What the automated checks establish

The suites exercise concrete contracts without a running Jellyfin service:

| Suite | Evidence |
| --- | --- |
| `tests/Engine` | Synthetic immutable facts cover Norwegian aliases, exact variants, original/audio separation, same-track predicates, boolean/quantifier/unknown semantics, strict precedence, validation limits, descendant inheritance, locked items, and stable decision keys. |
| `tests/Adapter` | Real v12.4 entity types with `DispatchProxy` service fakes verify read-only previews, full hydration before saving, isolated preference writes, cache replacement, per-version file/stream facts, Season ancestry, empty/not-ready distinctions, local source filtering, exact replacement flags, locks, and awaiting the single-item task. Unexpected provider, probe, and persistence calls fail the fixtures. |
| `tests/Integration` | A fake `IMediaAdapter` drives the real coordinator and file store through preview/application, repeat suppression, stale preview rejection, refresh failure/retry, locked items, journal recovery, and reviewed-scope enforcement. It also checks the nightly default and controller authorization attributes. |
| `tests/TriggerEvents` | Detached event capture tests verify owner IDs for episodes, seasons, and alternate editions, preservation of captured values after the event entity changes, and no library/provider reads while capturing event data. |
| `tests/Configuration` | The real Plugin/BasePlugin configuration path with a .NET XML serializer fixture verifies nested JSON persistence/reload, normalized library IDs, optimistic revisions, and preservation of active/on-disk settings after invalid native saves or stale editors. |
| `tests/Ratings` | Focused service/schema/adapter tests cover stored library-wide suggestions for age rating and custom age rating, independent field values, and actual text evaluation. |
| `tests/release.test.cjs` | Package metadata and contents, version/identity checks, official latest-version discovery, draft publication ordering, checksum validation, immutable public reruns, and catalog history protection. |

A Release build compiles against Jellyfin 12.4.0. CI resolves the official latest stable release and adds a distinct API build when needed. Package validation checks the actual generated ZIP and SHA-256. These checks establish API and orchestration contracts; they do not exercise Jellyfin's database, metadata savers, real providers, scheduler lifecycle, or live library events.

## Remaining running-server acceptance

Use a small Jellyfin v12.4 test library before enabling replacement on an existing collection:

1. Preview representative movies, a series/season/episode tree, and multiple local versions. Confirm original-language/audio evidence, inherited effects, locks, and no preview writes, probes, or provider calls.
2. Apply preferences with NFO saving enabled, restart, and verify stored/effective locales and unrelated metadata. Exercise actual providers for both replacement modes, including provider failures and NFO locale conflicts.
3. Verify a series-only refresh does not unexpectedly refresh episodes. Then select explicit descendant scope and inspect deduplicated target IDs.
4. Exercise full scans and late-arriving stored probe facts with instant mode enabled, including episode/version updates, deletion, overlapping triggers, cancellation, and shutdown/restart. Confirm queued/deferred states and journal recovery.
5. Change the nightly schedule in Jellyfin, save plugin settings, and confirm the custom schedule survives and executes in the displayed server timezone.

No running-server acceptance result is claimed by this repository yet. Local NFO behavior, provider output, filesystem path case rules, and server event timing remain environment-dependent integration concerns.
