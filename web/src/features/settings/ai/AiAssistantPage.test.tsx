import { screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { parseBlock } from './assistantStream';

const sse = (...events: [string, unknown][]) => new HttpResponse(
  events.map(([type, data]) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`).join(''),
  { headers: { 'content-type': 'text/event-stream' } },
);

describe('Réglages › IA › Assistant', () => {
  it("streams the answer as plain text, with Nid's links, what was kept out, and who answered", async () => {
    const bodies: unknown[] = [];
    server.use(http.post('*/api/v1/ai/assistant/turn', async ({ request }) => {
      bodies.push(await request.json());
      return sse(
        ['tool', { tool: 'list_envelopes' }],
        ['text', { text: 'Votre PEA vaut 21 000 €, voir ' }],
        ['text', { text: 'https://evil.example/pea.' }],
        ['held_back', { count: 2, destination: 'anthropic', placement: 'cloud' }],
        ['done', { provider: 'anthropic', placement: 'cloud', model: 'claude-opus-5-5', reason: 'end', links: [{ id: 'e1', module: 'investments', name: 'PEA' }] }],
      );
    }));
    const { user, container } = await renderApp('/settings/ai/assistant');

    await user.type(await screen.findByRole('textbox', { name: 'Votre question' }), 'Mon PEA ?');
    await user.click(screen.getByRole('button', { name: 'Demander' }));

    expect(await screen.findByText('Votre PEA vaut 21 000 €, voir https://evil.example/pea.')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /evil/ })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'PEA' })).toHaveAttribute('href', '/investments/e1');
    expect(screen.getByText('Consulté : list_envelopes')).toBeInTheDocument();
    expect(screen.getByText(/2 résultats gardés hors de Anthropic par les règles du foyer\./)).toBeInTheDocument();
    expect(screen.getByText('Répondu par Anthropic, modèle claude-opus-5-5.')).toBeInTheDocument();
    expect(bodies).toEqual([{ question: 'Mon PEA ?', history: [] }]);
    await expectNoAxeViolations(container);
  });

  it('says why a turn stopped or was refused, in words', async () => {
    server.use(http.post('*/api/v1/ai/assistant/turn', () => sse(['error', { code: 'ai.assistant_turn_limit' }])));
    const { user } = await renderApp('/settings/ai/assistant');

    await user.type(await screen.findByRole('textbox', { name: 'Votre question' }), 'Encore ?');
    await user.click(screen.getByRole('button', { name: 'Demander' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Vous avez posé beaucoup de questions cette heure-ci');
  });

  it('sends the earlier answered turns as history', async () => {
    const bodies: unknown[] = [];
    server.use(http.post('*/api/v1/ai/assistant/turn', async ({ request }) => {
      bodies.push(await request.json());
      return sse(['text', { text: 'Oui.' }], ['done', { provider: 'salon', placement: 'local', model: 'llama3.2', reason: 'max_steps', links: [] }]);
    }));
    const { user } = await renderApp('/settings/ai/assistant');
    const field = await screen.findByRole('textbox', { name: 'Votre question' });

    await user.type(field, 'Un');
    await user.click(screen.getByRole('button', { name: 'Demander' }));
    expect(await screen.findByText("L'assistant s'est arrêté après trop d'étapes : posez une question plus précise.")).toBeInTheDocument();
    await user.type(field, 'Deux');
    await user.click(screen.getByRole('button', { name: 'Demander' }));

    await screen.findAllByText('Oui.');
    expect(bodies[1]).toEqual({ question: 'Deux', history: [{ role: 'user', content: 'Un' }, { role: 'assistant', content: 'Oui.' }] });
  });

  it('skips what is not one of its events', () => {
    expect(parseBlock(': keep-alive')).toBeNull();
    expect(parseBlock('event: other\ndata: {}')).toBeNull();
    expect(parseBlock('event: tool\ndata: {"tool":"get_dashboard"}')).toEqual({ type: 'tool', tool: 'get_dashboard' });
  });
});
