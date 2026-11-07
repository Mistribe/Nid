import { cleanup, screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { expectTrailBack } from '@/test/trail';
import { server } from '@/test/server';
import type { Statement } from './api';
import { list } from '../documents/testData';
import { ARRANGEMENT_ID, detail, september, statement, statements } from './testData';

const url = `/childcare/${ARRANGEMENT_ID}/statements/2025-09`;
type Write = { method: string; path: string; body: unknown };

function serve(body: Statement, writes: Write[] = [], answer: () => Response = () => HttpResponse.json(body)) {
  const record = async ({ request }: { request: Request }) => {
    writes.push({ method: request.method, path: new URL(request.url).pathname, body: await request.json().catch(() => null) });
    return answer();
  };
  server.use(
    http.get('*/api/v1/childcare/:id/statements/:month', () => HttpResponse.json(body)),
    http.put('*/api/v1/childcare/:id/statements/:month', record),
    http.delete('*/api/v1/childcare/:id/statements/:month', record),
    http.post('*/api/v1/childcare/:id/statements/:month/validate', record),
    http.post('*/api/v1/childcare/:id/statements/:month/reopen', record),
  );
}

beforeAll(async () => { await import('./StatementPage'); }, 30_000);

describe('StatementPage', () => {

  it('loading and error states show no inline back link: the bar leads back to the arrangement, named once read', async () => {
    server.use(http.get('*/api/v1/childcare/:id/statements/:month', async () => { await delay('infinite'); return HttpResponse.json({}); }));
    await renderApp(url);
    expect(await screen.findByLabelText('Chargement du relevé')).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Cette garde', `/childcare/${ARRANGEMENT_ID}`));
    expectTrailBack('Garde', '/childcare');
    cleanup();

    // Coming from the arrangement, its name is already in the cache: no request names it.
    server.use(
      http.get('*/api/v1/childcare/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/childcare/:id/statements', () => HttpResponse.json(statements())),
      http.get('*/api/v1/documents', () => HttpResponse.json(list([]))),
      http.get('*/api/v1/childcare/:id/statements/:month', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })),
    );
    const { router } = await renderApp(`/childcare/${ARRANGEMENT_ID}`);
    await screen.findByRole('heading', { level: 1, name: 'Garde de Léa' });
    await router.navigate({ to: '/childcare/$id/statements/$month', params: { id: ARRANGEMENT_ID, month: '2025-09' } });
    expect(await screen.findByRole('heading', { level: 1, name: 'Page introuvable' })).toBeInTheDocument();
    await waitFor(() => expectTrailBack('Garde de Léa', `/childcare/${ARRANGEMENT_ID}`));
    expectTrailBack('Garde', '/childcare');
  });
  it("shows the server's declaration", async () => {
    serve(statement());
    await renderApp(url);
    const declaration = await screen.findByRole('region', { name: 'Déclaration Pajemploi' });
    // Nid's figure and the value used, the same while nothing is typed by hand.
    expect(within(declaration).getAllByText('626,37')).toHaveLength(2);
    expect(within(declaration).getAllByText('146')).toHaveLength(2);
    expect(within(declaration).queryByText('≠ Nid')).not.toBeInTheDocument();
    // Pajemploi asks these as Oui / Non; none this month, no paid leave outside June.
    expect(within(declaration).getByText('Congés payés versés ?').nextSibling).toHaveTextContent('Non');
    expect(within(declaration).getByText('Heures majorées effectuées ?').nextSibling).toHaveTextContent('Non');
    expect(screen.getByRole('region', { name: 'Chiffres du mois' })).toHaveTextContent('avant CMG');
    expect(screen.getByRole('region', { name: 'Chiffres du mois' })).toHaveTextContent('Salaire net626,37 €');
  });

  it('names the week type next to the date', async () => {
    const days = september();
    days[0] = { ...days[0]!, weekType: 'Semaine courte' };
    serve(statement({ days }));
    await renderApp(url);
    expect(await screen.findByText('Semaine courte')).toBeInTheDocument();
  });

  it('marking a day absent sends the month with the reason', async () => {
    const writes: Write[] = [];
    serve(statement(), writes);
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Modifier le lundi 8 septembre' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('radio', { name: 'Absent' }));
    await pick(user, "Motif de l'absence", 'Enfant malade');
    await user.click(within(dialog).getByRole('button', { name: 'Enregistrer le jour' }));
    await waitFor(() => expect(writes).toHaveLength(1));
    const body = writes[0]!.body as { days: { date: string }[]; version: number; cmg: string | null };
    expect(body.version).toBe(3);
    expect(body.days).toHaveLength(30);
    expect(body.days[7]).toEqual({ date: '2025-09-08', present: false, arrival: null, departure: null, meal: false, absence: 'child_sick' });
  });

  it('the total of the month goes to the server with the days as they are', async () => {
    const writes: Write[] = [];
    serve(statement(), writes);
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Saisir le total du mois' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Jours de garde'), '15');
    await user.type(within(dialog).getByLabelText('Heures du mois'), '120,5');
    await user.click(within(dialog).getByRole('button', { name: 'Répartir sur le mois' }));
    await waitFor(() => expect(writes).toHaveLength(1));
    expect((writes[0]!.body as { summary: unknown }).summary).toEqual({ careDays: 15, hours: '120.5', absence: 'family' });
  });

  it('every field of the declaration can be typed in the second column, an empty one left to the server', async () => {
    const writes: Write[] = [];
    serve(statement(), writes);
    const { user } = await renderApp(url);
    const declaration = await screen.findByRole('region', { name: 'Déclaration Pajemploi' });
    expect(within(declaration).getAllByRole('textbox')).toHaveLength(9);
    await user.type(within(declaration).getByLabelText('Saisie : Salaire net'), '612,40');
    const [normalHours] = within(declaration).getAllByRole('textbox');
    await user.type(normalHours!, '120');
    await user.click(within(declaration).getByRole('button', { name: 'Enregistrer la saisie' }));
    await waitFor(() => expect(writes).toHaveLength(1));
    expect(writes[0]!.body).toMatchObject({ declared: { netPay: '612.40', normalHours: 120, upkeep: null } });
  });

  it("keeps Nid's figure beside the one typed by hand and marks the difference", async () => {
    const base = statement();
    serve(statement({
      figures: { ...base.figures!, declaration: { ...base.figures!.declaration, normalHours: 120 } },
      computed: base.figures!.declaration,
      declared: { normalHours: 120, activityDays: null, paidLeaveDays: null, complementaryHours: null, overtimeHours: null, netPay: null, upkeep: null, meals: null },
    }));
    await renderApp(url);
    const declaration = await screen.findByRole('region', { name: 'Déclaration Pajemploi' });
    const row = within(declaration).getByText('Heures normales').parentElement!;
    expect(row).toHaveTextContent('Calculé par Nid146');
    expect(row).toHaveTextContent('Valeur retenue120≠ Nid');
    expect(within(declaration).getAllByText('≠ Nid')).toHaveLength(1);
  });

  it('copy puts the French value on the clipboard', async () => {
    serve(statement());
    const { user } = await renderApp(url);
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await user.click(await screen.findByRole('button', { name: 'Copier : Salaire net' }));
    expect(writeText).toHaveBeenCalledWith('626,37');
  });

  it('an empty CMG shows the estimate', async () => {
    serve(statement({ cmg: null, cmgEstimate: '120.33' }));
    await renderApp(url);
    expect(await screen.findByText('Estimé d’après les derniers mois : 120,33')).toBeInTheDocument();
  });

  it('a problem hides the declaration and blocks validation', async () => {
    serve(statement({ figures: null, cmg: '0.00', problems: [{ field: 'days[7].planned', code: 'childcare.day_replanned' }] }));
    await renderApp(url);
    const declaration = await screen.findByRole('region', { name: 'Déclaration Pajemploi' });
    expect(declaration).toHaveTextContent('La déclaration apparaît quand chaque jour est cohérent');
    expect(screen.getByText(/ont changé ce jour depuis l'enregistrement/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Valider le relevé' })).toBeDisabled();
  });

  it('validate asks for confirmation then posts', async () => {
    const writes: Write[] = [];
    serve(statement({ cmg: '100.00' }), writes);
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Valider le relevé' }));
    const dialog = await screen.findByRole('alertdialog');
    await user.click(within(dialog).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(writes).toEqual([{ method: 'POST', path: `/api/v1/childcare/${ARRANGEMENT_ID}/statements/2025-09/validate`, body: { version: 3 } }]));
  });

  it('a validated month shows no editing', async () => {
    serve(statement({ state: 'validated', cmg: '100.00', canReopen: true }));
    await renderApp(url);
    expect(await screen.findByText('Ce relevé est validé : il se lit seulement.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Modifier le/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Valider le relevé' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Rouvrir' })).toBeInTheDocument();
  });

  it('reopen refused shows the server message', async () => {
    serve(statement({ state: 'validated', cmg: '100.00', canReopen: true }), [],
      () => HttpResponse.json({ type: 'about:blank', title: 'Conflict', status: 409, code: 'childcare.next_validated', detail: 'The next month is validated: reopen it first.' }, { status: 409 }));
    const { user } = await renderApp(url);
    await user.click(await screen.findByRole('button', { name: 'Rouvrir' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Le mois suivant est validé');
  });
});
