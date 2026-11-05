import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readReleaseInfo, packageFileName, validateTag, projectRoot } from './release-info.mjs';
import { validatePackage } from './generate-manifest.mjs';

function runGh(args, allowMissing = false) {
    const result = spawnSync('gh', args, { encoding: 'utf8' });
    if (result.error) throw result.error;
    if (result.status !== 0) {
        if (allowMissing && /release not found|HTTP 404/i.test(result.stderr)) return null;
        throw new Error(result.stderr.trim() || `gh exited with ${result.status}`);
    }
    return result.stdout;
}

export function verifyChecksum(directory, filename) {
    const zip = readFileSync(resolve(directory, filename));
    const hash = createHash('sha256').update(zip).digest('hex');
    const expected = readFileSync(resolve(directory, filename + '.sha256'), 'utf8').trim();
    if (expected !== `${hash}  ${filename}`) throw new Error('Release ZIP does not match its SHA-256 checksum.');
    return hash;
}

export function publishRelease({ repository, tag, directory, info, notesPath, gh = runGh, validate = validatePackage }) {
    validateTag(tag, info);
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository)) throw new Error('Repository must be owner/name.');
    const filename = packageFileName(info);
    const files = [filename, filename + '.sha256'];
    const inspect = ['release', 'view', tag, '--repo', repository, '--json', 'isDraft,isPrerelease,assets,publishedAt,url'];
    const snapshot = gh(inspect, true);
    const release = snapshot ? JSON.parse(snapshot) : null;
    if (release?.isPrerelease) throw new Error('Prereleases are not published to the stable Jellyfin catalog.');

    if (release && !release.isDraft) {
        // A rerun must use the existing published bytes, not a rebuilt ZIP whose
        // timestamps differ. Published release assets are never overwritten.
        for (const name of files) {
            if (!release.assets.some(asset => asset.name === name)) throw new Error(`Published release is missing ${name}; do not overwrite it. Publish a new version.`);
        }
        gh(['release', 'download', tag, '--repo', repository, '--dir', directory, '--clobber',
            '--pattern', filename, '--pattern', filename + '.sha256']);
        verifyChecksum(directory, filename);
        validate(resolve(directory, filename), info);
    } else {
        const expectedHash = verifyChecksum(directory, filename);
        validate(resolve(directory, filename), info);
        if (!release) {
            gh(['release', 'create', tag, '--repo', repository, '--verify-tag', '--draft',
                '--title', `${info.config.name} ${info.version}`, '--notes-file', notesPath]);
        }
        gh(['release', 'upload', tag, '--repo', repository, '--clobber',
            ...files.map(name => resolve(directory, name))]);
        // Confirm the uploaded bytes before making the draft public.
        gh(['release', 'download', tag, '--repo', repository, '--dir', directory, '--clobber',
            '--pattern', filename, '--pattern', filename + '.sha256']);
        if (verifyChecksum(directory, filename) !== expectedHash) throw new Error('Uploaded release ZIP differs from the tested package.');
        gh(['release', 'edit', tag, '--repo', repository, '--draft=false']);
    }
    const published = JSON.parse(gh(inspect));
    if (published.isDraft || published.isPrerelease || !published.publishedAt) throw new Error('Stable release publication is not confirmed.');
    return { timestamp: published.publishedAt, url: published.url };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    const [repository, tag, directory] = process.argv.slice(2);
    if (!directory) throw new Error('Usage: node scripts/publish-release.mjs owner/repo tag assets-directory');
    const published = publishRelease({ repository, tag, directory: resolve(directory), info: readReleaseInfo(), notesPath: resolve(projectRoot, 'release/notes.md') });
    if (process.env.GITHUB_OUTPUT) writeFileSync(process.env.GITHUB_OUTPUT, `timestamp=${published.timestamp}\nurl=${published.url}\n`, { flag: 'a' });
    console.log(`Release available: ${published.url}`);
}
