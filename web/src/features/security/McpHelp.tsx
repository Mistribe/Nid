import { useId, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { Folded } from '@/components/form/Folded';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { InfoTip } from '@/components/InfoTip';
import { buttonVariants } from '@/components/ui/button';
import { CopyButton } from './CopyButton';
import {
  AGENTS, claudeCodeCommand, claudeCodeProject, claudeDesktopConfig, codexConfig, cursorConfig, exportLine, geminiCommand,
  unreachableReason, vscodeConfig, type Agent,
} from './mcpSnippets';
import '@/locales/security';

const REMEMBERED = 'nid.mcp.agent';
/** Nid's Claude Desktop extension, zipped for this instance (McpBundle.cs). */
const MCP_BUNDLE = '/api/v1/mcp/nid.mcpb';

function rememberedAgent(): Agent {
  try {
    const saved = localStorage.getItem(REMEMBERED);
    return AGENTS.find((agent) => agent === saved) ?? AGENTS[0];
  } catch {
    return AGENTS[0];
  }
}

/** A value to copy: the address, a command, a file. Wraps rather than scrolls sideways. */
function Snippet({ value, label }: { value: string; label: string }) {
  return (
    <div className="flex items-start gap-2">
      <pre className="min-w-0 flex-1 rounded-md border bg-muted p-2 font-mono text-sm break-all whitespace-pre-wrap"><code>{value}</code></pre>
      <CopyButton value={value} label={label} />
    </div>
  );
}

function Steps({ children }: { children: ReactNode }) {
  return <ol className="list-decimal space-y-3 pl-5">{children}</ol>;
}

/**
 * #309, « Brancher un assistant IA »: one assistant at a time, numbered steps and every value ready to copy. With `token` (the
 * secret just created, shown once by `NewTokenDialog`), the snippets carry it; it never leaves the caller's state.
 */
export function McpHelp({ token }: { token?: string }) {
  const { t } = useTranslation();
  const [agent, setAgent] = useState<Agent>(rememberedAgent);
  const address = `${window.location.origin}/mcp`;
  const secret = token ?? t('security.mcp.token_placeholder');
  const choose = (next: string) => {
    setAgent(next as Agent);
    try { localStorage.setItem(REMEMBERED, next); } catch { /* a private window: the choice lasts this visit */ }
  };
  const copy = (what: string) => t('security.mcp.copy', { what });
  const variable = (
    <li className="space-y-2">
      <p>{t('security.mcp.variable')}</p>
      <Snippet value={exportLine(secret)} label={copy(t('security.mcp.what.variable'))} />
    </li>
  );

  const steps: Record<Agent, ReactNode> = {
    claude_desktop: <ClaudeDesktop address={address} secret={secret} token={token} copy={copy} />,
    claude_code: (
      <>
        <Steps>
          <li className="space-y-2">
            <p>{t('security.mcp.claude_code.command')}</p>
            <Snippet value={claudeCodeCommand(address, secret)} label={copy(t('security.mcp.what.command'))} />
          </li>
        </Steps>
        <p className="text-sm text-muted-foreground">{t('security.mcp.claude_code.stored')}</p>
        <Folded label={t('security.mcp.claude_code.project_title')} hasError={false}>
          <p>{t('security.mcp.claude_code.project')}</p>
          <Snippet value={claudeCodeProject(address)} label={copy('.mcp.json')} />
          <p role="note" className="text-sm text-status-warning-text">{t('security.mcp.never_commit')}</p>
        </Folded>
      </>
    ),
    cursor: (
      <Steps>
        {variable}
        <li className="space-y-2"><p>{t('security.mcp.cursor.file')}</p><Snippet value={cursorConfig(address)} label={copy('mcp.json')} /></li>
        <li>{t('security.mcp.restart', { agent: 'Cursor' })}</li>
      </Steps>
    ),
    vscode: (
      <Steps>
        {variable}
        <li className="space-y-2"><p>{t('security.mcp.vscode.file')}</p><Snippet value={vscodeConfig(address)} label={copy('.mcp.json')} /></li>
        <li>{t('security.mcp.restart', { agent: 'VS Code' })}</li>
      </Steps>
    ),
    gemini: (
      <Steps>
        <li className="space-y-2"><p>{t('security.mcp.gemini.command')}</p><Snippet value={geminiCommand(address, secret)} label={copy(t('security.mcp.what.command'))} /></li>
      </Steps>
    ),
    codex: (
      <Steps>
        {variable}
        <li className="space-y-2"><p>{t('security.mcp.codex.file')}</p><Snippet value={codexConfig(address)} label={copy('config.toml')} /></li>
        <li>{t('security.mcp.restart', { agent: 'Codex' })}</li>
      </Steps>
    ),
  };
  const stored: Partial<Record<Agent, string>> = {
    cursor: t('security.mcp.stored_variable'), vscode: t('security.mcp.stored_variable'), codex: t('security.mcp.stored_variable'),
    gemini: t('security.mcp.gemini.stored'),
  };

  return (
    // Folded under its title in the Jetons tab (#308) and in the new-token dialog: the fold's button says it.
    <section className="max-w-prose space-y-3" aria-label={t('security.mcp.title')}>
      <p>
        {t('security.mcp.intro')}{' '}
        <InfoTip label={t('security.mcp.mcp')}>{t('security.mcp.mcp_about')}</InfoTip>
      </p>
      <div className="space-y-1">
        <p className="text-sm text-muted-foreground">{t('security.mcp.address')}</p>
        <Snippet value={address} label={copy(t('security.mcp.what.address'))} />
      </div>
      <div className="max-w-xs">
        <Field label={t('security.mcp.agent_label')}>
          {(c) => (
            <SelectField {...c} value={agent} onValueChange={choose}
              options={AGENTS.map((a) => ({ value: a, label: t(`security.mcp.agent.${a}`) }))} />
          )}
        </Field>
      </div>
      {steps[agent]}
      {stored[agent] ? <p className="text-sm text-muted-foreground">{stored[agent]}</p> : null}
      {token ? null : <p>{t('security.mcp.advice')}</p>}
    </section>
  );
}

function ClaudeDesktop({ address, secret, token, copy }: { address: string; secret: string; token?: string; copy: (what: string) => string }) {
  const { t } = useTranslation();
  const reason = unreachableReason(window.location.origin);
  const [online, setOnline] = useState(reason ? 'no' : 'yes');
  const questionId = useId();
  return (
    <div className="space-y-3">
      <p id={questionId} className="font-medium">{t('security.mcp.desktop.question')}</p>
      <Segmented aria-labelledby={questionId} value={online} onValueChange={setOnline}
        options={[{ value: 'yes', label: t('security.mcp.desktop.yes') }, { value: 'no', label: t('security.mcp.desktop.no') }]} />
      {reason ? <p className="text-sm text-muted-foreground">{t(`security.mcp.desktop.reason_${reason}`)}</p> : null}
      {online === 'yes' ? (
        <>
          <Steps>
            <li>{t('security.mcp.desktop.connector_open')}</li>
            <li className="space-y-2">
              <p>{t('security.mcp.desktop.connector_url')}</p>
              <Snippet value={address} label={copy(t('security.mcp.what.address'))} />
            </li>
            <li className="space-y-2">
              <p>{t('security.mcp.desktop.connector_header')}</p>
              <Snippet value={`Bearer ${secret}`} label={copy(t('security.mcp.what.header'))} />
            </li>
          </Steps>
          <p className="text-sm text-muted-foreground">{t('security.mcp.desktop.connector_cloud')}</p>
          <p role="note" className="text-sm text-status-warning-text">{t('security.mcp.desktop.connector_team')}</p>
          <p className="text-sm text-muted-foreground">{t('security.mcp.desktop.connector_missing')}</p>
        </>
      ) : (
        <>
          <Steps>
            <li className="space-y-2">
              <p>{t('security.mcp.desktop.extension_download')}</p>
              <a href={MCP_BUNDLE} download className={buttonVariants({ variant: 'outline' })}>{t('security.mcp.desktop.extension_link')}</a>
            </li>
            <li>{t('security.mcp.desktop.extension_open')}</li>
            {token ? (
              <li className="space-y-2">
                <p>{t('security.mcp.desktop.extension_token')}</p>
                <Snippet value={token} label={copy(t('security.mcp.what.token'))} />
              </li>
            ) : <li>{t('security.mcp.desktop.extension_token_later')}</li>}
          </Steps>
          <p className="text-sm text-muted-foreground">{t('security.mcp.desktop.extension_stored')}</p>
          <Folded label={t('security.mcp.desktop.manual_title')} hasError={false}>
            <Steps>
              <li>{t('security.mcp.desktop.manual_open')}</li>
              <li className="space-y-2">
                <p>{t('security.mcp.desktop.manual_add')}</p>
                <Snippet value={claudeDesktopConfig(address, secret)} label={copy('claude_desktop_config.json')} />
              </li>
              <li>{t('security.mcp.restart', { agent: 'Claude Desktop' })}</li>
            </Steps>
            <p className="text-sm text-muted-foreground">{t('security.mcp.desktop.manual_stored')}</p>
          </Folded>
        </>
      )}
    </div>
  );
}
