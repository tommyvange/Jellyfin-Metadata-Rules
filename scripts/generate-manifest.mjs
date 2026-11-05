import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, basename } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readReleaseInfo, packageFileName, compareVersions, validateTag, projectRoot } from './release-info.mjs';

export function createCatalog({ info, repository, tag, zipBytes, timestamp, changelog }) {
    validateTag(tag, info);
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Repository must be owner/name.');
    if (!timestamp || !Number.isFinite(Date.parse(timestamp))) throw new Error('A valid release timestamp is required.');
    const { config, version } = info;
    return [{
        guid: config.guid, name: config.name, description: config.description,
        overview: config.overview, owner: config.owner, category: config.category,
        imageUrl: `https://raw.githubusercontent.com/${repository}/${tag}/${config.imagePath}`,
        versions: [{
            version, changelog, targetAbi: `${config.minimumJellyfinVersion}.0`,
            sourceUrl: `https://github.com/${repository}/releases/download/${tag}/${packageFileName(info)}`,
            checksum: createHash('md5').update(zipBytes).digest('hex'),
            timestamp: new Date(timestamp).toISOString()
        }]
    }];
}

export function mergeCatalog(previous, next) {
    if (!Array.isArray(previous) || !Array.isArray(next)) throw new Error('Plugin catalogs must be arrays.');
    const result = structuredClone(previous);
    for (const plugin of next) {
        const existing = result.find(entry => entry.guid === plugin.guid);
        if (existing && existing.name !== plugin.name) throw new Error('Keep the plugin name stable for existing installs.');
        const versions = existing ? structuredClone(existing.versions) : [];
        if (!Array.isArray(versions)) throw new Error('Plugin versions must be an array.');
        for (const version of plugin.versions) {
            const published = versions.find(entry => entry.version === version.version);
            if (published) {
                // Published ZIPs and their ABI must not change under an existing version.
                if (published.sourceUrl !== version.sourceUrl || published.checksum.toLowerCase() !== version.checksum.toLowerCase() ||
                    published.targetAbi !== version.targetAbi) {
                    throw new Error(`Version ${version.version} already exists with different release contents. Bump the plugin version.`);
                }
            } else versions.push(version);
        }
        versions.sort((a, b) => compareVersions(b.version, a.version));
        const merged = { ...plugin, versions };
        if (existing) result[result.indexOf(existing)] = merged;
        else result.push(merged);
    }
    return result;
}

export function validatePackage(zipPath, info) {
    const entries = execFileSync('unzip', ['-Z1', zipPath], { encoding: 'utf8' }).trim().split('\n');
    const dllName = `${info.config.assemblyName}.dll`;
    const imageName = basename(info.config.imagePath);
    if (entries.filter(entry => entry === 'meta.json').length !== 1 ||
        entries.filter(entry => entry === dllName).length !== 1 ||
        !execFileSync('unzip', ['-p', zipPath, dllName], { maxBuffer: 32 * 1024 * 1024 }).length) {
        throw new Error('Release ZIP must contain one nonempty plugin DLL and one meta.json at its root.');
    }
    if (entries.filter(entry => entry === imageName).length !== 1 ||
        !execFileSync('unzip', ['-p', zipPath, imageName], { maxBuffer: 32 * 1024 * 1024 }).length) {
        throw new Error('Release ZIP must contain one nonempty plugin image at its root.');
    }
    const expected = new Set([dllName, 'meta.json', imageName]);
    if (entries.length !== expected.size || entries.some(entry => !expected.has(entry))) {
        throw new Error('Release ZIP must contain only the plugin DLL, meta.json, and image at its root.');
    }
    const meta = JSON.parse(execFileSync('unzip', ['-p', zipPath, 'meta.json'], { encoding: 'utf8' }));
    if (meta.guid !== info.config.guid || meta.name !== info.config.name || meta.version !== info.version ||
        meta.targetAbi !== `${info.config.minimumJellyfinVersion}.0` || meta.autoUpdate !== true || meta.imagePath !== imageName ||
        !Array.isArray(meta.assemblies) || meta.assemblies.length !== 1 || meta.assemblies[0] !== dllName) {
        throw new Error('ZIP metadata does not match the catalog release configuration.');
    }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    const [repository, tag, assetsDirectory, previousPath, outputPath, timestamp] = process.argv.slice(2);
    if (!outputPath || !timestamp) throw new Error('Usage: node scripts/generate-manifest.mjs owner/repo tag assets-directory previous.json output.json release-timestamp');
    const info = readReleaseInfo();
    const zipPath = resolve(assetsDirectory, packageFileName(info));
    validatePackage(zipPath, info);
    const candidate = createCatalog({
        info, repository, tag, zipBytes: readFileSync(zipPath), timestamp,
        changelog: readFileSync(resolve(projectRoot, 'release/notes.md'), 'utf8').trim()
    });
    const catalog = mergeCatalog(JSON.parse(readFileSync(previousPath, 'utf8')), candidate);
    writeFileSync(outputPath, JSON.stringify(catalog, null, 2) + '\n');
    console.log(`Generated ${outputPath} for ${tag}; retained ${catalog[0].versions.length} release versions.`);
}
