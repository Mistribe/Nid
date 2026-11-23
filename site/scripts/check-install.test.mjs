import { test } from 'node:test';
import assert from 'node:assert/strict';
import { staleBlocks } from './check-install.mjs';

test('a block found verbatim in the README is fine', () => {
  const readme = 'x\n```bash\ndeploy/setup.sh --nid-host nid.example.org\n```\n';
  assert.deepEqual(staleBlocks([{ path: 'installer/installer.md', text: '```sh\ndeploy/setup.sh --nid-host nid.example.org\n```' }], readme), []);
});

test('indentation and surrounding blank lines do not count', () => {
  const readme = '1. step\n   ```bash\n   docker compose up -d\n   docker compose logs nid\n   ```\n';
  assert.deepEqual(staleBlocks([{ path: 'a.mdx', text: '```bash\n\ndocker compose up -d\ndocker compose logs nid\n\n```' }], readme), []);
});

test('a block the README no longer holds is reported with its page', () => {
  assert.deepEqual(staleBlocks([{ path: 'installer/proxy.md', text: '```bash\ncaddy reload\n```' }], 'nothing'), ['installer/proxy.md: block 1 not in deploy/README.md']);
});

test('lines that are each in the README but not together are reported', () => {
  const readme = '```bash\ngit pull\n```\n\n```bash\ndocker compose up -d\n```';
  assert.equal(staleBlocks([{ path: 'a.md', text: '```bash\ngit pull\ndocker compose up -d\n```' }], readme).length, 1);
});

test('non-shell blocks are ignored', () => {
  assert.deepEqual(staleBlocks([{ path: 'a.md', text: '```yaml\nk: v\n```' }], ''), []);
});

test('a block whose line the README has since extended is reported', () => {
  const readme = '```bash\ndocker compose -f deploy/compose.yaml up -d --build --wait\n```';
  assert.equal(staleBlocks([{ path: 'a.md', text: '```bash\ndocker compose -f deploy/compose.yaml up -d --build\n```' }], readme).length, 1);
  assert.equal(staleBlocks([{ path: 'a.md', text: '```bash\nup -d --build --wait\n```' }], readme).length, 1);
});

test('untagged blocks, like a Caddyfile, are compared too', () => {
  assert.deepEqual(staleBlocks([{ path: 'proxy.md', text: '```\nnid.example.org {\n\treverse_proxy nid:9090\n}\n```' }], '```\nnid.example.org {\n\treverse_proxy nid:8080\n}\n```'), ['proxy.md: block 1 not in deploy/README.md']);
});
