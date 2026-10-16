import { useTranslation } from 'react-i18next';

/** Spec 4B §6: where the MCP server is and how to plug Claude Code into it. */
export function McpHelp() {
  const { t } = useTranslation();
  const address = `${window.location.origin}/mcp`;
  const command = `claude mcp add --transport http nid ${address} --header "Authorization: Bearer ${t('security.mcp.token_placeholder')}"`;

  return (
    <section className="space-y-3" aria-labelledby="mcp-title">
      <h2 id="mcp-title" className="text-xl font-semibold">{t('security.mcp.title')}</h2>
      <p>{t('security.mcp.intro')}</p>
      <p className="flex flex-wrap gap-2">
        {t('security.mcp.address')}
        <code className="break-all rounded-md border bg-muted px-1 font-mono text-sm">{address}</code>
      </p>
      <p>{t('security.mcp.command_label')}</p>
      <code className="block break-all rounded-md border bg-muted p-2 font-mono text-sm">{command}</code>
      <p>{t('security.mcp.advice')}</p>
    </section>
  );
}
