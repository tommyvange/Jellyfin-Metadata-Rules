'use strict';

// Optional browser test. The ordinary test suite only needs to syntax-check this file.
// See docs/ui-testing.md for Playwright and browser discovery options.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const repositoryRoot = path.resolve(__dirname, '..');
const settingsPath = path.join(repositoryRoot, 'src/Jellyfin.Plugin.MetadataRules/Web/config.html');
const screenshotDirectory = path.join(repositoryRoot, 'artifacts/ui');
const fixtureIds = {
    item: '11111111111111111111111111111111',
    preview: '22222222222222222222222222222222',
    run: '33333333333333333333333333333333',
    version: '44444444444444444444444444444444',
    library: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
};

function loadPlaywright() {
    const moduleName = process.env.PLAYWRIGHT_MODULE || 'playwright';
    try {
        return require(moduleName);
    } catch (error) {
        throw new Error(
            `Could not load Playwright from ${moduleName}. Install it with ` +
            '`npm install --no-save --package-lock=false playwright`, or set PLAYWRIGHT_MODULE ' +
            'to an existing Playwright package directory. See docs/ui-testing.md.',
            { cause: error }
        );
    }
}

function browserExecutable(chromium) {
    const override = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE || process.env.CHROME_PATH;
    if (override) {
        if (!fs.existsSync(override)) throw new Error(`Browser executable does not exist: ${override}`);
        return override;
    }

    const candidates = [chromium.executablePath()];
    if (process.platform === 'darwin') {
        candidates.push('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome');
    } else if (process.platform === 'win32') {
        for (const directory of [process.env.PROGRAMFILES, process.env['PROGRAMFILES(X86)'], process.env.LOCALAPPDATA]) {
            if (directory) candidates.push(path.join(directory, 'Google/Chrome/Application/chrome.exe'));
        }
    }
    for (const directory of (process.env.PATH || '').split(path.delimiter)) {
        for (const name of ['google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser']) {
            if (directory) candidates.push(path.join(directory, name));
        }
    }
    const found = candidates.find(candidate => fs.existsSync(candidate));
    if (found) return found;
    throw new Error(
        'No Chromium or Google Chrome executable was found. Run `npx playwright install chromium` ' +
        'or set CHROME_PATH to an installed browser. See docs/ui-testing.md.'
    );
}

// These mock responses deliberately use the controller's PascalCase response keys.
// No Jellyfin server, access token, media library, or external network request is used.
function installMockApi(ids) {
    window.__requests = [];
    const operators = [
        'eq', 'neq', 'in', 'notIn', 'contains', 'containsValue', 'containsAny',
        'containsAll', 'containsNone', 'range', 'isKnown', 'isMissing'
    ];
    const mediaKinds = ['Movie', 'Series', 'Episode', 'Season'];
    const fields = [
        ['item.kind', 'Item kind', 'enum', [...mediaKinds, 'Version', 'AudioTrack']],
        ['item.originalLanguage', 'Original language (metadata)', 'language', mediaKinds],
        ['audio.language', 'Audio language', 'language', ['AudioTrack']],
        ['audio.channels', 'Channels', 'number', ['AudioTrack']],
        ['file.path', 'Path', 'text', [...mediaKinds, 'Version']],
        ['item.genres', 'Genres', 'set', mediaKinds],
        ['item.tags', 'Tags', 'set', mediaKinds],
        ['item.premiereDate', 'Premiere date', 'date', mediaKinds],
        ['rating.official', 'Age rating', 'text', mediaKinds],
        ['rating.custom', 'Custom age rating', 'text', mediaKinds]
    ].map(([Id, DisplayName, Type, ItemKinds]) => ({
        Id, DisplayName, Type, ItemKinds, Operators: operators,
        Choices: Type === 'language' ? [{ Value: 'norwegian', Label: 'Norwegian, any variant' }] : [],
        HelpText: 'Stored source value'
    }));
    fields.find(field => field.Id === 'rating.official').Choices = ['12', '15', 'PG-13', 'TV-MA'].map(Value => ({ Value, Label: Value }));
    fields.find(field => field.Id === 'rating.custom').Choices = ['Family night', 'NO:15', '  Festival 18+  '].map(Value => ({ Value, Label: Value }));
    window.__config = {
        Enabled: true, NightlyEnabled: true, RunAfterScan: false, InstantEnabled: false,
        LibraryIds: [ids.library], PathCaseSensitive: false, ExcludedItemIds: [],
        RulesJson: JSON.stringify({ schemaVersion: 1, revision: '1', ruleSets: [] }),
        Revision: '1', HistoryLimit: 50, ResultsLimit: 10000, MinimumRefreshIntervalMinutes: 1440
    };
    const schema = {
        Fields: fields,
        Collections: [
            { Id: 'versions', DisplayName: 'Local playable versions', ItemKinds: ['Movie', 'Episode'], ElementKinds: ['Version'] },
            { Id: 'audioTracks', DisplayName: 'Audio tracks', ItemKinds: ['Movie', 'Episode', 'Version'], ElementKinds: ['AudioTrack'] },
            { Id: 'episodes', DisplayName: 'Episodes', ItemKinds: ['Series'], ElementKinds: ['Episode'] }
        ],
        Operators: operators.map(Id => ({ Id, DisplayName: Id })),
        Languages: [
            { Value: 'no', Label: 'Norwegian' },
            { Value: 'nb', Label: 'Norwegian Bokmål' },
            { Value: 'en', Label: 'English' }
        ],
        Countries: [{ Value: 'NO', Label: 'Norway' }, { Value: 'US', Label: 'United States' }],
        Libraries: [{ Id: ids.library, Name: 'Movies' }],
        Capabilities: { OriginalLanguage: true }, ServerTimeZone: 'Europe/Oslo'
    };
    const preview = {
        Id: ids.preview, State: 'Completed', Trigger: 'Manual', DryRun: true,
        StartedAt: '2026-10-31T12:00:00Z', Total: 1, Processed: 1, Revision: '1',
        Counts: { Examined: 1, Matched: 1, Changed: 1, Refresh: 1, Inherited: 1, Affected: 1 },
        CountsByRule: [{ RuleSetId: 'rule1', RuleSetName: 'Norwegian', Matched: 1, Changed: 1, Refresh: 1, Deferred: 0, Errors: 0 }]
    };
    window.__preview = preview;
    const run = { ...preview, Id: ids.run, DryRun: false };
    const result = {
        ItemId: ids.item, Title: '<img src=x onerror=alert(1)>', ItemKind: 'Movie', Status: 'Matched',
        Reason: 'Matched Norwegian audio', FactsTimestamp: '2026-10-31T12:00:00Z', Revision: '1',
        Plan: {
            ChangedCount: 1, RefreshCount: 1,
            Evaluation: {
                RuleSetName: 'Norwegian', BranchName: 'Audio',
                Trace: [{
                    RuleSetId: 'original-rule', BranchId: 'original-branch', Evaluated: true, Result: 'False',
                    Condition: {
                        Kind: 'Predicate', FieldId: 'item.originalLanguage', OperatorId: 'eq',
                        Actual: null, Availability: 'Absent', Expected: { Kind: 'text', Text: 'norwegian' },
                        Result: 'False', Evaluated: true,
                        Reason: 'Confirmed missing original language does not match Norwegian.'
                    }
                }, {
                    RuleSetId: 'audio-rule', BranchId: 'audio-branch', Evaluated: true, Result: 'True',
                    Condition: {
                        Kind: 'Any', Result: 'True', Evaluated: true,
                        Children: [{
                            Kind: 'Predicate', FieldId: 'item.tags', OperatorId: 'containsValue', Actual: [],
                            Availability: 'Known', Expected: { Kind: 'text', Text: 'Preferred' }, Result: 'False', Evaluated: true
                        }, {
                            Kind: 'Predicate', FieldId: 'rating.custom', OperatorId: 'eq', Actual: null,
                            Availability: 'NotReady', Expected: { Kind: 'text', Text: 'NO:15' }, Result: 'Unknown', Evaluated: true
                        }, {
                            Kind: 'Predicate', FieldId: 'file.path', OperatorId: 'eq', Actual: null,
                            Availability: 'Unsupported', Expected: { Kind: 'text', Text: '/Movies' }, Result: 'Unknown', Evaluated: true
                        }, {
                            Kind: 'Predicate', FieldId: 'rating.official', OperatorId: 'eq', Actual: null,
                            Availability: 'ReadError', Expected: { Kind: 'text', Text: 'PG-13' }, Result: 'Unknown', Evaluated: true
                        }, {
                            Kind: 'Predicate', FieldId: 'audio.language', OperatorId: 'eq', Actual: 'nob',
                            Availability: 'Known', Expected: { Kind: 'text', Text: 'norwegian' }, Result: 'True', Evaluated: true,
                            EvidenceItemId: ids.version, EvidenceTitle: 'Version A', StreamIndex: 2,
                            Source: 'Stored language nob normalized to nb'
                        }]
                    }
                }]
            },
            Targets: [{
                ItemId: ids.item, Title: 'Example', IsSource: true,
                Before: { Language: null, EffectiveLanguage: 'en', Country: null, EffectiveCountry: 'US' },
                After: { Language: 'no', EffectiveLanguage: 'no', Country: 'NO', EffectiveCountry: 'NO' },
                RefreshMode: 'metadata'
            }],
            InheritedEffects: []
        }
    };
    window.ApiClient = {
        accessToken: () => 'mock-dashboard-token',
        getUrl: route => route,
        ajax: async options => {
            const route = options.url;
            const body = options.data ? JSON.parse(options.data) : null;
            window.__requests.push({ path: route, type: options.type, body, headers: options.headers });
            if (route.endsWith('/Schema')) return schema;
            if (route.endsWith('/Facts/' + ids.item)) return {
                ItemId: ids.item, Title: 'Example Film', ItemKind: 'Movie', Timestamp: '2026-10-31T12:00:00Z',
                Fields: {
                    'item.title': { State: 'Known', Value: 'Example Film', Source: 'Jellyfin' },
                    'item.originalLanguage': { State: 'NotReady', Reason: 'Metadata not ready' }
                },
                Collections: [{
                    Id: 'versions', State: 'Known', Count: 1,
                    Elements: [{
                        ItemId: ids.version, Title: 'Version A',
                        Fields: { 'audio.language': { State: 'Known', Value: 'nob' } }, Collections: []
                    }]
                }]
            };
            if (route.endsWith('/Configuration')) {
                if (options.type === 'PUT') window.__config = { ...body, Revision: '2' };
                return window.__config;
            }
            if (route.endsWith('/Validate')) return { IsValid: true, Errors: [], Warnings: [] };
            if (route.endsWith('/Preview')) return { JobId: ids.preview };
            if (route.endsWith('/Run')) return { JobId: ids.run };
            if (route.endsWith('/Jobs')) return [preview, run];
            if (route.includes('/Items?')) return { Items: [result], TotalRecordCount: 1 };
            if (route.endsWith('/Items/' + ids.item)) return result;
            if (route.endsWith('/Jobs/' + ids.preview)) return preview;
            if (route.endsWith('/Jobs/' + ids.run)) return run;
            throw new Error('Unexpected mock API route: ' + route);
        }
    };
}

async function runSmokeTest(page) {
    const errors = [];
    const dialogs = [];
    const networkRequests = [];
    page.setDefaultTimeout(10_000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => { dialogs.push(dialog.message()); void dialog.accept(); });
    page.on('request', request => networkRequests.push(request.url()));
    await page.evaluate(installMockApi, fixtureIds);
    await page.setContent(fs.readFileSync(settingsPath, 'utf8'));
    await page.evaluate(() => document.querySelector('#metadataRulesPage').dispatchEvent(new Event('viewshow')));
    await page.getByText('All changes saved', { exact: true }).waitFor();

    // Add every built-in template, group a condition, and preserve the engine's typed AST.
    await page.getByRole('tab', { name: 'Rules', exact: true }).click();
    for (const label of [
        'Norwegian original language', 'Norwegian audio (movie or series)',
        'Path-based localization', 'Genre / tag exception'
    ]) {
        await page.getByRole('combobox', { name: 'Rule template' }).selectOption({ label });
    }
    assert.equal(await page.locator('.mr-rule').count(), 4);
    await page.getByRole('button', { name: 'Validate draft', exact: true }).first().click();
    await page.getByText('Draft is valid.', { exact: true }).waitFor();
    await page.locator('.mr-node select[aria-label="Condition type"]').first().selectOption('All');
    await page.getByRole('button', { name: 'Add condition', exact: true }).first().click();
    assert.equal(await page.locator('.mr-rule').first().locator('.mr-node').count(), 3);
    // Rating suggestions come from each field's stored values, while arbitrary text remains valid.
    const firstRule = page.locator('.mr-rule').first();
    async function chooseRating(predicateIndex, fieldId, displayName, expectedChoices, selectedValue) {
        const fieldInput = firstRule.locator('.mr-predicate').nth(predicateIndex).locator('input[list]').first();
        await fieldInput.fill(displayName + ' · ' + fieldId);
        await fieldInput.dispatchEvent('change');
        const valueInput = firstRule.locator('.mr-predicate').nth(predicateIndex).locator('input[list]').last();
        assert.equal(await valueInput.getAttribute('placeholder'), 'Search existing ratings or type text');
        const listId = await valueInput.getAttribute('list');
        const choices = await page.locator('#' + listId + ' option').evaluateAll(options => options.map(option => option.value));
        assert.deepEqual(choices, expectedChoices, "Only this rating field's distinct stored values are suggested");
        await valueInput.fill(selectedValue);
        await valueInput.dispatchEvent('change');
    }
    await chooseRating(1, 'rating.official', 'Age rating', ['12', '15', 'PG-13', 'TV-MA'], 'PG-13');
    await page.getByRole('button', { name: 'Add condition', exact: true }).first().click();
    await chooseRating(2, 'rating.custom', 'Custom age rating', ['Family night', 'NO:15', '  Festival 18+  '], '  Festival 18+  ');
    await page.locator('.mr-rule').first().locator('.mr-actions select').first().selectOption('leave');
    await page.getByRole('button', { name: 'Save settings & rules' }).click();
    await page.getByText('All changes saved', { exact: true }).waitFor();
    const saved = await page.evaluate(() => window.__requests.findLast(request => request.type === 'PUT').body);
    const document = JSON.parse(saved.RulesJson);
    assert.equal(document.ruleSets.length, 4);
    assert.equal(Object.hasOwn(document.ruleSets[0].branches[0].actions.language, 'value'), false,
        'Leave mode must clear a previous set value');
    assert.equal(document.ruleSets[1].branches.length, 2);
    assert.equal(document.ruleSets[1].branches[0].condition.children[1].child.collectionId, 'audioTracks');
    assert.equal(document.ruleSets[3].branches[0].condition.children[0].operatorId, 'containsValue');
    assert.equal(saved.Revision, '1', 'Save carries the revision that was loaded');
    const ratingConditions = document.ruleSets[0].branches[0].condition.children;
    assert.equal(ratingConditions[1].fieldId, 'rating.official');
    assert.deepEqual(ratingConditions[1].value, { kind: 'text', text: 'PG-13' });
    assert.equal(ratingConditions[2].fieldId, 'rating.custom');
    assert.deepEqual(ratingConditions[2].value, { kind: 'text', text: '  Festival 18+  ' },
        'Selecting a stored custom rating preserves its exact value, including whitespace');
    await firstRule.locator('.mr-predicate').nth(1).locator('select').selectOption('contains');
    let ratingText = firstRule.locator('.mr-predicate').nth(1).locator('input[list]').last();
    await ratingText.fill('  PG-  ');
    await ratingText.dispatchEvent('change');
    await page.getByRole('button', { name: 'Validate draft', exact: true }).first().click();
    await page.getByText('Draft is valid.', { exact: true }).waitFor();
    const comparisonDraft = await page.evaluate(() => JSON.parse(window.__requests.findLast(request => request.path.endsWith('/Validate')).body.rulesJson));
    const freeformRating = comparisonDraft.ruleSets[0].branches[0].condition.children[1];
    assert.equal(freeformRating.operatorId, 'contains');
    assert.equal(freeformRating.value.text, 'PG-', 'Contains still accepts a freeform fragment absent from the suggestions');
    await firstRule.locator('.mr-predicate').nth(1).locator('select').selectOption('eq');
    ratingText = firstRule.locator('.mr-predicate').nth(1).locator('input[list]').last();
    await ratingText.fill('PG-13');
    await ratingText.dispatchEvent('change');


    // Inspect stored facts before testing the current draft against a frozen single-item scope.
    await page.getByRole('tab', { name: 'Preview', exact: true }).click();
    await page.locator('#mrPreviewIds').fill(fixtureIds.item);
    await page.getByRole('button', { name: 'Inspect stored facts' }).click();
    await page.getByText('Stored facts: Example Film', { exact: true }).waitFor();
    await page.getByRole('textbox', { name: 'Search stored facts' }).fill('original');
    await page.getByText('Metadata not ready', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Start dry-run' }).click();
    await page.getByRole('button', { name: 'Apply this preview' }).waitFor();
    await page.getByText('Counts by winning ruleset', { exact: true }).waitFor();
    const previewRequest = await page.evaluate(() => window.__requests.find(request => request.path.endsWith('/Preview')).body);
    assert.deepEqual(previewRequest.itemIds, [fixtureIds.item]);
    assert.deepEqual(JSON.parse(previewRequest.rulesJson).ruleSets, document.ruleSets);

    // Changing the draft invalidates Apply until the displayed proposal matches again.
    await page.getByRole('tab', { name: 'Rules', exact: true }).click();
    const ruleName = page.locator('.mr-rule-head .mr-input').first();
    const oldName = await ruleName.inputValue();
    await ruleName.fill(oldName + ' changed');
    await page.getByRole('tab', { name: 'Preview', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Apply this preview' }).isDisabled(), true,
        'A changed draft blocks a stale preview');
    await page.getByRole('tab', { name: 'Rules', exact: true }).click();
    await page.locator('.mr-rule-head .mr-input').first().fill(oldName);
    await page.getByRole('tab', { name: 'Preview', exact: true }).click();
    assert.equal(await page.getByRole('button', { name: 'Apply this preview' }).isEnabled(), true);

    // Media-derived strings remain text. Trace detail retains source and stream evidence.
    assert.equal(await page.locator('#mrPreviewResults img').count(), 0, 'Media text must not become markup');
    await page.getByRole('button', { name: '<img src=x onerror=alert(1)>' }).click();
    await page.getByText('Evidence: Version A · stream 2', { exact: true }).waitFor();
    await page.getByText('Source: Stored language nob normalized to nb', { exact: true }).waitFor();
    await page.getByText('Actual: Missing · Expected: norwegian', { exact: true }).waitFor();
    await page.getByText('Actual: Not ready · Expected: NO:15', { exact: true }).waitFor();
    await page.getByText('Actual: Unavailable (unsupported) · Expected: /Movies', { exact: true }).waitFor();
    await page.getByText('Actual: Unavailable (read error) · Expected: PG-13', { exact: true }).waitFor();
    await page.getByText('Actual: (empty) · Expected: Preferred', { exact: true }).waitFor();
    const traceText = await page.locator('.mr-details').textContent();
    assert.equal(traceText.includes('Actual: inherit'), false, 'Missing facts must not imply inherited preferences');
    const conditionHeaders = await page.locator('.mr-details .mr-trace > .mr-row').allTextContents();
    const missingOriginalIndex = conditionHeaders.findIndex(header => header.includes('Original language (metadata) eq'));
    const matchingAudioIndex = conditionHeaders.findIndex(header => header.includes('Audio language eq'));
    assert.ok(missingOriginalIndex >= 0 && conditionHeaders[missingOriginalIndex].includes('False'),
        'Confirmed missing original language displays a False comparison');
    assert.ok(matchingAudioIndex > missingOriginalIndex && conditionHeaders[matchingAudioIndex].includes('True'),
        'The trace displays the following matching audio rule');
    assert.ok(await page.locator('.mr-details').getByRole('cell', { name: 'inherit', exact: true }).count() > 0,
        'Locale preference tables still label cleared overrides as inherit');

    await page.getByRole('checkbox', { name: /Force this reviewed run/ }).check();
    await page.getByRole('button', { name: 'Apply this preview' }).click();
    await page.locator('#mrHistoryResults').waitFor();
    const execution = await page.evaluate(() => window.__requests.find(request => request.path.endsWith('/Run')).body);
    assert.equal(execution.previewJobId, fixtureIds.preview);
    assert.equal(execution.expectedRevision, '2');
    assert.equal(execution.force, true);
    assert.equal(dialogs.length, 1, 'Only the explicit reviewed execution opens a confirmation');

    // Invalid imports preserve the draft, and incomplete reports cannot be applied.
    await page.getByRole('tab', { name: 'Rules', exact: true }).click();
    await page.locator('#mrImport').setInputFiles({
        name: 'invalid.json', mimeType: 'application/json', buffer: Buffer.from('{"notRules":true}')
    });
    await page.getByText('Import must be a versioned rule document with ruleSets.').waitFor();
    assert.equal(await page.locator('.mr-rule').count(), 4, 'Invalid import keeps the active draft');
    await page.getByRole('tab', { name: 'Preview', exact: true }).click();
    await page.evaluate(() => { window.__preview.ResultsTruncated = true; });
    await page.getByRole('button', { name: 'Start dry-run' }).click();
    await page.getByText(/Result retention limit reached/).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Apply this preview' }).isDisabled(), true,
        'A truncated preview cannot be applied');

    // Approximate Jellyfin's host styling; the plugin supplies its own component CSS.
    await page.getByRole('tab', { name: 'General', exact: true }).click();
    await page.evaluate(() => {
        document.querySelector('#mrMessage').textContent = '';
        document.body.style = 'margin:0;background:#101010;color:#eee;font-family:Segoe UI,sans-serif';
    });
    await page.screenshot({ path: path.join(screenshotDirectory, 'general-desktop.png'), fullPage: true, animations: 'disabled' });
    await page.getByRole('tab', { name: 'Rules', exact: true }).click();
    await page.screenshot({ path: path.join(screenshotDirectory, 'rules-desktop.png'), fullPage: true, animations: 'disabled' });
    await page.setViewportSize({ width: 390, height: 844 });
    const width = await page.evaluate(() => ({ width: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
    assert.ok(width.scroll <= width.width + 1, `Mobile page overflows: ${JSON.stringify(width)}`);
    await page.screenshot({ path: path.join(screenshotDirectory, 'rules-mobile.png'), fullPage: true, animations: 'disabled' });
    assert.deepEqual(errors, [], 'The settings page must not raise browser errors');
    assert.deepEqual(networkRequests, [], 'The smoke harness must not access the network');
    const authenticated = await page.evaluate(() => window.__requests.every(request => request.headers['X-Emby-Token'] === 'mock-dashboard-token'));
    assert.equal(authenticated, true, 'Custom endpoints use the dashboard access token');
    await page.evaluate(() => document.querySelector('#metadataRulesPage').dispatchEvent(new Event('viewhide')));
}

async function main() {
    const { chromium } = loadPlaywright();
    const executablePath = browserExecutable(chromium);
    fs.mkdirSync(screenshotDirectory, { recursive: true });
    const browser = await chromium.launch({ headless: true, executablePath });
    let page;
    try {
        page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
        await runSmokeTest(page);
        console.log('PASS: templates, typed nested AST, stored rating suggestions, optimistic save, stored facts, per-rule counts, stale/truncated preview guards, safe media text, missing-fact trace states, evidence, reviewed execution, invalid imports, mobile layout and lifecycle.');
        console.log('Screenshots: artifacts/ui');
    } catch (error) {
        if (page) await page.screenshot({ path: path.join(screenshotDirectory, 'failure.png'), fullPage: true, animations: 'disabled' }).catch(() => {});
        throw error;
    } finally {
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
