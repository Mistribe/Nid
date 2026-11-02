import { File as NodeFile } from 'node:buffer';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { todayIso } from '../../subscriptions/today';
import { contentUrl, type DocumentSummary } from '../../documents/api';
import { link, summary } from '../../documents/testData';
import type { IncomeExpected, IncomeSourceDetail } from '../api';
import { detail, eur, SALARY } from '../testData';

type Month = IncomeExpected['months'][number];
type Sent = { method: string; path: string; form?: Record<string, string> };

const SLIP = '0192f0aa-0000-7000-8000-00000000d0a1';
const NEW = '0192f0aa-0000-7000-8000-00000000d0a2';
const paid = (on: string): Month => ({
  year: Number(on.slice(0, 4)), month: Number(on.slice(5, 7)), oneOffs: [], total: eur('2300.00'),
  occurrences: [{ phaseId: 'p1', label: 'Salaire de base', on, amount: eur('2300.00') }],
});
/** August's payslip, linked to the base salary's phase and to the source itself. */
const august = (over: Partial<DocumentSummary> = {}) => summary({
  id: SLIP, title: 'Bulletin de paie août 2026', documentDate: '2026-08-31', ...over,
  links: [
    link({ targetKind: 'income.phase', targetId: 'p1', role: 'payslip', label: 'Salaire de base' }),
    link({ targetKind: 'core.item', targetId: SALARY, role: 'payslip', label: 'Salaire Alice' }),
  ],
});

/** The source, its payments, its anchored documents, and the writes in the order they were sent. */
function serve({ months = [paid('2026-08-31'), paid('2026-09-30')], docs = [august()], source = detail(), failDelete = false }: {
  months?: Month[]; docs?: DocumentSummary[]; source?: IncomeSourceDetail; failDelete?: boolean;
} = {}) {
  const sent: Sent[] = [];
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(source)),
    http.get('*/api/v1/income/sources/:id/expected', ({ params }) => HttpResponse.json({ id: params.id, from: '2025-11-01', to: '2026-10-31', months })),
    http.get('*/api/v1/documents', () => HttpResponse.json({ items: docs })),
    http.post('*/api/v1/documents', async ({ request }) => {
      const form = Object.fromEntries([...(await request.formData()).entries()].filter(([key]) => key !== 'file')) as Record<string, string>;
      sent.push({ method: 'POST', path: new URL(request.url).pathname, form });
      return HttpResponse.json({ id: NEW }, { status: 201 });
    }),
    http.delete('*/api/v1/documents/:id/links/:kind/:target', ({ request }) => {
      sent.push({ method: 'DELETE', path: new URL(request.url).pathname });
      return failDelete ? HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 }) : new HttpResponse(null, { status: 204 });
    }),
  );
  return sent;
}

const HISTORY = `/income/${SALARY}?tab=history`;
const pdf = (name: string) => new File(['%PDF-1.7'], name, { type: 'application/pdf' });
/** The row group of a month (« septembre 2026 »). */
async function month(label: string) {
  const group = (await screen.findByText(label)).closest('tbody');
  if (!group) throw new Error(`no row group for ${label}`);
  return group;
}
const MENU = 'Actions du bulletin Bulletin de paie août 2026';

describe('PayslipCell', () => {
  beforeAll(async () => {
    // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
    await import('../IncomeSourceDetailPage');
  }, 30_000);
  afterAll(() => { vi.unstubAllGlobals(); });

  it('links a covered month’s payslip to its file and offers its actions', async () => {
    serve();
    const { user, container } = await renderApp(HISTORY);
    const cell = await month('août 2026');

    expect(await within(cell).findByRole('link', { name: 'Bulletin de paie août 2026' })).toHaveAttribute('href', contentUrl(SLIP));
    expect(within(cell).queryByRole('button', { name: 'Joindre' })).not.toBeInTheDocument();
    await user.click(within(cell).getByRole('button', { name: MENU }));
    const items = (await screen.findAllByRole('menuitem')).map((i) => i.textContent);
    expect(items).toEqual(['Télécharger', 'Voir le document', 'Remplacer', 'Retirer']);
    await user.keyboard('{Escape}');
    await expectNoAxeViolations(container);
  });

  it('removes every link to the source after a confirmation, and keeps the document', async () => {
    const sent = serve();
    const { user } = await renderApp(HISTORY);
    const cell = await month('août 2026');

    await user.click(await within(cell).findByRole('button', { name: MENU }));
    await user.click(await screen.findByRole('menuitem', { name: 'Retirer' }));
    expect(await screen.findByText('Retirer ce bulletin de ce revenu ? Il reste dans Documents.')).toBeInTheDocument();
    expect(sent).toEqual([]);
    await user.click(screen.getByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(sent).toEqual([
      { method: 'DELETE', path: `/api/v1/documents/${SLIP}/links/income.phase/p1` },
      { method: 'DELETE', path: `/api/v1/documents/${SLIP}/links/core.item/${SALARY}` },
    ]));
  });

  it('offers neither « Remplacer » nor « Retirer » on a document the actor cannot edit', async () => {
    serve({ docs: [august({ canEdit: false })] });
    const { user } = await renderApp(HISTORY);

    await user.click(await within(await month('août 2026')).findByRole('button', { name: MENU }));
    expect((await screen.findAllByRole('menuitem')).map((i) => i.textContent)).toEqual(['Télécharger', 'Voir le document']);
  });

  it('says a past month has no payslip, in muted text, and offers to attach one', async () => {
    serve();
    await renderApp(HISTORY);
    const cell = await month('septembre 2026');

    const missing = await within(cell).findByText('Pas de bulletin');
    expect(missing).toHaveClass('text-muted-foreground');
    expect(cell.innerHTML).not.toMatch(/text-status-/);
    expect(within(cell).getByRole('button', { name: 'Joindre' })).toBeInTheDocument();
  });

  it('still offers « Joindre » on an ended source the actor can write to (a payslip arrives after the end)', async () => {
    serve({ source: detail({ ended: true, endedOn: '2026-09-30', canWrite: true }) });
    await renderApp(HISTORY);

    expect(await within(await month('septembre 2026')).findByRole('button', { name: 'Joindre' })).toBeInTheDocument();
  });

  it('shows a reader the payslips and their reading actions only', async () => {
    serve({ source: detail({ canWrite: false }) });
    const { user } = await renderApp(HISTORY);

    expect(await within(await month('septembre 2026')).findByText('Pas de bulletin')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Joindre' })).not.toBeInTheDocument();
    await user.click(within(await month('août 2026')).getByRole('button', { name: MENU }));
    expect((await screen.findAllByRole('menuitem')).map((i) => i.textContent)).toEqual(['Télécharger', 'Voir le document']);
  });

  it('attaches a file to the month’s payment', async () => {
    const sent = serve();
    const { user } = await renderApp(HISTORY);
    const cell = await month('septembre 2026');

    await user.click(await within(cell).findByRole('button', { name: 'Joindre' }));
    await user.upload(within(cell).getByLabelText('Joindre'), pdf('scan.pdf'));

    await waitFor(() => expect(sent).toEqual([{
      method: 'POST', path: '/api/v1/documents',
      form: { targetKind: 'income.phase', targetId: 'p1', role: 'payslip', date: '2026-09-30', title: 'Bulletin de paie septembre 2026' },
    }]));
  });

  it('attaches a file dropped on « Joindre », and refuses one of another type without sending it', async () => {
    const sent = serve();
    await renderApp(HISTORY);
    const cell = await month('septembre 2026');
    const zone = (await within(cell).findByRole('button', { name: 'Joindre' })).closest('[data-drop-zone]')!;

    fireEvent.drop(zone, { dataTransfer: { files: [new File(['hello'], 'notes.txt', { type: 'text/plain' })] } });
    expect(await within(cell).findByText('notes.txt : ce type de fichier n’est pas pris en charge (PDF, PNG, JPEG ou e-mail .eml).')).toBeInTheDocument();
    expect(sent).toEqual([]);

    fireEvent.drop(zone, { dataTransfer: { files: [pdf('scan.pdf')] } });
    await waitFor(() => expect(sent.map((s) => s.form?.title)).toEqual(['Bulletin de paie septembre 2026']));
  });

  it('dates the current month’s payslip today when its payment comes later', async () => {
    const today = todayIso();
    const endOfMonth = new Date(Date.UTC(Number(today.slice(0, 4)), Number(today.slice(5, 7)), 0)).toISOString().slice(0, 10);
    const sent = serve({ months: [paid(endOfMonth)], docs: [] });
    const { user } = await renderApp(HISTORY);
    const cell = (await screen.findByRole('button', { name: 'Joindre' })).closest('tbody')!;

    await user.upload(within(cell).getByLabelText('Joindre'), pdf('scan.pdf'));
    await waitFor(() => expect(sent.map((s) => s.form?.date)).toEqual([today]));
  });

  it('shows a refused file as the documents page does', async () => {
    serve();
    server.use(http.post('*/api/v1/documents', () => HttpResponse.json({ status: 413, code: 'documents.too_large' }, { status: 413 })));
    const { user } = await renderApp(HISTORY);
    const cell = await month('septembre 2026');

    await user.upload(await within(cell).findByLabelText('Joindre'), pdf('scan.pdf'));
    expect(await within(cell).findByText('scan.pdf : le fichier dépasse la taille permise (25 Mo).')).toBeInTheDocument();
  });

  it('replaces a payslip: uploads the new file first, then removes the old one', async () => {
    const sent = serve();
    const { user } = await renderApp(HISTORY);
    const cell = await month('août 2026');

    await user.click(await within(cell).findByRole('button', { name: MENU }));
    await user.click(await screen.findByRole('menuitem', { name: 'Remplacer' }));
    await user.upload(within(cell).getByLabelText('Remplacer'), pdf('new.pdf'));

    await waitFor(() => expect(sent).toEqual([
      { method: 'POST', path: '/api/v1/documents', form: { targetKind: 'income.phase', targetId: 'p1', role: 'payslip', date: '2026-08-31', title: 'Bulletin de paie août 2026' } },
      { method: 'DELETE', path: `/api/v1/documents/${SLIP}/links/income.phase/p1` },
      { method: 'DELETE', path: `/api/v1/documents/${SLIP}/links/core.item/${SALARY}` },
    ]));
  });

  it('keeps the new payslip and says so when the old one cannot be removed', async () => {
    const sent = serve({ failDelete: true });
    const { user } = await renderApp(HISTORY);
    const cell = await month('août 2026');

    await user.upload(await within(cell).findByLabelText('Remplacer'), pdf('new.pdf'));

    expect(await within(cell).findByRole('alert')).toBeInTheDocument();
    // The first refused removal stops the others.
    expect(sent.map((s) => s.method)).toEqual(['POST', 'DELETE']);
  });
});
