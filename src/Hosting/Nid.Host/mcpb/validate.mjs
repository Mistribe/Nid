// The image build checks the manifest template with the MCPB CLI, as the host fills it in (McpBundle.cs).
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const manifest = JSON.parse(readFileSync(new URL('./manifest.json', import.meta.url), 'utf8'));
Object.assign(manifest, { version: '1.2.3', privacy_policies: ['https://nid.example.org/'] });
manifest.user_config.url.default = 'https://nid.example.org/mcp';
const dir = mkdtempSync(join(tmpdir(), 'nid-mcpb-'));
writeFileSync(join(dir, 'manifest.json'), JSON.stringify(manifest));
execFileSync(new URL('./node_modules/.bin/mcpb', import.meta.url).pathname, ['validate', join(dir, 'manifest.json')], { stdio: 'inherit' });
