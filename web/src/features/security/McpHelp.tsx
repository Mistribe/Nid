import { useState } from 'react';
import { useTranslation } from 'react-i18next';

type Agent = 'claude_code' | 'claude_desktop';

/** Spec 4B §6: where the MCP server is and how to plug an assistant into it. */
export function McpHelp() {
  const { t } = useTranslation();
  const [agent, setAgent] = useState<Agent>('claude_code');
  const address = `${window.location.origin}/mcp`;
  const token = t('security.mcp.token_placeholder');
  const command = `claude mcp add --transport http nid ${address} --header "Authorization: Bearer ${token}"`;
  // Claude Desktop only reaches a remote server with a bearer header through mcp-remote; the header sits in env because a space in an arg breaks on Windows.
  const desktopConfig = JSON.stringify({
    mcpServers: {
      nid: {
        command: 'npx',
        args: ['-y', 'mcp-remote', address, '--header', 'Authorization:${NID_AUTH}'],
        env: { NID_AUTH: `Bearer ${token}` },
      },
    },
  }, null, 2);

  return (
    <section className="space-y-3" aria-labelledby="mcp-title">
      <h2 id="mcp-title" className="text-xl font-semibold">{t('security.mcp.title')}</h2>
      <p>{t('security.mcp.intro')}</p>
      <p className="flex flex-wrap gap-2">
        {t('security.mcp.address')}
        <code className="break-all rounded-md border bg-muted px-1 font-mono text-sm">{address}</code>
      </p>
      <label className="flex flex-wrap items-center gap-2">
        {t('security.mcp.agent_label')}
        <select value={agent} onChange={(e) => setAgent(e.target.value as Agent)} className="rounded-md border px-3 py-2">
          <option value="claude_code">{t('security.mcp.agent_claude_code')}</option>
          <option value="claude_desktop">{t('security.mcp.agent_claude_desktop')}</option>
        </select>
      </label>
      {agent === 'claude_code' ? (
        <>
          <p>{t('security.mcp.command_label')}</p>
          <code className="block break-all rounded-md border bg-muted p-2 font-mono text-sm">{command}</code>
        </>
      ) : (
        <>
          <p>{t('security.mcp.desktop_label')}</p>
          <pre className="overflow-x-auto rounded-md border bg-muted p-2 font-mono text-sm"><code>{desktopConfig}</code></pre>
          <p>{t('security.mcp.desktop_restart')}</p>
        </>
      )}
      <p>{t('security.mcp.advice')}</p>
    </section>
  );
}
