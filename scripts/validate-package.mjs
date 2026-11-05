import { resolve } from 'node:path';
import { readReleaseInfo, packageFileName, projectRoot } from './release-info.mjs';
import { validatePackage } from './generate-manifest.mjs';
import { verifyChecksum } from './publish-release.mjs';

const info = readReleaseInfo();
const directory = resolve(process.argv[2] || resolve(projectRoot, 'artifacts'));
const filename = packageFileName(info);
verifyChecksum(directory, filename);
validatePackage(resolve(directory, filename), info);
console.log(`Validated ${filename}: plugin DLL, install metadata, image, and SHA-256.`);
