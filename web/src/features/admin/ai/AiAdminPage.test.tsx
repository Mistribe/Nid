import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { adminCapabilities } from '@/test/fixtures';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';

// As the server and the worker's console give it: groups of four.
const FINGERPRINT = Array.from({ length: 16 }, (_, i) => `a${i.toString(16)}12`).join(' ');
const ROTATED = 'ef56 '.repeat(16).trim();

const worker = (over: Record<string, unknown> = {}) => ({
  id: 'w1', name: 'Serveur du salon', url: 'https://worker:8443', placement: 'local', proxyDeclared: false, safe: false, status: 'active',
  keyFingerprint: FINGERPRINT, nidKeyFingerprint: 'cd34'.repeat(16), nidKeyRotationOverdue: false, softwareVersion: '1.0.0',
  lastSeenAt: '2026-11-22T08:00:00+00:00', createdAt: '2026-11-20T08:00:00+00:00', pairedAt: '2026-11-20T08:05:00+00:00', load: 2, ...over,
});

const USES = ['chat', 'extract', 'embed', 'decide'].map((use) => ({ use, mode: 'self_hosted', model: null as string | null }));

const PROFILE = {
  id: 'anthropic-claude', provider: 'anthropic', tier: 'paid', host: 'api.anthropic.com', model: 'claude-opus-5-5', trainsOnData: false,
  humanReview: true, retentionDays: 30, termsUrl: 'https://www.anthropic.com/legal/commercial-terms', reviewedOn: '2026-11-22', reviewDue: false,
  eligible: true, enabled: false, keyHint: null as string | null, suspended: false, capabilities: ['chat'],
};

const BUDGET = { monthlyBudgetEur: '20.00', month: '2026-11-01', spentEur: '16.50', reservedEur: '0.40', remainingEur: '3.10', status: 'alert' };

function serve(workers: unknown[], allowed = false, uses = USES, profile: typeof PROFILE | (typeof PROFILE)[] = PROFILE, allowedKinds: string[] = []) {
  server.use(
    http.get('*/api/v1/ai/budget', () => HttpResponse.json(BUDGET)),
    http.get('*/api/v1/ai/cloud', () => HttpResponse.json({ region: 'eea_ch_uk', profiles: [profile].flat() })),
    http.get('*/api/v1/ai/workers', () => HttpResponse.json({ workers })),
    http.get('*/api/v1/ai/settings', () => HttpResponse.json({ remoteWorkersAllowed: allowed, uses, allowedKinds, conversationRetentionDays: 30 })),
    http.get('*/api/v1/ai/models', () => HttpResponse.json({ models: [{ name: 'llama3.2', capability: 'chat', workers: 1 }] })),
    http.get('*/api/v1/ai/assistant/me', () => HttpResponse.json({ enabled: false, offsiteDefault: 'ask', cloudPossible: false })),
  );
}

describe('Administration › IA', () => {
  it('lists the workers: where they run, whether they are safe, their state and load', async () => {
    serve([worker(), worker({ id: 'w2', name: 'Box', placement: 'remote', proxyDeclared: true, safe: true, status: 'pending', lastSeenAt: null, load: 0, softwareVersion: null })]);
    const { container } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const salon = await screen.findByRole('row', { name: /Serveur du salon/ });
    expect(salon).toHaveTextContent('Local');
    expect(salon).toHaveTextContent('Actif');
    expect(salon).toHaveTextContent('1.0.0');
    const box = screen.getByRole('row', { name: /Box/ });
    expect(box).toHaveTextContent('Distant, derrière un proxy');
    expect(box).toHaveTextContent('Oui');
    expect(box).toHaveTextContent('À appairer');
    expect(box).toHaveTextContent('jamais vu');
    expect(screen.getByRole('link', { name: 'IA' })).toHaveAttribute('aria-current', 'page');
    await expectNoAxeViolations(container);
  });

  it('says how to start a worker when there is none', async () => {
    serve([]);
    await renderApp('/admin/ai', { capabilities: adminCapabilities });

    expect(await screen.findByRole('heading', { name: 'Aucun worker' })).toBeInTheDocument();
    expect(screen.getByText(/docker compose --profile worker up -d worker/)).toBeInTheDocument();
  });

  it('pairs a worker: its address, then the fingerprint to compare and the code', async () => {
    serve([]);
    const registered: unknown[] = [];
    const paired: unknown[] = [];
    server.use(
      http.post('*/api/v1/ai/workers', async ({ request }) => {
        registered.push(await request.json());
        return HttpResponse.json({ id: 'w9', fingerprint: FINGERPRINT }, { status: 201 });
      }),
      http.post('*/api/v1/ai/workers/w9/pair', async ({ request }) => {
        paired.push(await request.json());
        return HttpResponse.json(worker({ id: 'w9', name: 'worker' }));
      }),
    );
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click((await screen.findAllByRole('button', { name: 'Ajouter un worker' }))[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Ajouter un worker' });
    expect(within(dialog).getByLabelText('Adresse du worker')).toHaveValue('https://worker:8443');
    await user.click(within(dialog).getByRole('button', { name: 'Continuer' }));

    expect(await within(dialog).findByText(FINGERPRINT)).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText("Code d’appairage"), 'abcd-efgh');
    await user.click(within(dialog).getByRole('button', { name: 'Appairer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('worker est appairé.'));
    expect(registered).toEqual([{ url: 'https://worker:8443', name: 'worker', placement: 'local', proxyDeclared: false }]);
    expect(paired).toEqual([{ fingerprint: FINGERPRINT, code: 'abcd-efgh' }]);
  });

  it("says in words why the worker was not added", async () => {
    serve([]);
    server.use(http.post('*/api/v1/ai/workers', () =>
      HttpResponse.json({ status: 409, code: 'ai.worker_unreachable', detail: 'Unreachable.' }, { status: 409 })));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click((await screen.findAllByRole('button', { name: 'Ajouter un worker' }))[0]!);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Continuer' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent("Nid n’arrive pas à joindre le worker à cette adresse.");
  });

  it('says which field is missing', async () => {
    serve([]);
    server.use(http.post('*/api/v1/ai/workers', () => HttpResponse.json(
      { status: 422, detail: 'Invalid.', errors: [{ detail: 'This field is required.', pointer: '#/url', code: 'request.required' }] }, { status: 422 })));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click((await screen.findAllByRole('button', { name: 'Ajouter un worker' }))[0]!);
    const dialog = await screen.findByRole('dialog');
    await user.clear(within(dialog).getByLabelText('Adresse du worker'));
    await user.click(within(dialog).getByRole('button', { name: 'Continuer' }));

    await waitFor(() => expect(within(dialog).getByLabelText('Adresse du worker')).toHaveAccessibleDescription(/Indiquez l’adresse du worker/));
  });

  it('asks the address again for a worker whose key Nid never read', async () => {
    serve([worker({ status: 'pending', keyFingerprint: '', url: 'https://grenier:8443', name: 'Grenier' })]);
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(within(await screen.findByRole('row', { name: /Grenier/ })).getByRole('button', { name: 'Actions pour Grenier' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Appairer' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByLabelText('Adresse du worker')).toHaveValue('https://grenier:8443');
    expect(within(dialog).getByLabelText('Nom')).toHaveValue('Grenier');
  });

  it('resumes a worker left before its code', async () => {
    serve([worker({ status: 'pending' })]);
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(within(await screen.findByRole('row', { name: /Serveur du salon/ })).getByRole('button', { name: 'Actions pour Serveur du salon' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Appairer' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(FINGERPRINT)).toBeInTheDocument();
    expect(within(dialog).getByLabelText("Code d’appairage")).toBeInTheDocument();
  });

  it("sets how long the assistant's conversations are kept, 1 to 365 days", async () => {
    serve([worker()]);
    const saved: unknown[] = [];
    server.use(http.put('*/api/v1/ai/settings/conversation-retention', async ({ request }) => {
      saved.push(await request.json());
      return HttpResponse.json({ remoteWorkersAllowed: false, uses: USES, conversationRetentionDays: 7 });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const field = await screen.findByRole('textbox', { name: "Conserver les conversations de l’assistant (jours)" });
    const save = screen.getByRole('button', { name: 'Enregistrer la durée' });
    expect(field).toHaveValue('30');
    expect(save).toBeDisabled();
    await user.clear(field);
    await user.type(field, '400');
    expect(save).toBeDisabled();
    await user.clear(field);
    await user.type(field, '7');
    await user.click(save);

    await waitFor(() => expect(saved).toEqual([{ days: 7 }]));
  });

  it('marks a worker safe and opens remote workers, each after a confirmation', async () => {
    serve([worker()]);
    const safe: unknown[] = [];
    const remote: unknown[] = [];
    server.use(
      http.put('*/api/v1/ai/workers/w1/safe', async ({ request }) => { safe.push(await request.json()); return HttpResponse.json(worker({ safe: true })); }),
      http.put('*/api/v1/ai/settings/remote-workers', async ({ request }) => { remote.push(await request.json()); return HttpResponse.json({ remoteWorkersAllowed: true, uses: USES }); }),
    );
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(within(await screen.findByRole('row', { name: /Serveur du salon/ })).getByRole('button', { name: 'Actions pour Serveur du salon' }));
    const menu = await screen.findByRole('menu');
    await user.click(within(menu).getByRole('menuitem', { name: 'Marquer sûr' }));
    const safeConfirm = await screen.findByRole('alertdialog');
    expect(safeConfirm).toHaveTextContent('libellés bancaires');
    expect(safe).toEqual([]);
    await user.click(within(safeConfirm).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(safe).toEqual([{ safe: true }]));

    await user.click(screen.getByRole('switch', { name: 'Autoriser les workers distants' }));
    const remoteConfirm = await screen.findByRole('alertdialog');
    expect(remote).toEqual([]);
    await user.click(within(remoteConfirm).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(remote).toEqual([{ allowed: true }]));
  });

  it('tests a worker and points to its job', async () => {
    serve([worker()]);
    server.use(http.post('*/api/v1/ai/workers/w1/test', () => HttpResponse.json({ frames: 1, jobId: 'j1' })));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(within(await screen.findByRole('row', { name: /Serveur du salon/ })).getByRole('button', { name: 'Actions pour Serveur du salon' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Tester' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Test réussi : 1 morceau reçu.'));
    expect(within(toasts()).getByRole('button', { name: 'Voir les tâches' })).toBeInTheDocument();
  });

  it('revokes a worker after a confirmation', async () => {
    serve([worker()]);
    let revoked = 0;
    server.use(http.post('*/api/v1/ai/workers/w1/revoke', () => { revoked++; return HttpResponse.json(worker({ status: 'revoked' })); }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(within(await screen.findByRole('row', { name: /Serveur du salon/ })).getByRole('button', { name: 'Actions pour Serveur du salon' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Révoquer' }));
    const confirm = await screen.findByRole('alertdialog');
    expect(confirm).toHaveTextContent('Nid n’enverra plus rien à Serveur du salon.');
    await user.click(within(confirm).getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(revoked).toBe(1));
  });

  it('revokes a worker never paired without asking to unpair it', async () => {
    serve([worker({ status: 'pending' })]);
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(within(await screen.findByRole('row', { name: /Serveur du salon/ })).getByRole('button', { name: 'Actions pour Serveur du salon' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Révoquer' }));

    expect(await screen.findByRole('alertdialog')).not.toHaveTextContent('unpair');
  });

  it('lets an admin pick the model of a use among those the workers declare', async () => {
    serve([worker()]);
    const sent: unknown[] = [];
    server.use(http.put('*/api/v1/ai/settings/uses/chat/model', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ remoteWorkersAllowed: false, uses: USES });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const assistant = (await screen.findByRole('heading', { name: 'Assistant' })).parentElement!;
    await user.click(within(assistant).getByRole('combobox', { name: 'Modèle' }));
    await user.click(await screen.findByRole('option', { name: 'llama3.2 (1 worker)' }));

    await waitFor(() => expect(sent).toEqual([{ model: 'llama3.2' }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Réglage enregistré.'));
  });

  it('shows category suggestions on the search model, not a model to pick (#450)', async () => {
    serve([worker()], false, USES.map((u) => (u.use === 'embed' ? { ...u, model: 'bge-m3:latest' } : u)));
    await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const decide = (await screen.findByRole('heading', { name: 'Suggestions de catégories' })).parentElement!;
    expect(within(decide).queryByRole('combobox', { name: 'Modèle' })).not.toBeInTheDocument();
    expect(within(decide).getByRole('textbox', { name: 'Modèle' })).toHaveValue('bge-m3:latest (celui de la recherche par le sens)');
    expect(within(decide).getByRole('textbox', { name: 'Modèle' })).toHaveAttribute('readonly');
  });

  it('turns a use off', async () => {
    serve([worker()], false, USES.map((u) => (u.use === 'chat' ? { ...u, model: 'llama3.2' } : u)));
    const sent: unknown[] = [];
    server.use(http.put('*/api/v1/ai/settings/uses/chat', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ remoteWorkersAllowed: false, uses: USES });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const assistant = (await screen.findByRole('heading', { name: 'Assistant' })).parentElement!;
    await user.click(within(assistant).getByRole('combobox', { name: 'Réglage' }));
    await user.click(await screen.findByRole('option', { name: 'Désactivé' }));

    await waitFor(() => expect(sent).toEqual([{ mode: 'off' }]));
  });

  it("lets an admin enter a provider's key, which never comes back", async () => {
    serve([worker()]);
    const sent: unknown[] = [];
    server.use(http.put('*/api/v1/ai/cloud/keys/anthropic', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ region: 'eea_ch_uk', profiles: [{ ...PROFILE, keyHint: 'WXYZ' }] });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Ajouter la clé' }));
    const dialog = await screen.findByRole('dialog', { name: 'Clé Anthropic' });
    await user.type(within(dialog).getByLabelText("Clé d’API"), 'sk-ant-0123456789abcdef-WXYZ');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer la clé' }));

    await waitFor(() => expect(sent).toEqual([{ key: 'sk-ant-0123456789abcdef-WXYZ' }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Clé enregistrée.'));
  });

  it('enables a profile on its current terms after a confirmation, and offers the cloud to the uses', async () => {
    serve([worker()], false, USES, { ...PROFILE, keyHint: 'WXYZ' });
    const sent: unknown[] = [];
    server.use(http.post('*/api/v1/ai/cloud/profiles/anthropic-claude/enable', async ({ request }) => {
      sent.push(await request.json());
      serve([worker()], false, USES, { ...PROFILE, keyHint: 'WXYZ', enabled: true });
      return HttpResponse.json({ region: 'eea_ch_uk', profiles: [{ ...PROFILE, keyHint: 'WXYZ', enabled: true }] });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Activer' }));
    const confirm = await screen.findByRole('alertdialog');
    expect(confirm).toHaveTextContent('Chaque adulte en sera prévenu.');
    await user.click(within(confirm).getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(sent).toEqual([{ termsReviewedOn: '2026-11-22' }]));
    const assistant = (await screen.findByRole('heading', { name: 'Assistant' })).parentElement!;
    await user.click(within(assistant).getByRole('combobox', { name: 'Réglage' }));
    expect(await screen.findByRole('option', { name: 'Workers du foyer, puis le cloud' })).toBeInTheDocument();
    await user.keyboard('{Escape}');

    // A chat profile does not embed (#556): no cloud for the search.
    const search = screen.getByRole('heading', { name: 'Recherche par le sens' }).parentElement!;
    await user.click(within(search).getByRole('combobox', { name: 'Réglage' }));
    expect(await screen.findByRole('option', { name: 'Workers du foyer' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Workers du foyer, puis le cloud' })).not.toBeInTheDocument();
  });

  it('offers an enabled embed profile as the model of the search, the only one of its index (#556)', async () => {
    const embed = { ...PROFILE, id: 'openai-embed', provider: 'openai', model: 'text-embedding-3-small', enabled: true, keyHint: 'WXYZ', capabilities: ['embed'] };
    serve([worker()], false, USES, [PROFILE, embed]);
    const sent: unknown[] = [];
    server.use(http.put('*/api/v1/ai/settings/uses/embed/model', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ remoteWorkersAllowed: false, uses: USES, allowedKinds: [] });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const search = (await screen.findByRole('heading', { name: 'Recherche par le sens' })).parentElement!;
    await user.click(within(search).getByRole('combobox', { name: 'Réglage' }));
    expect(await screen.findByRole('option', { name: 'Workers du foyer, puis le cloud' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await user.click(within(search).getByRole('combobox', { name: 'Modèle' }));
    await user.click(await screen.findByRole('option', { name: 'text-embedding-3-small (OpenAI, cloud)' }));

    await waitFor(() => expect(sent).toEqual([{ model: 'text-embedding-3-small' }]));
    // The chat's cloud runs on its own profile's model: no embed model offered there.
    const assistant = screen.getByRole('heading', { name: 'Assistant' }).parentElement!;
    await user.click(within(assistant).getByRole('combobox', { name: 'Modèle' }));
    expect(screen.queryByRole('option', { name: /text-embedding-3-small/ })).not.toBeInTheDocument();
  });

  it('lets an admin pick the document types a cloud provider may read, none by default and never payslips (#556)', async () => {
    serve([worker()], false, USES, PROFILE, ['invoice']);
    const sent: unknown[] = [];
    server.use(http.put('*/api/v1/ai/settings/allowed-kinds', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ remoteWorkersAllowed: false, uses: USES, allowedKinds: ['invoice', 'contract'] });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const section = (await screen.findByRole('heading', { name: 'Documents envoyés au cloud' })).closest('section')!;
    expect(await within(section).findByRole('checkbox', { name: 'Factures' })).toBeChecked();
    expect(within(section).getAllByRole('checkbox').map((c) => c.getAttribute('aria-checked'))).toEqual(['false', 'false', 'true', 'false']);
    expect(within(section).queryByRole('checkbox', { name: /paie/i })).not.toBeInTheDocument();

    await user.click(within(section).getByRole('checkbox', { name: 'Contrats' }));

    await waitFor(() => expect(sent).toEqual([{ kinds: ['invoice', 'contract'] }]));
  });

  it('shows what a provider may do with the data', async () => {
    serve([worker()]);
    await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const section = (await screen.findByRole('heading', { name: 'Fournisseurs cloud' })).closest('section')!;
    expect(await within(section).findByText("N’entraîne pas ses modèles sur vos données")).toBeInTheDocument();
    expect(section).toHaveTextContent('Demandes conservées 30 jours chez le fournisseur');
  });

  it("shows the month's spend as the server counts it, and its alert", async () => {
    serve([worker()]);
    await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const section = (await screen.findByRole('heading', { name: 'Budget du cloud' })).closest('section')!;
    expect(await within(section).findByText(/16,50\s€ dépensés sur 20,00\s€ ; il reste 3,10\s€/)).toBeInTheDocument();
    expect(within(section).getByRole('status')).toHaveTextContent('80 % du budget du mois est dépensé.');
  });

  it('sets the ceiling, sent as the decimal typed', async () => {
    serve([worker()]);
    const sent: unknown[] = [];
    server.use(http.put('*/api/v1/ai/budget', async ({ request }) => {
      sent.push(await request.json());
      return HttpResponse.json({ ...BUDGET, monthlyBudgetEur: '30.00', status: 'ok' });
    }));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    const field = await screen.findByLabelText('Plafond mensuel (€)');
    await user.clear(field);
    await user.type(field, '30,00');
    await user.click(within(field.closest('form')!).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(sent).toEqual([{ monthlyBudgetEur: '30.00' }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Budget enregistré.'));
  });

  it("rotates Nid's key and says what each worker needs", async () => {
    serve([worker(), worker({ id: 'w2', name: 'Box' })]);
    server.use(http.post('*/api/v1/ai/key/rotate', () => HttpResponse.json({ fingerprint: ROTATED, unreached: ['w2'] })));
    const { user } = await renderApp('/admin/ai', { capabilities: adminCapabilities });

    await user.click(await screen.findByRole('button', { name: 'Faire tourner la clé de Nid' }));
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));

    const done = await screen.findByRole('dialog', { name: 'Nouvelle clé de Nid' });
    expect(done).toHaveTextContent(`confirm-rotation ${'ef56'.repeat(16)}`);
    expect(done).toHaveTextContent(/n’a pas pu prévenir.*Box/);
  });
});
