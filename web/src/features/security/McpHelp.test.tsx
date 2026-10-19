import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { pick } from '@/test/controls';
import { server } from '@/test/server';

describe('Brancher un assistant', () => {
  it('gives the MCP address, the Claude Code command and the read-only advice', async () => {
    server.use(http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })));
    const { container } = await renderApp('/security');

    const section = await screen.findByRole('region', { name: 'Brancher un assistant' });
    const origin = window.location.origin;
    expect(section).toHaveTextContent(`${origin}/mcp`);
    expect(section).toHaveTextContent(`claude mcp add --transport http nid ${origin}/mcp --header "Authorization: Bearer <votre jeton>"`);
    expect(section).toHaveTextContent('Lecture seule');
    await expectNoAxeViolations(container);
  });

  it('gives the Claude Desktop config once picked', async () => {
    server.use(http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({ entries: [], next: null })));
    const { user } = await renderApp('/security');

    const section = await screen.findByRole('region', { name: 'Brancher un assistant' });
    await pick(user, 'Mon assistant', 'Claude Desktop');
    expect(section).toHaveTextContent('claude_desktop_config.json');
    expect(section).toHaveTextContent('"args": [ "-y", "mcp-remote", "' + window.location.origin + '/mcp"');
    expect(section).toHaveTextContent('Bearer <votre jeton>');
    expect(section).not.toHaveTextContent('claude mcp add');
  });

  it('names the assistant’s reads in my events', async () => {
    server.use(http.get('*/api/v1/platform/audit/me/security', () => HttpResponse.json({
      entries: [{ seq: 1, at: '2026-10-16T08:00:00+00:00', event: 'mcp_tool_called', actor: { kind: 'person', personId: 'a', via: 'mcp', tokenId: 't' }, subjectPersonId: 'a', details: { tool: 'list_persons' } }],
      next: null,
    })));
    await renderApp('/security');

    const row = await screen.findByRole('row', { name: /Lecture par l’assistant/ });
    expect(row).toHaveTextContent('Assistant (MCP)');
  });
});
