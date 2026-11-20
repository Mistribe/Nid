import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { beforeEach, describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';
import { MCP_REMOTE } from './mcpSnippets';

async function openHelp() {
  const rendered = await renderApp('/security?tab=tokens');
  await rendered.user.click(await screen.findByRole('button', { name: 'Brancher un assistant IA' }));
  return { ...rendered, section: await screen.findByRole('region', { name: 'Brancher un assistant IA' }) };
}

describe('Brancher un assistant IA', () => {
  beforeEach(() => localStorage.removeItem('nid.mcp.agent'));

  it('gives the address with a copy button, the token advice of today’s presets, and starts with Claude Desktop', async () => {
    const { container, section } = await openHelp();
    const origin = window.location.origin;

    expect(within(section).getAllByText(`${origin}/mcp`)[0]).toBeInTheDocument();
    expect(within(section).getAllByRole('button', { name: 'Copier l’adresse' }).length).toBeGreaterThan(0);
    expect(section).toHaveTextContent('« Lecture + propositions » est recommandé');
    expect(section).not.toHaveTextContent('ne peut encore que consulter');
    // jsdom's origin is http://localhost: the connector cannot reach it, so the extension comes first, and why.
    expect(section).toHaveTextContent('n’est pas en https');
    expect(within(section).getByRole('link', { name: 'Télécharger l’extension (nid.mcpb)' })).toHaveAttribute('href', '/api/v1/mcp/nid.mcpb');
    expect(section).not.toHaveTextContent('mcp-remote@');
    await expectNoAxeViolations(container);
  });

  it('keeps the manual Claude Desktop configuration folded, its bridge pinned', async () => {
    const { user, section } = await openHelp();

    await user.click(within(section).getByRole('button', { name: 'Configuration manuelle' }));

    expect(section).toHaveTextContent(`"${MCP_REMOTE}"`);
    expect(section).toHaveTextContent('"NID_AUTH": "Bearer <votre jeton>"');
  });

  it('walks Claude Desktop through a custom connector when Nid is on the internet', async () => {
    const { user, section } = await openHelp();

    await user.click(within(section).getByRole('radio', { name: 'Oui' }));

    expect(section).toHaveTextContent('Connecteur personnalisé');
    expect(section).toHaveTextContent('« Sans connexion »');
    expect(section).toHaveTextContent('en-tête de requête nommé authorization');
    expect(within(section).getByText('Bearer <votre jeton>')).toBeInTheDocument();
    expect(section).toHaveTextContent('jamais comme connecteur de l’organisation');
  });

  it.each([
    ['Claude Code', `claude mcp add --transport http nid ${window.location.origin}/mcp --header "Authorization: Bearer <votre jeton>"`],
    ['Cursor', '"Authorization": "Bearer ${env:NID_TOKEN}"'],
    ['VS Code (Copilot)', '"Authorization": "Bearer ${NID_TOKEN}"'],
    ['Gemini CLI', `gemini mcp add --transport http --header "Authorization: Bearer <votre jeton>" nid ${window.location.origin}/mcp`],
    ['Codex CLI', 'bearer_token_env_var = "NID_TOKEN"'],
  ])('gives %s its own snippet', async (agent, snippet) => {
    const { user, section } = await openHelp();

    await pick(user, 'Mon assistant', agent);

    expect(section).toHaveTextContent(snippet);
    expect(section).not.toHaveTextContent('claude_desktop_config.json');
    expect(localStorage.getItem('nid.mcp.agent')).not.toBeNull();
  });

  it('names the assistant’s reads in my events', async () => {
    server.use(http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({
      entries: [{ seq: 1, at: '2026-10-16T08:00:00+00:00', event: 'mcp_tool_called', actor: { kind: 'person', personId: 'a', via: 'mcp', tokenId: 't' }, subjectPersonId: 'a', details: { tool: 'list_persons' } }],
      next: null,
    })));
    await renderApp('/security?tab=activity&show=assistant');

    const row = await screen.findByRole('row', { name: /Lecture par l’assistant/ });
    expect(row).toHaveTextContent('Outil list_persons · Assistant (MCP)');
  });
});
