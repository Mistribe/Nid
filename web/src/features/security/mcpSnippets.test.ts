import { describe, expect, it } from 'vitest';
import { claudeDesktopConfig, codexConfig, cursorConfig, unreachableReason, vscodeConfig } from './mcpSnippets';

const address = 'https://nid.example.org/mcp';

describe('MCP snippets', () => {
  it('pins mcp-remote and keeps the token out of the arguments', () => {
    const config = JSON.parse(claudeDesktopConfig(address, 'secret')) as { mcpServers: { nid: { args: string[]; env: Record<string, string> } } };
    expect(config.mcpServers.nid.args).toEqual(['-y', 'mcp-remote@0.16.5', address, '--header', 'Authorization:${NID_AUTH}']);
    expect(config.mcpServers.nid.env).toEqual({ NID_AUTH: 'Bearer secret' });
  });

  it('reads the token from the environment, in each client’s own syntax', () => {
    expect(cursorConfig(address)).toContain('"Authorization": "Bearer ${env:NID_TOKEN}"');
    expect(vscodeConfig(address)).toContain('"Authorization": "Bearer ${NID_TOKEN}"');
    expect(vscodeConfig(address)).toContain('"type": "http"');
    expect(codexConfig(address)).toBe(`[mcp_servers.nid]\nurl = "${address}"\nbearer_token_env_var = "NID_TOKEN"`);
  });
});

describe('unreachableReason', () => {
  it('says when Anthropic’s cloud surely cannot reach the address', () => {
    expect(unreachableReason('https://nid.example.org')).toBeNull();
    expect(unreachableReason('https://nid.example.org:443')).toBeNull();
    expect(unreachableReason('http://nid.example.org')).toBe('http');
    expect(unreachableReason('https://nid.example.org:8443')).toBe('port');
    expect(unreachableReason('https://192.168.1.20')).toBe('private');
    expect(unreachableReason('https://172.20.0.5')).toBe('private');
    expect(unreachableReason('https://nid.local')).toBe('private');
    expect(unreachableReason('https://nid')).toBe('private');
    expect(unreachableReason('https://nid.localtest.me')).toBe('private');
    expect(unreachableReason('https://172.40.0.5')).toBeNull();
  });
});
