# Publishing Metadata Rules

Metadata Rules uses the same deployment model as Jellyfin Infinite Scroll: downloadable ZIPs on GitHub Releases and a dedicated `manifest` branch containing the Jellyfin plugin catalog. The repository URL will be:

```text
https://raw.githubusercontent.com/tommyvange/Jellyfin-Metadata-Rules/manifest/manifest.json
```

The URL becomes available after the first successful release. GitHub Actions uses its built-in `GITHUB_TOKEN`; no custom secret, Pages setup, or separate hosting is required. Verification jobs have read-only permissions, and the publication job receives `contents: write` to publish the release and advance the catalog branch.

## Verify and package locally

Use .NET SDK 10, Node.js 20 or newer, and the `zip`/`unzip` command-line tools. No npm dependencies need installing.

```sh
npm test
npm run build
npm run package
node scripts/validate-package.mjs
```

`npm test` runs release-tooling tests, pure rule-engine cases, coordinator integration checks, adapter acceptance tests against Jellyfin 12.4 interfaces, and age-rating suggestion checks. `npm run package` compiles against the minimum supported Jellyfin API, validates the ZIP contents, and writes:

```text
artifacts/Jellyfin.Plugin.MetadataRules_1.0.1.0_jellyfin-12.4.0.zip
artifacts/Jellyfin.Plugin.MetadataRules_1.0.1.0_jellyfin-12.4.0.zip.sha256
```

The ZIP contains `Jellyfin.Plugin.MetadataRules.dll`, `meta.json`, and `Jellyfin-Metadata-Rules.png` at its root. Jellyfin supplies the server assemblies. Installed metadata enables updates and references the image by its local filename; the repository catalog references the image at the immutable release tag.

Extract all three files into the plugin's own directory under Jellyfin's `plugins` directory while the server is stopped, then start Jellyfin. Preserve the separate plugin configuration and data when upgrading. A catalog installation and Jellyfin restart is the normal installation path once the repository has a release.

## Publish a release

1. Set the four-part plugin version in `Directory.Build.props`. The current source version is `1.0.1.0`; subsequent published versions must increase. Keep the npm package version in `package.json` aligned with its first three components.
2. Update `release/notes.md`. Keep the plugin GUID and name stable in `release/config.json` and `Plugin.cs`.
3. Commit to `main`, push, and let **Build and test** pass.
4. Create and push the matching version tag:

   ```sh
   git tag -a v1.0.1.0 -m "Release 1.0.1.0"
   git push origin v1.0.1.0
   ```

The tag must point to a commit on `main` and match the exact source version. The **Publish release** workflow verifies the tagged source with the reusable build workflow, downloads its tested minimum-ABI package, and then publishes it. Creating a local package does not publish anything.

## Verification and publication behavior

**Build and test** runs on source pushes, pull requests, manual runs, release verification, and every Monday at 06:23 UTC. It reads the latest published stable Jellyfin version from the official release API. It builds the plugin against the fixed `12.4.0` minimum and against the latest stable API when that differs, keeping at most two compatibility targets. Failure to discover the latest release fails the check instead of substituting an older version. Server plugin API compilation needs no Jellyfin Web fixture downloads.

The catalog advertises one package per plugin version, compiled against the minimum supported ABI. Its `targetAbi` is `12.4.0.0`. A successful API build is useful compatibility evidence; acceptance testing on a running server is also required before claiming behavior on a new Jellyfin release. The test suite does not replace verification of provider refreshes, NFO persistence, scheduled tasks, and library-event behavior on a real server.

Publication creates a draft, uploads the ZIP and SHA-256 sidecar, downloads both, verifies the package and checksum, and only then publishes the draft. The catalog is generated from those published ZIP bytes using Jellyfin's required MD5 checksum, with the confirmed publication timestamp. It preserves previous versions and advances `manifest` with a normal commit and push. A package is never advertised before publication is confirmed.

`meta.json` describes an installed plugin; the repository's `manifest.json` describes available releases. They serve different purposes. The catalog's MD5 is required by Jellyfin's installer, while the sidecar provides an independent SHA-256 for manual download verification.

## Retry an interrupted publication

Rerun the failed Actions run, or select **Publish release → Run workflow** and enter the existing four-part tag.

A draft can have incomplete assets replaced. Once public, release assets are immutable: a retry downloads and validates the existing public assets, then completes or confirms the catalog update. It never uploads over a public ZIP. The catalog refuses changes to an already published version's URL, checksum, or ABI. Existing prereleases are rejected by the stable workflow.

Release jobs are serialized so catalog updates cannot compete. If the source workflow has changed since an old tag was created, rerun that tag's original workflow run; a manual dispatch uses the current workflow definition and needs its scripts to exist in the selected tag.

Never move a published tag or replace its public ZIP. Publish a new version for fixes. Raise `minimumJellyfinVersion`, the target framework, and the plugin version deliberately if a future server API break requires dropping an older ABI.
