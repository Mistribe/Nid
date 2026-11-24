import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const sse = (...events: [string, unknown][]) => new HttpResponse(
  events.map(([type, data]) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`).join(''),
  { headers: { 'content-type': 'text/event-stream' } },
);

const USES = (model: string | null) => [{ use: 'chat', mode: 'self_hosted', model }];

function serve({ enabled = true, model = 'llama3.2' }: { enabled?: boolean; model?: string | null } = {}) {
  server.use(
    http.get('*/api/v1/ai/assistant/me', () => HttpResponse.json({ enabled, offsiteDefault: 'ask', cloudPossible: false })),
    http.get('*/api/v1/ai/settings', () => HttpResponse.json({ remoteWorkersAllowed: false, uses: USES(model), conversationRetentionDays: 30 })),
  );
}

async function openPanel(user: Awaited<ReturnType<typeof renderApp>>['user']) {
  await user.click(await screen.findByRole('button', { name: 'Assistant' }));
  return screen.findByRole('dialog', { name: 'Assistant' });
}

describe('The assistant panel', () => {
  it("asks from any page and streams the answer, with Nid's links, the tools, what was kept out and who answered", async () => {
    serve();
    const bodies: unknown[] = [];
    server.use(http.post('*/api/v1/ai/assistant/turn', async ({ request }) => {
      bodies.push(await request.json());
      return sse(
        ['tool', { tool: 'list_envelopes' }],
        ['text', { text: 'Votre PEA vaut 21 000 €, voir https://evil.example/pea.' }],
        ['held_back', { count: 1, destination: 'salon', placement: 'local' }],
        ['done', { provider: 'salon', placement: 'local', model: 'llama3.2', reason: 'end', links: [{ id: 'e1', module: 'investments', name: 'PEA' }], conversationId: 'c1' }],
      );
    }));
    const { user, container } = await renderApp('/dashboard');

    const panel = await openPanel(user);
    await user.type(within(panel).getByRole('textbox', { name: 'Votre question' }), 'Mon PEA ?');
    await user.click(within(panel).getByRole('button', { name: 'Demander' }));
    await user.type(within(panel).getByRole('textbox', { name: 'Votre question' }), 'Et ensuite ?');
    await within(panel).findByText('Votre PEA vaut 21 000 €, voir https://evil.example/pea.');
    await user.click(within(panel).getByRole('button', { name: 'Demander' }));

    expect(within(panel).getAllByRole('link', { name: 'PEA' })[0]).toHaveAttribute('href', '/investments/e1');
    expect(within(panel).queryByRole('link', { name: /evil/ })).not.toBeInTheDocument();
    expect(within(panel).getAllByText('Consulté : list_envelopes')[0]).toBeInTheDocument();
    expect(within(panel).getAllByText('Répondu par salon, modèle llama3.2.')[0]).toBeInTheDocument();
    await waitFor(() => expect(bodies).toEqual([{ question: 'Mon PEA ?', conversationId: null }, { question: 'Et ensuite ?', conversationId: 'c1' }]));
    await expectNoAxeViolations(container);
  });

  it("shows the conversation's tokens and cost, a worker's without cost, and when paid calls come back after a budget block", async () => {
    serve();
    let turn = 0;
    server.use(
      http.post('*/api/v1/ai/assistant/turn', () => (++turn === 1
        ? sse(['text', { text: 'Bonjour.' }], ['done', { provider: 'anthropic', placement: 'cloud', model: 'claude-opus-5-5', reason: 'end', links: [], conversationId: 'c1' }])
        : sse(['error', { code: 'ai.budget_blocked', availableOn: '2026-12-01' }]))),
      http.get('*/api/v1/ai/assistant/conversations/c1', () => HttpResponse.json({
        id: 'c1', title: 'Un ?', createdAt: '2026-11-20T08:00:00Z', messages: [], tokensIn: 1200, tokensOut: 300, costEur: '0.0108',
      })),
    );
    const { user } = await renderApp('/dashboard');
    const panel = await openPanel(user);

    await user.type(within(panel).getByRole('textbox', { name: 'Votre question' }), 'Un ?');
    await user.click(within(panel).getByRole('button', { name: 'Demander' }));
    expect(await within(panel).findByText(/1\s500 jetons · environ 0,0108\s€/)).toBeInTheDocument();
    await user.type(within(panel).getByRole('textbox', { name: 'Votre question' }), 'Deux ?');
    await user.click(within(panel).getByRole('button', { name: 'Demander' }));

    expect(await within(panel).findByRole('alert')).toHaveTextContent('Les appels payants reprennent le 1 décembre 2026.');
  });

  it('opens from « Plus » on a phone, where the top bar has no room for its button', async () => {
    serve();
    const { user } = await renderApp('/more');

    // The list's entry (the top bar's button is the other one, hidden on a phone by CSS).
    await user.click(within(await screen.findByRole('list', { name: 'Autres sections' })).getByRole('button', { name: 'Assistant' }));

    expect(await screen.findByRole('dialog', { name: 'Assistant' })).toBeInTheDocument();
  });

  it('says why it cannot answer and who can fix it', async () => {
    serve({ enabled: false });
    const off = await renderApp('/dashboard');
    const panel = await openPanel(off.user);
    expect(await within(panel).findByRole('status')).toHaveTextContent("L'assistant n'est pas activé pour vous.");
    expect(within(panel).getByRole('link', { name: "Ouvrir les réglages de l'IA" })).toHaveAttribute('href', '/settings/ai');
    expect(within(panel).getByRole('button', { name: 'Demander' })).toBeDisabled();
    off.unmount();

    serve({ model: null });
    const member = await renderApp('/dashboard');
    expect(await within(await openPanel(member.user)).findByRole('status')).toHaveTextContent('demandez à un administrateur de Nid');
    member.unmount();

    serve({ model: null });
    const admin = await renderApp('/dashboard', { capabilities: adminCapabilities });
    const adminPanel = await openPanel(admin.user);
    expect(await within(adminPanel).findByRole('status')).toHaveTextContent("Aucun modèle n'est choisi pour l'assistant.");
    expect(within(adminPanel).getByRole('link', { name: "Ouvrir les réglages de l'IA" })).toHaveAttribute('href', '/admin/ai');
  });

  it('lists the earlier conversations, reopens one and deletes another', async () => {
    serve();
    const deleted: string[] = [];
    server.use(
      http.get('*/api/v1/ai/assistant/conversations', () => HttpResponse.json({
        items: [
          { id: 'c1', title: 'Mon PEA ?', provider: 'salon', model: 'llama3.2', createdAt: '2026-11-20T08:00:00Z', updatedAt: '2026-11-20T08:00:00Z' },
          { id: 'c2', title: 'Mes crédits ?', provider: 'salon', model: 'llama3.2', createdAt: '2026-11-19T08:00:00Z', updatedAt: '2026-11-19T08:00:00Z', tokensIn: 400, tokensOut: 100, costEur: null },
        ],
        total: 2, page: 1,
      })),
      http.get('*/api/v1/ai/assistant/conversations/c1', () => HttpResponse.json({
        id: 'c1', title: 'Mon PEA ?', provider: 'salon', model: 'llama3.2', createdAt: '2026-11-20T08:00:00Z', policyClass: 'local',
        messages: [
          { role: 'user', content: 'Mon PEA ?', tools: [], items: [], createdAt: '2026-11-20T08:00:00Z' },
          { role: 'assistant', content: 'Il vaut 21 000 €.', tools: ['list_envelopes'], items: [], createdAt: '2026-11-20T08:00:00Z' },
          { role: 'user', content: 'Et mon bail ?', tools: [], items: [], createdAt: '2026-11-20T08:01:00Z' },
          { role: 'assistant', content: '', tools: [], items: [], createdAt: '2026-11-20T08:01:00Z', removed: true },
        ],
      })),
      http.delete('*/api/v1/ai/assistant/conversations/:id', ({ params }) => { deleted.push(String(params.id)); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user } = await renderApp('/dashboard');
    const panel = await openPanel(user);

    await user.click(within(panel).getByRole('button', { name: 'Conversations' }));
    const list = await within(panel).findByRole('list', { name: 'Conversations' });
    const second = within(list).getAllByRole('listitem')[1]!;
    expect(second).toHaveTextContent('500 jetons, sans coût'); // a worker's model: no cost
    await user.click(within(second).getByRole('button', { name: 'Supprimer' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(deleted).toEqual(['c2']));

    await user.click(within(list).getByRole('button', { name: /Mon PEA \?/ }));
    expect(await within(panel).findByText('Il vaut 21 000 €.')).toBeInTheDocument();
    expect(within(panel).getByText('Consulté : list_envelopes')).toBeInTheDocument();
    // #617: an answer taken out with the document it had read.
    expect(within(panel).getByText('Et mon bail ?')).toBeInTheDocument();
    expect(within(panel).getByText("Réponse retirée : un document qu'elle avait lu a été supprimé.")).toBeInTheDocument();
  });
});
