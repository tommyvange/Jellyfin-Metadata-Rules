# Jellyfin Metadata Rules

[![Build and test](https://github.com/tommyvange/Jellyfin-Metadata-Rules/actions/workflows/build.yml/badge.svg)](https://github.com/tommyvange/Jellyfin-Metadata-Rules/actions/workflows/build.yml)
[![Stars](https://img.shields.io/github/stars/tommyvange/Jellyfin-Metadata-Rules)](https://github.com/tommyvange/Jellyfin-Metadata-Rules/stargazers)
[![Latest release](https://img.shields.io/github/v/release/tommyvange/Jellyfin-Metadata-Rules)](https://github.com/tommyvange/Jellyfin-Metadata-Rules/releases/latest)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

> [!NOTE]
> This plugin is a server-side plugin and makes no front-end changes directly.

![Metadata Rules](assets/Jellyfin-Metadata-Rules.png)

Set metadata language and country using rules based on your Jellyfin library. Build nested conditions, preview the results, and apply preferences with optional metadata and image replacement. Rules support movies, series, seasons, and episodes.

## Requirements

- **Jellyfin 12.4.0 or newer**, using .NET 10.
- Administrator access to configure the plugin and run rules.

Jellyfin supplies the runtime and server assemblies. Node.js is only needed for development and packaging.

## Install

1. Add **Metadata Rules** as a plugin repository in **Dashboard → Plugins → Repositories** using:

   ```text
   https://raw.githubusercontent.com/tommyvange/Jellyfin-Metadata-Rules/manifest/manifest.json
   ```

2. Install **Metadata Rules** from the catalog and restart Jellyfin.
3. Open **Dashboard → Plugins → Metadata Rules** to configure the plugin.

For manual installation, download the ZIP from [GitHub Releases](https://github.com/tommyvange/Jellyfin-Metadata-Rules/releases), stop Jellyfin, and extract all ZIP contents directly into `plugins/Metadata Rules_<version>/`. The DLL, `meta.json`, and plugin image must be at that directory's root. In the official Docker image, the plugins directory is normally `/config/plugins/`. Preserve the separate plugin configuration and data when upgrading, then restart Jellyfin.

## Getting started

1. In **General**, select the libraries to process and save. **No selected libraries means no library-wide processing.** The initial rule list is empty, so installing the plugin does not change your media.
2. In **Rules**, start with a template or create a ruleset. Choose item types, conditions, language, country, and optional refresh actions.
3. In **Preview**, validate the rules and run a dry run for selected libraries or item IDs. Review matching conditions, before-and-after values, inherited effects, refresh targets, and skipped or deferred items.
4. Apply the completed preview. The plugin checks the same items again and reports changed plans instead of applying stale proposals. A truncated preview cannot be applied; narrow the scope or increase the report limit and preview again.
5. Use **History** to search results, inspect item traces, export reports, cancel runs, and check saved preferences and refresh results.

Rules can be imported and exported as versioned JSON. Library references use stable IDs, with names shown as labels.

### Automatic processing

The global enable switch controls execution. Preview remains available while the plugin is disabled.

| Trigger | Default | Behavior |
| --- | --- | --- |
| Nightly | Enabled | Runs daily at 03:00 in the server's local timezone. Change this in **Dashboard → Scheduled Tasks → Apply Metadata Rules**. Saving plugin settings preserves the schedule. |
| Run after full library scan | Off | Processes items after a full library scan. Targeted folder refreshes may not trigger this hook. |
| Instant processing | Off | Groups incoming changes, waits for required metadata, and retries unavailable facts within a limited window. Later updates wake pending items; episode and version changes also update their parent evaluations. |

Deferred items remain available for later manual or nightly processing. All triggers share one worker, with one metadata refresh at a time.

## How rules work

Rulesets run in the displayed order. Each contains IF, ELSE IF, and an optional final ELSE branch. **The first matching branch wins**, and only its actions apply. A matching branch with no actions stops further rules.

If a branch needs facts that are not yet available, evaluation waits rather than falling through to a lower-priority branch or ruleset. ELSE runs only when the preceding branches are definitely false.

Conditions support nested AND, OR, and NOT groups, value and set comparisons, and any/all/none/count tests over versions, streams, and episodes. Conditions grouped around an audio track must all match the same track.

An empty collection differs from an unavailable one: ANY is false, NONE is true, and ALL requires at least one element. **Is missing** matches confirmed absence; unfinished or unreadable facts remain unknown.

### Languages and media tracks

**Original language (metadata)** uses the provider's stored value. It is separate from audio language and is never inferred from titles, countries, or the default audio track.

When an item's original language, or its inherited series original language, is confirmed absent, Equals, Does not equal, Is one of, and Is none of do not match. Evaluation can continue to a later branch, such as a Norwegian-audio rule. Use **Is missing** to match that absence explicitly. Unfinished metadata, read errors, and unsupported original-language facts remain unknown and defer later branches. This behavior for absent original language does not change comparisons for other fields.

The Norwegian templates support:

- **Norwegian, any variant:** accepts `no`/`nor`, `nb`/`nob`, and `nn`/`nno`.
- **Exact Norwegian, Bokmål, or Nynorsk:** keeps those language choices separate.
- **Norwegian audio:** checks local playable movie versions, or available episodes and their versions for a series. Preview identifies the matching version and stream.

Choose the output language explicitly, such as Norwegian `no` or Bokmål `nb`, and metadata country **Norway / NO**. Available choices come from Jellyfin's localization catalog.

### Other conditions

Rules can use library and item information, metadata preferences, genres, tags, studios, production locations, dates, ratings, provider IDs, people, file properties, media streams, series and episode information, and lock or location state. Each field supports operators suited to its type and availability.

Production locations use provider text rather than ISO country codes. Text comparisons use ordinal case-insensitive matching. Configure **Path case sensitivity** in General to match your media filesystem; the default ignores case on Windows and macOS and respects case elsewhere.

**Age rating** and **Custom age rating** use Jellyfin's separate stored fields. The builder suggests existing values while allowing text entry. Ratings use text comparisons, so labels such as `TV-14` and `PG-13` retain their meaning without being converted to a numerical age.

## Actions and refreshes

Language and country each support **Leave unchanged**, **Set catalog value**, and **Clear override → inherit**. Preference changes and refreshes have separate scopes. Descendant overrides are preserved unless you explicitly enable overwriting them, and preview shows inherited effects separately. Locked items are skipped before writes or refreshes.

Refresh is off by default. **Replace all metadata** requests full metadata replacement; **Replace all metadata & images** also requests image replacement. Replacement can remove fields that providers do not return. Metadata-only refresh avoids remote image replacement, although Jellyfin may still register local artwork.

Preferences are saved before refresh. Saving can invoke Jellyfin's metadata savers, including NFO savers, and local metadata or provider settings can affect the result. A completed refresh means Jellyfin's call returned; provider success and translated content are not guaranteed.

A conflicting stored language or country, or a repeating cycle between language, country, and matching rules, pauses automatic changes for that item and records the reason.

### Repeat processing

A persistent journal prevents repeated replacement when the decision has not changed. Rules can instead require a preference change or allow repeated runs with a minimum interval. Failed refresh stages remain retryable.

**Force this reviewed run** bypasses repeat suppression for one completed preview. After a restart, interrupted or uncertain actions are recorded and automatic retries are limited. External provider actions may have completed before an interruption.

## Data and limits

Settings use Jellyfin's plugin configuration. Rules are stored as versioned JSON within that configuration.

Reports and decision journals are stored under `<Jellyfin data directory>/metadata-rules/`:

- `jobs/` contains run summaries and item results. The defaults retain 50 completed runs and up to 10,000 result rows per run; both are configurable.
- `journal/` records item decisions and actions. It survives report cleanup so unchanged rules do not repeatedly replace metadata. Deleting it resets that protection.

Rules are limited to 256 KiB, 128 rulesets, 2,048 condition nodes, and 24 levels of nesting. Each job supports up to 100,000 candidate IDs. Processing queues have fixed limits. Custom endpoints require administrator access, and rules never execute user-written scripts.

## Build and test

Requires **.NET 10 SDK**, **Node.js 20+**, and `zip`/`unzip`. There are no npm dependencies to install.

```sh
npm test
npm run build
npm run package
```

The package command builds the plugin in Release mode and writes the ZIP and SHA-256 checksum to `artifacts/`. It targets the minimum Jellyfin version in `release/config.json`, currently **12.4.0**. Run `node scripts/validate-package.mjs` to validate an existing package.

Tests cover the rule engine, coordinator, Jellyfin adapter, event handling, configuration persistence, age ratings, and release tooling. They use synthetic data and mocked server services. Before releasing, also test a running server with provider refreshes, NFO behavior, scheduled tasks, and library events.

GitHub Actions builds against the minimum supported version and the latest stable Jellyfin server API when it differs. See [architecture and test coverage](docs/architecture.md) and [browser smoke tests](docs/ui-testing.md) for details.

## Releases and deployment

Update the four-part version in `Directory.Build.props`, the three-part version in `package.json`, and the release notes in `release/notes.md`. Commit and push to `main`, wait for **Build and test** to pass, then push a matching version tag, such as `v1.0.2.0`.

GitHub Actions runs the tests and compatibility checks, publishes the ZIP and SHA-256 checksum to GitHub Releases, and updates the Jellyfin catalog on the `manifest` branch. The release package targets the minimum supported Jellyfin version. The catalog retains earlier versions and includes Jellyfin's required MD5 checksum of the published ZIP.

Publishing uses GitHub Actions' built-in token. See [the release guide](docs/releasing.md) for versioning, publication retries, and catalog recovery.

## License

Licensed under the [GNU General Public License v3.0](LICENSE).
