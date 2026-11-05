const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('embedded dashboard JavaScript parses before packaging', () => {
    const file = path.join(__dirname, '../src/Jellyfin.Plugin.MetadataRules/Web/config.html');
    const html = fs.readFileSync(file, 'utf8');
    const scripts = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/gi)];
    assert.ok(scripts.length > 0, 'The dashboard must include its application script.');
    for (const script of scripts) new vm.Script(script[1], { filename: file });
});
