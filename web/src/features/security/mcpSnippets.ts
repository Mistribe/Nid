/**
 * #309: what each MCP client needs to reach `{origin}/mcp` with a bearer token. Key names checked against each vendor's
 * documentation on 2026-11-19 (tokens-and-credentials.md §2). `token` is the real secret right after its creation, a
 * placeholder otherwise.
 */
export const AGENTS = ['claude_desktop', 'claude_code', 'cursor', 'vscode', 'gemini', 'codex'] as const;
export type Agent = (typeof AGENTS)[number];

/** Pinned: `npx -y mcp-remote` alone would run whatever version is latest at each launch, with the token in hand. */
export const MCP_REMOTE = 'mcp-remote@0.16.7';

/** Where the token is read from, for the clients that can take it from the environment. */
export const TOKEN_VARIABLE = 'NID_TOKEN';

const json = (value: unknown) => JSON.stringify(value, null, 2);

export const exportLine = (token: string) => `export ${TOKEN_VARIABLE}="${token}"`;

export const claudeCodeCommand = (address: string, token: string) =>
  `claude mcp add --transport http nid ${address} --header "Authorization: Bearer ${token}"`;

/** Claude Code's project file: the token stays in the environment, never in a file a repository could hold. */
export const claudeCodeProject = (address: string) =>
  json({ mcpServers: { nid: { type: 'http', url: address, headers: { Authorization: `Bearer \${${TOKEN_VARIABLE}}` } } } });

/** Claude Desktop without a connector: the bridge runs on the computer; the header sits in env because a space in an arg breaks on Windows. */
export const claudeDesktopConfig = (address: string, token: string) => json({
  mcpServers: {
    nid: {
      command: 'npx',
      args: ['-y', MCP_REMOTE, address, '--header', 'Authorization:${NID_AUTH}'],
      env: { NID_AUTH: `Bearer ${token}` },
    },
  },
});

export const cursorConfig = (address: string) =>
  json({ mcpServers: { nid: { url: address, headers: { Authorization: `Bearer \${env:${TOKEN_VARIABLE}}` } } } });

/** VS Code / GitHub Copilot's portable format (`.mcp.json`, `~/.copilot/mcp-config.json`). */
export const vscodeConfig = (address: string) =>
  json({ mcpServers: { nid: { type: 'http', url: address, headers: { Authorization: `Bearer \${${TOKEN_VARIABLE}}` } } } });

/** Gemini CLI's documented form takes the token as it is: its settings file then holds it in clear. */
export const geminiCommand = (address: string, token: string) =>
  `gemini mcp add --transport http --header "Authorization: Bearer ${token}" nid ${address}`;

export const codexConfig = (address: string) => `[mcp_servers.nid]\nurl = "${address}"\nbearer_token_env_var = "${TOKEN_VARIABLE}"`;

/**
 * A custom connector of Claude Desktop is called from Anthropic's cloud: only an https address on the standard port, outside
 * a private network, can answer it. Nid cannot test that from the browser; it can only tell when it is surely not so.
 */
export function unreachableReason(origin: string): 'http' | 'port' | 'private' | null {
  const url = new URL(origin);
  if (url.protocol !== 'https:') return 'http';
  if (url.port !== '' && url.port !== '443') return 'port';
  const host = url.hostname;
  const privateHost = host === 'localhost' || host.endsWith('.localhost') || host.endsWith('.local') || host.endsWith('.lan')
    || host.endsWith('.home.arpa') || host.endsWith('.internal') || host.endsWith('.localtest.me')
    || /^(10|127)\./.test(host) || /^192\.168\./.test(host) || /^172\.(1[6-9]|2\d|3[01])\./.test(host) || /^169\.254\./.test(host)
    || /^\[?(::1|f[cd][0-9a-f]{2}:|fe80:)/i.test(host) || !host.includes('.');
  return privateHost ? 'private' : null;
}
