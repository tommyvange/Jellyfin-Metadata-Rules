import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { compareVersions, readReleaseInfo } from './release-info.mjs';

export function normalizeJellyfinVersion(value) {
    const match = /^v?(\d+)\.(\d+)(?:\.(\d+))?$/.exec(value ?? '');
    if (!match) throw new Error(`Expected a stable Jellyfin release version, got ${value}.`);
    return match.slice(1).map(component => Number(component ?? 0)).join('.');
}

/** Bounded official metadata requests; never substitute an older release on failure. */
export async function fetchOfficial(url, { fetchImpl = fetch, maxBytes = 1_000_000, token = process.env.GH_TOKEN } = {}) {
    if (url !== 'https://api.github.com/repos/jellyfin/jellyfin/releases/latest') {
        throw new Error(`Expected the official latest stable Jellyfin metadata URL: ${url}`);
    }
    let failure;
    for (let attempt = 0; attempt < 4; attempt++) {
        try {
            const headers = { 'User-Agent': 'Jellyfin-Metadata-Rules-compatibility', Accept: 'application/vnd.github+json' };
            if (token) headers.Authorization = `Bearer ${token}`;
            const response = await fetchImpl(url, { headers, signal: AbortSignal.timeout(30_000), redirect: 'error' });
            if (!response.ok) throw new Error(`Official source returned HTTP ${response.status}: ${url}`);
            if (Number(response.headers.get('content-length') ?? 0) > maxBytes) throw new Error(`Upstream response is too large: ${url}`);
            const chunks = [];
            let size = 0;
            for await (const chunk of response.body) {
                size += chunk.length;
                if (size > maxBytes) throw new Error(`Upstream response is too large: ${url}`);
                chunks.push(chunk);
            }
            return Buffer.concat(chunks);
        } catch (error) {
            failure = error;
            if (attempt < 3) await new Promise(resolve => setTimeout(resolve, 250 * (attempt + 1)));
        }
    }
    throw failure;
}

export function latestStableVersion(release, minimum) {
    if (!release || release.draft !== false || release.prerelease !== false) {
        throw new Error('Official latest release is not a published stable release.');
    }
    const version = normalizeJellyfinVersion(release.tag_name);
    if (compareVersions(version, minimum) < 0) throw new Error(`Latest stable Jellyfin ${version} is below the supported minimum ${minimum}.`);
    return version;
}

export async function compatibilityPlan(info, { fetchImpl, token } = {}) {
    const release = JSON.parse((await fetchOfficial(info.config.compatibility.latestReleaseUrl, { fetchImpl, token })).toString('utf8'));
    const latest = latestStableVersion(release, info.config.minimumJellyfinVersion);
    const baseline = { jellyfin: info.config.minimumJellyfinVersion, role: 'minimum ABI', publishPackage: true };
    const targets = [baseline];
    if (latest !== baseline.jellyfin) targets.push({ jellyfin: latest, role: 'latest stable', publishPackage: false });
    return { latest, releaseUrl: release.html_url, matrix: { include: targets } };
}

async function main() {
    const plan = await compatibilityPlan(readReleaseInfo());
    if (process.argv.includes('--github-output')) {
        console.log(`latest-jellyfin=${plan.latest}`);
        console.log(`matrix=${JSON.stringify(plan.matrix)}`);
    } else console.log(JSON.stringify(plan, null, 2));
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    main().catch(error => { console.error(`Jellyfin compatibility discovery failed: ${error.message}`); process.exitCode = 1; });
}
