'use strict';

const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { execFileSync } = require('node:child_process');
const { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');
const test = require('node:test');

const helpers = Promise.all([
    import('../scripts/release-info.mjs'),
    import('../scripts/generate-manifest.mjs'),
    import('../scripts/publish-release.mjs'),
    import('../scripts/compatibility-info.mjs')
]).then(([releaseInfo, manifest, publication, compatibility]) => ({ ...releaseInfo, ...manifest, ...publication, ...compatibility }));

const imageBytes = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jKwoAAAAASUVORK5CYII=', 'base64');

function releaseInfo(version = '1.1.0.0') {
    return {
        version, tag: `v${version}`,
        config: {
            guid: '7960b22c-4e70-4a69-a57a-9b3b68dfdd38', name: 'Metadata Rules',
            description: 'Automatic metadata language and country rules.', overview: 'Apply metadata preferences with rules.',
            owner: 'Metadata Rules contributors', category: 'General',
            imagePath: 'assets/Jellyfin-Metadata-Rules.png',
            assemblyName: 'Jellyfin.Plugin.MetadataRules', minimumJellyfinVersion: '12.4.0',
            compatibility: {
                latestReleaseUrl: 'https://api.github.com/repos/jellyfin/jellyfin/releases/latest'
            }
        }
    };
}

function temporaryDirectory(t) {
    const directory = mkdtempSync(path.join(tmpdir(), 'metadata-rules-release-test-'));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    return directory;
}

function writeReleaseConfig(directory, info) {
    mkdirSync(path.join(directory, 'release'), { recursive: true });
    writeFileSync(path.join(directory, 'Directory.Build.props'),
        `<Project><PropertyGroup><Version>${info.version}</Version></PropertyGroup></Project>\n`);
    writeFileSync(path.join(directory, 'release/config.json'), JSON.stringify(info.config));
    const sourceDirectory = path.join(directory, 'src/Jellyfin.Plugin.MetadataRules');
    mkdirSync(sourceDirectory, { recursive: true });
    writeFileSync(path.join(sourceDirectory, 'Plugin.cs'),
        `public const string PluginId = "${info.config.guid}";\npublic override string Name => "${info.config.name}";\n`);
}

function writePackage(directory, filename, bytes) {
    writeFileSync(path.join(directory, filename), bytes);
    writeFileSync(path.join(directory, filename + '.sha256'),
        `${createHash('sha256').update(bytes).digest('hex')}  ${filename}\n`);
}

function catalog(createCatalog, info = releaseInfo(), zipBytes = Buffer.from('tested release bytes')) {
    return createCatalog({
        info, repository: 'owner/Jellyfin-Metadata-Rules', tag: info.tag, zipBytes,
        timestamp: '2026-10-13T14:30:00+02:00', changelog: 'Initial metadata rules.'
    });
}

function publicationFixture(t, packageFileName, options = {}) {
    const info = releaseInfo();
    const directory = temporaryDirectory(t);
    const filename = packageFileName(info);
    const bytes = Buffer.from('locally tested package');
    writePackage(directory, filename, bytes);
    const notesPath = path.join(directory, 'notes.md');
    writeFileSync(notesPath, 'Release notes.\n');
    const calls = [];
    const validations = [];
    const publicRelease = {
        isDraft: false, isPrerelease: false, assets: [filename, filename + '.sha256'].map(name => ({ name })),
        publishedAt: '2026-10-13T12:30:00Z', url: `https://github.com/owner/Jellyfin-Metadata-Rules/releases/tag/${info.tag}`
    };
    let inspections = 0;
    const gh = (args, allowMissing = false) => {
        calls.push({ args: [...args], allowMissing });
        const command = args[1];
        if (command === 'view') {
            inspections++;
            return inspections === 1 ?
                (options.initialRelease === undefined ? null : JSON.stringify(options.initialRelease)) :
                JSON.stringify(options.confirmedRelease || publicRelease);
        }
        if (command === 'upload' && options.uploadError) throw new Error('Upload failed.');
        if (command === 'download') {
            if (options.downloadError) throw new Error('Download failed.');
            if (options.downloadBytes) writePackage(directory, filename, options.downloadBytes);
            if (options.corruptDownload) writeFileSync(path.join(directory, filename), 'corrupted remote bytes');
        }
        return '';
    };
    const validate = (zipPath, metadata) => {
        validations.push({ zipPath, metadata, bytes: readFileSync(zipPath) });
        if (options.validationError) throw new Error('Invalid package metadata.');
    };
    return {
        info, directory, filename, bytes, notesPath, publicRelease, calls, validations,
        arguments: { repository: 'owner/Jellyfin-Metadata-Rules', tag: info.tag, directory, info, notesPath, gh, validate }
    };
}

function commands(fixture) { return fixture.calls.map(call => call.args[1]); }

test('release metadata derives the exact version and tag from shared build properties', async t => {
    const { readReleaseInfo, packageFileName, validateTag } = await helpers;
    const directory = temporaryDirectory(t);
    const info = releaseInfo('1.10.2.3');
    writeReleaseConfig(directory, info);
    assert.deepEqual(readReleaseInfo(directory), info);
    assert.equal(packageFileName(info), 'Jellyfin.Plugin.MetadataRules_1.10.2.3_jellyfin-12.4.0.zip');
    assert.equal(packageFileName(info, '12.5.0'), 'Jellyfin.Plugin.MetadataRules_1.10.2.3_jellyfin-12.5.0.zip');
    assert.doesNotThrow(() => validateTag('v1.10.2.3', info));
    for (const tag of ['1.10.2.3', 'v1.10.2', 'v1.10.2.4']) assert.throws(() => validateTag(tag, info), /Release tag must be/);
    for (const target of ['11.9.0', '12.3.9', '12.0', '12.4.0.0', 'invalid']) assert.throws(() => packageFileName(info, target), /Jellyfin version must be/);
});

test('release metadata rejects a missing build version and invalid minimum/latest policy', async t => {
    const { readReleaseInfo } = await helpers;
    const directory = temporaryDirectory(t);
    const info = releaseInfo();
    writeReleaseConfig(directory, info);
    writeFileSync(path.join(directory, 'Directory.Build.props'), '<Project><Version>1.1.0</Version></Project>');
    assert.throws(() => readReleaseInfo(directory), /four-part plugin Version/);
    const invalidConfigs = [
        { ...info.config, compatibility: undefined },
        { ...info.config, minimumJellyfinVersion: '12.0' },
        { ...info.config, minimumJellyfinVersion: '11.9.0' },
        { ...info.config, minimumJellyfinVersion: '12.3.0' },
        { ...info.config, compatibility: { ...info.config.compatibility, latestReleaseUrl: 'https://example.com/latest' } }
    ];
    for (const config of invalidConfigs) {
        writeReleaseConfig(directory, { ...info, config });
        assert.throws(() => readReleaseInfo(directory), /minimum Jellyfin version|compatibility policy/);
    }
});

test('version comparisons use numeric components rather than lexical ordering', async () => {
    const { compareVersions } = await helpers;
    assert.equal(compareVersions('1.10.0.0', '1.9.9.9'), 1);
    assert.equal(compareVersions('12.2.0', '12.10.0'), -1);
    assert.equal(compareVersions('1.2', '1.2.0.0'), 0);
    assert.equal(compareVersions('2.0.0.0', '1.99.99.99'), 1);
});

test('catalog uses the stable GUID, minimum ABI, exact release URL, UTC timestamp, and Jellyfin MD5', async () => {
    const { createCatalog, packageFileName } = await helpers;
    const info = releaseInfo();
    const zipBytes = Buffer.from('known release bytes');
    const manifest = catalog(createCatalog, info, zipBytes);
    assert.equal(manifest.length, 1);
    assert.equal(manifest[0].guid, info.config.guid);
    assert.equal(manifest[0].name, info.config.name);
    assert.equal(manifest[0].imageUrl,
        'https://raw.githubusercontent.com/owner/Jellyfin-Metadata-Rules/v1.1.0.0/assets/Jellyfin-Metadata-Rules.png');
    assert.deepEqual(manifest[0].versions, [{
        version: '1.1.0.0', changelog: 'Initial metadata rules.', targetAbi: '12.4.0.0',
        sourceUrl: `https://github.com/owner/Jellyfin-Metadata-Rules/releases/download/v1.1.0.0/${packageFileName(info)}`,
        checksum: createHash('md5').update(zipBytes).digest('hex'), timestamp: '2026-10-13T12:30:00.000Z'
    }]);
    assert.equal(manifest[0].versions[0].checksum.length, 32);
    assert.notEqual(manifest[0].versions[0].checksum, createHash('sha256').update(zipBytes).digest('hex'));
    const args = { info, repository: 'owner/repo', tag: info.tag, zipBytes, timestamp: '2026-10-13T12:30:00Z', changelog: '' };
    assert.throws(() => createCatalog({ ...args, tag: 'v1.0.0.0' }), /Release tag must be/);
    for (const repository of ['owner', 'https://github.com/owner/repo', 'owner/repo/extra', 'owner/repo name']) {
        assert.throws(() => createCatalog({ ...args, repository }), /owner\/name/);
    }
    assert.throws(() => createCatalog({ ...args, timestamp: 'not a date' }), /valid release timestamp/);
});

test('catalog merging preserves history and unrelated plugins, sorts numerically, and is idempotent', async () => {
    const { createCatalog, mergeCatalog } = await helpers;
    const older = catalog(createCatalog, releaseInfo('1.9.0.0'), Buffer.from('older bytes'));
    const next = catalog(createCatalog, releaseInfo('1.10.0.0'), Buffer.from('newer bytes'));
    const unrelated = { guid: 'another-plugin', name: 'Another plugin', versions: [{ version: '4.0.0.0' }] };
    const previous = [...older, unrelated];
    const beforePrevious = structuredClone(previous);
    const beforeNext = structuredClone(next);
    const merged = mergeCatalog(previous, next);
    assert.deepEqual(merged[0].versions.map(version => version.version), ['1.10.0.0', '1.9.0.0']);
    assert.deepEqual(merged[0].versions[1], older[0].versions[0]);
    assert.deepEqual(merged[1], unrelated);
    assert.deepEqual(previous, beforePrevious);
    assert.deepEqual(next, beforeNext);
    assert.deepEqual(mergeCatalog(merged, next), merged);
    const duplicate = structuredClone(next);
    duplicate[0].versions[0].checksum = duplicate[0].versions[0].checksum.toUpperCase();
    duplicate[0].versions[0].timestamp = '2030-01-01T00:00:00.000Z';
    duplicate[0].versions[0].changelog = 'A rerun should keep the published entry.';
    assert.deepEqual(mergeCatalog(merged, duplicate), merged);
});

test('catalog refuses changing published bytes, ABI, URL, or the installed plugin name', async () => {
    const { createCatalog, mergeCatalog } = await helpers;
    const published = catalog(createCatalog);
    const before = structuredClone(published);
    for (const [property, replacement] of [
        ['checksum', '0'.repeat(32)], ['targetAbi', '12.1.0.0'], ['sourceUrl', 'https://github.com/other/repo/release.zip']
    ]) {
        const changed = structuredClone(published);
        changed[0].versions[0][property] = replacement;
        assert.throws(() => mergeCatalog(published, changed), /already exists with different release contents/);
        assert.deepEqual(published, before);
    }
    const renamed = structuredClone(published);
    renamed[0].name = 'Renamed Rules';
    assert.throws(() => mergeCatalog(published, renamed), /plugin name stable/);
    assert.throws(() => mergeCatalog({}, published), /catalogs must be arrays/);
});

test('package validation checks the metadata needed for repository installs', async t => {
    const { validatePackage } = await helpers;
    const directory = temporaryDirectory(t);
    const info = releaseInfo();
    const metadata = {
        guid: info.config.guid, name: info.config.name, version: info.version, targetAbi: '12.4.0.0',
        autoUpdate: true, assemblies: [info.config.assemblyName + '.dll'], imagePath: path.basename(info.config.imagePath)
    };
    const zipPath = path.join(directory, 'metadata.zip');
    function packageMetadata(meta) {
        writeFileSync(path.join(directory, 'meta.json'), JSON.stringify(meta));
        rmSync(zipPath, { force: true });
        const dllName = info.config.assemblyName + '.dll';
        writeFileSync(path.join(directory, dllName), 'nonempty test DLL bytes');
        const imageName = path.basename(info.config.imagePath);
        writeFileSync(path.join(directory, imageName), imageBytes);
        execFileSync('zip', ['-q', zipPath, 'meta.json', dllName, imageName], { cwd: directory });
    }
    packageMetadata(metadata);
    assert.doesNotThrow(() => validatePackage(zipPath, info));
    for (const [property, value] of [
        ['guid', 'different-guid'], ['name', 'Different name'], ['version', '1.0.0.0'],
        ['targetAbi', '12.2.0.0'], ['autoUpdate', false], ['assemblies', ['Unexpected.dll']],
        ['assemblies', [info.config.assemblyName + '.dll', 'Extra.dll']],
        ['imagePath', 'Different-image.png'], ['imagePath', info.config.imagePath], ['imagePath', undefined]
    ]) {
        packageMetadata({ ...metadata, [property]: value });
        assert.throws(() => validatePackage(zipPath, info), /ZIP metadata does not match/);
    }
});

test('release sidecar verifies SHA-256, its exact filename, and the actual bytes', async t => {
    const { verifyChecksum, packageFileName } = await helpers;
    const directory = temporaryDirectory(t);
    const filename = packageFileName(releaseInfo());
    const bytes = Buffer.from('release payload');
    writePackage(directory, filename, bytes);
    const sha256 = createHash('sha256').update(bytes).digest('hex');
    assert.equal(verifyChecksum(directory, filename), sha256);
    writeFileSync(path.join(directory, filename + '.sha256'), `${createHash('md5').update(bytes).digest('hex')}  ${filename}\n`);
    assert.throws(() => verifyChecksum(directory, filename), /SHA-256 checksum/);
    writeFileSync(path.join(directory, filename + '.sha256'), `${sha256}  wrong-name.zip\n`);
    assert.throws(() => verifyChecksum(directory, filename), /SHA-256 checksum/);
    writePackage(directory, filename, bytes);
    writeFileSync(path.join(directory, filename), 'changed bytes');
    assert.throws(() => verifyChecksum(directory, filename), /SHA-256 checksum/);
});

test('new release is drafted, uploaded, downloaded and verified, published, and confirmed in order', async t => {
    const { publishRelease, packageFileName } = await helpers;
    const fixture = publicationFixture(t, packageFileName);
    assert.deepEqual(publishRelease(fixture.arguments), {
        timestamp: fixture.publicRelease.publishedAt, url: fixture.publicRelease.url
    });
    assert.deepEqual(commands(fixture), ['view', 'create', 'upload', 'download', 'edit', 'view']);
    assert.equal(fixture.calls[0].allowMissing, true);
    assert.ok(fixture.calls[0].args[fixture.calls[0].args.indexOf('--json') + 1].split(',').includes('isPrerelease'));
    assert.equal(fixture.calls.at(-1).allowMissing, false);
    const create = fixture.calls[1].args;
    assert.ok(create.includes('--draft'));
    assert.ok(create.includes('--verify-tag'));
    assert.equal(create[create.indexOf('--notes-file') + 1], fixture.notesPath);
    assert.equal(create[create.indexOf('--title') + 1], 'Metadata Rules 1.1.0.0');
    for (const call of fixture.calls) {
        assert.equal(call.args[2], fixture.info.tag);
        assert.equal(call.args[call.args.indexOf('--repo') + 1], fixture.arguments.repository);
    }
    assert.deepEqual(fixture.calls[2].args.slice(-2), [
        path.join(fixture.directory, fixture.filename), path.join(fixture.directory, fixture.filename + '.sha256')
    ]);
    assert.deepEqual(fixture.calls[3].args.slice(-4), ['--pattern', fixture.filename, '--pattern', fixture.filename + '.sha256']);
    assert.ok(fixture.calls[4].args.includes('--draft=false'));
    assert.equal(fixture.validations.length, 1);
    assert.equal(fixture.validations[0].zipPath, path.join(fixture.directory, fixture.filename));
    assert.deepEqual(fixture.validations[0].bytes, fixture.bytes);
});

test('an existing draft resumes upload and publication without recreating the release', async t => {
    const { publishRelease, packageFileName } = await helpers;
    const fixture = publicationFixture(t, packageFileName, { initialRelease: { isDraft: true, assets: [] } });
    publishRelease(fixture.arguments);
    assert.deepEqual(commands(fixture), ['view', 'upload', 'download', 'edit', 'view']);
});

test('published reruns use existing downloaded bytes and perform no modifying release commands', async t => {
    const { publishRelease, packageFileName } = await helpers;
    const filename = packageFileName(releaseInfo());
    const remoteBytes = Buffer.from('immutable previously published bytes');
    const published = {
        isDraft: false, isPrerelease: false, assets: [filename, filename + '.sha256'].map(name => ({ name })),
        publishedAt: '2026-10-13T12:30:00Z', url: 'https://github.com/owner/repo/releases/tag/v1.1.0.0'
    };
    const fixture = publicationFixture(t, packageFileName, { initialRelease: published, confirmedRelease: published, downloadBytes: remoteBytes });
    // Local rebuilt bytes and sidecar can be absent: the public assets are authoritative.
    rmSync(path.join(fixture.directory, filename));
    rmSync(path.join(fixture.directory, filename + '.sha256'));
    assert.deepEqual(publishRelease(fixture.arguments), { timestamp: published.publishedAt, url: published.url });
    assert.deepEqual(commands(fixture), ['view', 'download', 'view']);
    assert.equal(fixture.validations.length, 1);
    assert.deepEqual(fixture.validations[0].bytes, remoteBytes);
    assert.deepEqual(readFileSync(path.join(fixture.directory, filename)), remoteBytes);
});

test('published releases missing either required asset fail without uploads or publication edits', async t => {
    const { publishRelease, packageFileName } = await helpers;
    const filename = packageFileName(releaseInfo());
    for (const assets of [[], [{ name: filename }], [{ name: filename + '.sha256' }]]) {
        const fixture = publicationFixture(t, packageFileName, { initialRelease: { isDraft: false, assets } });
        assert.throws(() => publishRelease(fixture.arguments), /Published release is missing/);
        assert.deepEqual(commands(fixture), ['view']);
        assert.equal(fixture.validations.length, 0);
    }
});

test('upload or download failures stop before publication', async t => {
    const { publishRelease, packageFileName } = await helpers;
    for (const [options, message, expected] of [
        [{ uploadError: true }, /Upload failed/, ['view', 'create', 'upload']],
        [{ downloadError: true }, /Download failed/, ['view', 'create', 'upload', 'download']]
    ]) {
        const fixture = publicationFixture(t, packageFileName, options);
        assert.throws(() => publishRelease(fixture.arguments), message);
        assert.deepEqual(commands(fixture), expected);
    }
});

test('corrupt downloads and valid but different uploaded bytes never make a draft public', async t => {
    const { publishRelease, packageFileName } = await helpers;
    for (const [options, message] of [
        [{ corruptDownload: true }, /SHA-256 checksum/],
        [{ downloadBytes: Buffer.from('different bytes with a valid checksum') }, /differs from the tested package/]
    ]) {
        const fixture = publicationFixture(t, packageFileName, options);
        assert.throws(() => publishRelease(fixture.arguments), message);
        assert.deepEqual(commands(fixture), ['view', 'create', 'upload', 'download']);
    }
});

test('invalid local package metadata and checksum stop before any release mutation', async t => {
    const { publishRelease, packageFileName } = await helpers;
    const invalidMetadata = publicationFixture(t, packageFileName, { validationError: true });
    assert.throws(() => publishRelease(invalidMetadata.arguments), /Invalid package metadata/);
    assert.deepEqual(commands(invalidMetadata), ['view']);
    const invalidChecksum = publicationFixture(t, packageFileName);
    writeFileSync(path.join(invalidChecksum.directory, invalidChecksum.filename), 'tampered bytes');
    assert.throws(() => publishRelease(invalidChecksum.arguments), /SHA-256 checksum/);
    assert.deepEqual(commands(invalidChecksum), ['view']);
    assert.equal(invalidChecksum.validations.length, 0);
});

test('release confirmation requires public state and a publication timestamp', async t => {
    const { publishRelease, packageFileName } = await helpers;
    for (const confirmedRelease of [
        { isDraft: true, publishedAt: '2026-10-13T12:30:00Z' },
        { isDraft: false, isPrerelease: true, publishedAt: '2026-10-13T12:30:00Z' },
        { isDraft: false, isPrerelease: false, publishedAt: null }
    ]) {
        const fixture = publicationFixture(t, packageFileName, { confirmedRelease });
        assert.throws(() => publishRelease(fixture.arguments), /publication is not confirmed/);
        assert.deepEqual(commands(fixture), ['view', 'create', 'upload', 'download', 'edit', 'view']);
    }
});

test('publication rejects a mismatched tag and malformed repository before invoking gh', async t => {
    const { publishRelease, packageFileName } = await helpers;
    const fixture = publicationFixture(t, packageFileName);
    assert.throws(() => publishRelease({ ...fixture.arguments, tag: 'v1.0.0.0' }), /Release tag must be/);
    assert.throws(() => publishRelease({ ...fixture.arguments, repository: 'https://github.com/owner/repo' }), /owner\/name/);
    assert.deepEqual(commands(fixture), []);
});


test('release configuration rejects a GUID or plugin name that differs from the installed plugin identity', async t => {
    const { readReleaseInfo } = await helpers;
    const directory = temporaryDirectory(t);
    const info = releaseInfo();
    for (const [property, value] of [['guid', 'different-plugin-guid'], ['name', 'A renamed plugin']]) {
        writeReleaseConfig(directory, info);
        writeFileSync(path.join(directory, 'release/config.json'), JSON.stringify({ ...info.config, [property]: value }));
        assert.throws(() => readReleaseInfo(directory), /GUID\/name must match Plugin.cs/);
    }
    writeReleaseConfig(directory, info);
    writeFileSync(path.join(directory, 'src/Jellyfin.Plugin.MetadataRules/Plugin.cs'), '// Missing identity declarations.\n');
    assert.throws(() => readReleaseInfo(directory), /GUID\/name must match Plugin.cs/);
});

test('package validation rejects a missing or empty DLL and requires installable files at the ZIP root', async t => {
    const { validatePackage } = await helpers;
    const directory = temporaryDirectory(t);
    const info = releaseInfo();
    const dllName = info.config.assemblyName + '.dll';
    const imageName = path.basename(info.config.imagePath);
    const metadata = JSON.stringify({
        guid: info.config.guid, name: info.config.name, version: info.version, targetAbi: '12.4.0.0',
        autoUpdate: true, assemblies: [dllName], imagePath: path.basename(info.config.imagePath)
    });
    writeFileSync(path.join(directory, 'meta.json'), metadata);
    writeFileSync(path.join(directory, imageName), imageBytes);
    writeFileSync(path.join(directory, dllName), '');
    mkdirSync(path.join(directory, 'nested'));
    writeFileSync(path.join(directory, 'nested', dllName), 'nonempty nested DLL');
    writeFileSync(path.join(directory, 'nested', 'meta.json'), metadata);
    const zipPath = path.join(directory, 'invalid-package.zip');
    for (const entries of [
        ['meta.json'], ['meta.json', dllName], ['meta.json', 'nested/' + dllName],
        [dllName], [dllName, 'nested/meta.json']
    ]) {
        rmSync(zipPath, { force: true });
        execFileSync('zip', ['-q', zipPath, ...entries, imageName], { cwd: directory });
        assert.throws(() => validatePackage(zipPath, info), /one nonempty plugin DLL and one meta.json at its root/);
    }
});

test('package validation rejects a missing, empty, or nested plugin image', async t => {
    const { validatePackage } = await helpers;
    const directory = temporaryDirectory(t);
    const info = releaseInfo();
    const dllName = info.config.assemblyName + '.dll';
    const imageName = path.basename(info.config.imagePath);
    writeFileSync(path.join(directory, 'meta.json'), JSON.stringify({
        guid: info.config.guid, name: info.config.name, version: info.version, targetAbi: '12.4.0.0',
        autoUpdate: true, assemblies: [dllName], imagePath: imageName
    }));
    writeFileSync(path.join(directory, dllName), 'nonempty test DLL bytes');
    writeFileSync(path.join(directory, imageName), Buffer.alloc(0));
    mkdirSync(path.join(directory, 'nested'));
    writeFileSync(path.join(directory, 'nested', imageName), imageBytes);
    const zipPath = path.join(directory, 'invalid-image.zip');
    for (const imageEntries of [[], [imageName], ['nested/' + imageName]]) {
        rmSync(zipPath, { force: true });
        execFileSync('zip', ['-q', zipPath, 'meta.json', dllName, ...imageEntries], { cwd: directory });
        assert.throws(() => validatePackage(zipPath, info), /one nonempty plugin image at its root/);
    }
});

test('existing prereleases are rejected before downloads, validations, or release mutations', async t => {
    const { publishRelease, packageFileName } = await helpers;
    for (const isDraft of [true, false]) {
        const fixture = publicationFixture(t, packageFileName, { initialRelease: { isDraft, isPrerelease: true, assets: [] } });
        assert.throws(() => publishRelease(fixture.arguments), /Prereleases are not published to the stable Jellyfin catalog/);
        assert.deepEqual(commands(fixture), ['view']);
        assert.equal(fixture.validations.length, 0);
    }
});

test('latest stable discovery normalizes release tags and rejects prereleases, drafts, and stale versions', async () => {
    const { normalizeJellyfinVersion, latestStableVersion } = await helpers;
    assert.equal(normalizeJellyfinVersion('v12.4'), '12.4.0');
    assert.equal(normalizeJellyfinVersion('12.4.1'), '12.4.1');
    for (const value of ['v12.4-rc1', '12.4.0.0', 'latest', '12']) {
        assert.throws(() => normalizeJellyfinVersion(value), /stable Jellyfin release/);
    }
    const release = { draft: false, prerelease: false, tag_name: 'v12.4' };
    assert.equal(latestStableVersion(release, '12.4.0'), '12.4.0');
    for (const value of [null, { ...release, draft: true }, { ...release, prerelease: true }]) {
        assert.throws(() => latestStableVersion(value, '12.4.0'), /published stable release/);
    }
    assert.throws(() => latestStableVersion({ ...release, tag_name: 'v12.3' }, '12.4.0'), /below the supported minimum/);
});

test('compatibility checks build the baseline package once and add only a distinct latest stable API target', async () => {
    const { compatibilityPlan } = await helpers;
    for (const latest of ['v12.4', 'v12.5.1', 'v13.0']) {
        const calls = [];
        const plan = await compatibilityPlan(releaseInfo(), { token: 'fixture-token', fetchImpl: async (url, options) => {
            calls.push({ url, options });
            return new Response(JSON.stringify({ draft: false, prerelease: false, tag_name: latest, html_url: 'https://github.com/jellyfin/jellyfin/releases/tag/' + latest }));
        } });
        const targets = plan.matrix.include;
        assert.deepEqual(targets[0], { jellyfin: '12.4.0', role: 'minimum ABI', publishPackage: true });
        assert.equal(targets.length, latest === 'v12.4' ? 1 : 2);
        if (targets.length > 1) assert.deepEqual(targets[1], { jellyfin: latest.slice(1) === '13.0' ? '13.0.0' : latest.slice(1), role: 'latest stable', publishPackage: false });
        assert.equal(calls.length, 1);
        assert.equal(calls[0].url, 'https://api.github.com/repos/jellyfin/jellyfin/releases/latest');
        assert.equal(calls[0].options.headers.Authorization, 'Bearer fixture-token');
        assert.equal(calls[0].options.redirect, 'error');
    }
});

test('failed latest discovery never silently substitutes the minimum release', async () => {
    const { compatibilityPlan, fetchOfficial } = await helpers;
    let calls = 0;
    await assert.rejects(compatibilityPlan(releaseInfo(), { fetchImpl: async () => {
        calls++;
        return new Response('Unavailable', { status: 503 });
    } }), /HTTP 503/);
    assert.equal(calls, 4);
    await assert.rejects(fetchOfficial('https://example.com/releases/latest', { fetchImpl: () => { throw new Error('must not fetch'); } }), /official latest stable/);
});


test('package validation rejects additional files outside the declared installable package', async t => {
    const { validatePackage } = await helpers;
    const directory = temporaryDirectory(t);
    const info = releaseInfo();
    const dllName = info.config.assemblyName + '.dll';
    const imageName = path.basename(info.config.imagePath);
    writeFileSync(path.join(directory, 'meta.json'), JSON.stringify({
        guid: info.config.guid, name: info.config.name, version: info.version, targetAbi: '12.4.0.0',
        autoUpdate: true, assemblies: [dllName], imagePath: imageName
    }));
    writeFileSync(path.join(directory, dllName), 'fixture DLL');
    writeFileSync(path.join(directory, imageName), imageBytes);
    writeFileSync(path.join(directory, 'Unexpected.dll'), 'unexpected bytes');
    const zipPath = path.join(directory, 'extra-files.zip');
    execFileSync('zip', ['-q', zipPath, 'meta.json', dllName, imageName, 'Unexpected.dll'], { cwd: directory });
    assert.throws(() => validatePackage(zipPath, info), /only the plugin DLL, meta.json, and image at its root/);
});
