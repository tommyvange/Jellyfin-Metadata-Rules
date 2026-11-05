# Settings page browser smoke test

The optional browser test runs the embedded settings page in headless Chromium or Google Chrome with mocked Jellyfin API responses. It exercises real DOM interactions without connecting to a Jellyfin server or touching media. The regular test suite does not require a browser or Playwright.

Use Node.js 20 or later. Install Playwright locally without changing the project's dependencies or creating a lockfile:

```sh
npm install --no-save --package-lock=false playwright
npm run test:ui
```

The test first looks for Playwright's installed Chromium, then an installed Google Chrome on macOS or Windows, then common Chromium/Chrome executable names on `PATH`. If no browser is present, either install Playwright's Chromium or choose an existing executable:

```sh
npx playwright install chromium
npm run test:ui
```

```sh
CHROME_PATH='/path/to/chrome' npm run test:ui
```

`PLAYWRIGHT_CHROMIUM_EXECUTABLE` also selects a browser and takes precedence over `CHROME_PATH`. The smoke test itself never installs packages, downloads a browser, or accesses the network.

If Playwright is already available outside this repository, point `PLAYWRIGHT_MODULE` at its package directory. This avoids a local dependency installation:

```sh
PLAYWRIGHT_MODULE='/path/to/node_modules/playwright' CHROME_PATH='/path/to/chrome' npm run test:ui
```

On PowerShell, set the environment variables before running the same npm command:

```powershell
$env:PLAYWRIGHT_MODULE = 'C:\tools\node_modules\playwright'
$env:CHROME_PATH = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
npm run test:ui
```

The script resolves source files relative to its own location, so it can also be invoked directly from another working directory:

```sh
node /path/to/Jellyfin-Metadata-Rules/tests/ui-smoke.cjs
```

## Coverage

The test checks the four rule templates, nested typed conditions, correct collection/operator IDs, clearing a preference action's obsolete set value, optimistic revision saves, single-item draft previews, stored-fact search, per-rule counts, trace source and stream evidence, text escaping, reviewed force execution, invalid import preservation, stale and truncated preview protections, authenticated custom API calls, and page lifecycle. It also checks the 390px layout for horizontal overflow.

Mock controller responses use PascalCase while the rules document uses camelCase, matching the plugin's API contracts. The fixture's media and job IDs have Jellyfin's GUID format. Browser errors and unexpected network requests fail the test.

The harness approximates Jellyfin's dark host background and font to make component screenshots useful. It writes `general-desktop.png`, `rules-desktop.png`, and `rules-mobile.png` to `artifacts/ui/`; a failed run also writes `failure.png` when a browser page is available. This directory is ignored by Git.

This is a browser interaction and layout test. The mock server accepts validation requests; the engine, adapter, controller, and trigger tests verify actual validation and server behavior. It does not establish compatibility with a live Jellyfin dashboard or prove metadata/provider outcomes. Use the real-server acceptance checklist for those checks.

For a dependency-free syntax check:

```sh
node --check tests/ui-smoke.cjs
```
