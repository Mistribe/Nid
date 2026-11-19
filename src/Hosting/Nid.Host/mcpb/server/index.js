#!/usr/bin/env node
// Nid's Claude Desktop extension (#309): hands Claude Desktop's stdio to Nid's /mcp. The token comes from the environment
// (Claude Desktop keeps it in the system keychain) and reaches mcp-remote's header through it, never on a command line
// another process could read.
const url = process.env.NID_URL;
const token = process.env.NID_TOKEN;
if (!url || !token) {
  process.stderr.write('Nid: the server address and the access token are both required.\n');
  process.exit(1);
}
process.env.NID_AUTH = `Bearer ${token}`;
delete process.env.NID_TOKEN;
const args = [url, '--header', 'Authorization:${NID_AUTH}', '--transport', 'http-only'];
// A Nid on the home network without https: the adult typed that address.
if (url.startsWith('http://')) args.push('--allow-http');
process.argv = [process.argv[0], process.argv[1], ...args];
await import('../node_modules/mcp-remote/dist/proxy.js');
