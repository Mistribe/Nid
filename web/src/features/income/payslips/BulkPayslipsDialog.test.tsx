import { File as NodeFile } from 'node:buffer';
import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { pick } from '@/test/controls';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { DocumentSummary } from '../../documents/api';
import { link, summary } from '../../documents/testData';
import type { IncomeExpected } from '../api';
import { detail, eur, SALARY } from '../testData';

type Month = IncomeExpected['months'][number];
type Occurrence = Month['occurrences'][number];

const pay = (phaseId: string, on: string): Occurrence => ({ phaseId, label: 'Salaire de base', on, amount: eur('2300.00') });
const month = (year: number, m: number, occurrences: Occurrence[]): Month => ({ year, month: m, oneOffs: [], total: eur('2300.00'), occurrences });
/** Phase payments in July, August and September 2026; October's comes later (today is 27 October 2026). */
const MONTHS = [
  month(2024, 3, [pay('p0', '2024-03-29')]),
  month(2026, 7, [pay('p1', '2026-07-31')]),
  month(2026, 8, [pay('p1', '2026-08-31')]),
  // The bonus is paid first in September: the month's payslip goes to its phase.
  month(2026, 9, [pay('p1', '2026-09-30'), pay('p2', '2026-09-15')]),
  month(2026, 10, []),
];
const september = (): DocumentSummary => summary({
  id: '0192f0aa-0000-7000-8000-00000000d0a1', title: 'Bulletin de paie septembre 2026', documentDate: '2026-09-30',
  links: [link({ targetKind: 'income.phase', targetId: 'p1', role: 'payslip', label: 'Salaire de base' })],
});

/** The expected ranges asked for, and each upload's fields in the order sent; `refuse` answers 415 to that upload (1-based). */
function serve({ docs = [] as DocumentSummary[], refuse = 0, failExpected = false, source = detail(), gate = undefined as Promise<void> | undefined } = {}) {
  const ranges: string[] = [];
  let listed = 0;
  const sent: Record<string, string>[] = [];
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(source)),
    http.get('*/api/v1/income/sources/:id/expected', ({ request, params }) => {
      const query = new URL(request.url).searchParams;
      const from = query.get('from')!, to = query.get('to')!;
      ranges.push(`${from}..${to}`);
      // The server's bound (`MaxYears = 2`): `to` before `from` plus two years.
      if (to >= `${Number(from.slice(0, 4)) + 2}${from.slice(4)}`) return HttpResponse.json({ status: 400, code: 'income.invalid_period' }, { status: 400 });
      // The history tab's own call is the last twelve months; the dialog's windows start from the oldest phase.
      if (failExpected && from < '2025-11-01') return HttpResponse.json({ status: 500, code: 'ERROR' }, { status: 500 });
      const months = MONTHS.filter((m) => { const key = `${m.year}-${String(m.month).padStart(2, '0')}`; return key >= from.slice(0, 7) && key <= to.slice(0, 7); });
      return HttpResponse.json({ id: params.id, from, to, months });
    }),
    http.get('*/api/v1/documents', () => { listed++; return HttpResponse.json({ items: docs }); }),
    http.post('*/api/v1/documents', async ({ request }) => {
      await gate;
      const form = await request.formData();
      sent.push(Object.fromEntries([...form.entries()].map(([key, value]) => [key, typeof value === 'string' ? value : value.name])));
      return sent.length === refuse
        ? HttpResponse.json({ status: 415, code: 'documents.unsupported_type' }, { status: 415 })
        : HttpResponse.json({ id: `0192f0aa-0000-7000-8000-0000000${String(sent.length).padStart(5, '0')}` }, { status: 201 });
    }),
  );
  return { ranges, sent, listed: () => listed };
}

const pdf = (name: string) => new File(['%PDF-1.7'], name, { type: 'application/pdf' });
const row = (name: string) => screen.getByText(`Mois de ${name}`).closest('li')!;
const SEND = { name: 'Envoyer' };

async function openWith(names: string[]) {
  const { user } = await renderApp(`/income/${SALARY}?tab=history`);
  await user.click(await screen.findByRole('button', { name: 'Déposer des bulletins' }));
  await user.upload(await screen.findByLabelText('Déposer'), names.map(pdf));
  return user;
}

describe('BulkPayslipsDialog', () => {
  beforeAll(async () => {
    // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
    vi.useFakeTimers({ toFake: ['Date'], now: new Date(2026, 9, 27, 12) });
    await import('../IncomeSourceDetailPage');
  }, 30_000);
  afterAll(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it('matches each file to its month and waits for the unrecognized one', async () => {
    const { ranges } = serve();
    const user = await openWith(['bulletin_2026-09.pdf', 'aout 2026.pdf', 'scan.pdf']);

    expect(screen.getAllByRole('listitem').map((li) => within(li).getByRole('combobox').textContent))
      .toEqual(['septembre 2026', 'août 2026', 'Choisir un mois']);
    // From the oldest phase's start (2024-01-01) to the end of this month, in windows under the server's two-year bound.
    expect(ranges).toEqual(expect.arrayContaining(['2024-01-01..2025-12-31', '2026-01-01..2026-10-31']));
    expect(screen.getByRole('button', SEND)).toBeDisabled();

    await pick(user, 'Mois de scan.pdf', 'juillet 2026');
    expect(screen.getByRole('button', SEND)).toBeEnabled();
    await expectNoAxeViolations(screen.getByRole('dialog'));
  });

  it('lets an unrecognized file be set aside instead', async () => {
    serve();
    const user = await openWith(['bulletin_2026-09.pdf', 'scan.pdf']);

    await user.click(within(row('scan.pdf')).getByRole('button', { name: 'Écarter' }));
    expect(screen.getByRole('button', SEND)).toBeEnabled();
  });

  it('leaves a month without payment unchosen and offers the paid months, newest first', async () => {
    serve();
    const user = await openWith(['bulletin_2025-01.pdf']);

    expect(within(row('bulletin_2025-01.pdf')).getByRole('combobox')).toHaveTextContent('Choisir un mois');
    expect(within(row('bulletin_2025-01.pdf')).getByText('Aucun versement ce mois-là')).toBeInTheDocument();
    await user.click(screen.getByRole('combobox', { name: 'Mois de bulletin_2025-01.pdf' }));
    expect((await screen.findAllByRole('option')).map((o) => o.textContent)).toEqual(['septembre 2026', 'août 2026', 'juillet 2026', 'mars 2024']);
  });

  it('warns of a month already covered or chosen twice, without blocking', async () => {
    serve({ docs: [september()] });
    await openWith(['bulletin_2026-09.pdf', 'aout 2026.pdf', '2026_08 bis.pdf', 'juillet 2026.pdf']);

    const already = (name: string) => within(row(name)).queryByText('Ce mois a déjà un bulletin');
    expect(already('bulletin_2026-09.pdf')).toBeInTheDocument();
    expect(already('aout 2026.pdf')).toBeInTheDocument();
    expect(already('2026_08 bis.pdf')).toBeInTheDocument();
    expect(already('juillet 2026.pdf')).not.toBeInTheDocument();
    expect(screen.getByRole('button', SEND)).toBeEnabled();
  });

  it('sends the files one by one in order, past a refused one, and not the set-aside ones', async () => {
    const { sent } = serve({ refuse: 2 });
    const user = await openWith(['juillet 2026.pdf', 'scan.pdf', 'aout 2026.pdf', 'bulletin_2026-09.pdf']);

    await user.click(within(row('scan.pdf')).getByRole('button', { name: 'Écarter' }));
    await user.click(screen.getByRole('button', SEND));

    await waitFor(() => expect(within(row('bulletin_2026-09.pdf')).getByText('Envoyé')).toBeInTheDocument());
    const fields = (file: string, targetId: string, date: string, title: string) => ({ file, targetKind: 'income.phase', targetId, role: 'payslip', date, title });
    expect(sent).toEqual([
      fields('juillet 2026.pdf', 'p1', '2026-07-31', 'Bulletin de paie juillet 2026'),
      fields('aout 2026.pdf', 'p1', '2026-08-31', 'Bulletin de paie août 2026'),
      fields('bulletin_2026-09.pdf', 'p2', '2026-09-15', 'Bulletin de paie septembre 2026'),
    ]);
    expect(within(row('juillet 2026.pdf')).getByText('Envoyé')).toBeInTheDocument();
    expect(within(row('aout 2026.pdf')).getByText(/aout 2026\.pdf : ce type de fichier n’est pas pris en charge/)).toBeInTheDocument();
    expect(within(row('scan.pdf')).queryByText('Envoyé')).not.toBeInTheDocument();
  });

  it('matches and sends a file for a month more than two years back, from the oldest window', async () => {
    const { sent } = serve();
    const user = await openWith(['bulletin_2024-03.pdf']);

    expect(within(row('bulletin_2024-03.pdf')).getByRole('combobox')).toHaveTextContent('mars 2024');
    await user.click(screen.getByRole('button', SEND));
    await waitFor(() => expect(within(row('bulletin_2024-03.pdf')).getByText('Envoyé')).toBeInTheDocument());
    expect(sent).toEqual([{ file: 'bulletin_2024-03.pdf', targetKind: 'income.phase', targetId: 'p0', role: 'payslip', date: '2024-03-29', title: 'Bulletin de paie mars 2024' }]);
  });

  it('reloads the documents when the sending ends, and keeps « Fermer » off while it runs', async () => {
    let release!: () => void;
    const { listed } = serve({ gate: new Promise<void>((resolve) => { release = resolve; }) });
    const user = await openWith(['juillet 2026.pdf']);
    const dialog = screen.getByRole('dialog');
    const footer = () => within(dialog).getAllByRole('button', { name: 'Fermer' }).find((b) => b.textContent === 'Fermer')!;
    const before = listed();

    await user.click(screen.getByRole('button', SEND));
    expect(footer()).toBeDisabled();
    release();

    await waitFor(() => expect(within(row('juillet 2026.pdf')).getByText('Envoyé')).toBeInTheDocument());
    await waitFor(() => expect(footer()).toBeEnabled());
    expect(listed()).toBeGreaterThan(before);
  });

  it('says the files go to the chosen month, not to « À classer »', async () => {
    serve();
    const { user } = await renderApp(`/income/${SALARY}?tab=history`);
    await user.click(await screen.findByRole('button', { name: 'Déposer des bulletins' }));

    expect(await screen.findByText('Chaque fichier est rattaché au mois choisi.')).toBeInTheDocument();
    expect(screen.queryByText(/À classer/)).not.toBeInTheDocument();
  });

  it('says why the months cannot be read and does not send', async () => {
    serve({ failExpected: true });
    const { user } = await renderApp(`/income/${SALARY}?tab=history`);
    await user.click(await screen.findByRole('button', { name: 'Déposer des bulletins' }));

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByRole('alert')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', SEND)).toBeDisabled();
  });

  it('is not offered to a reader of the source', async () => {
    serve({ source: detail({ canWrite: false }) });
    await renderApp(`/income/${SALARY}?tab=history`);

    expect(await screen.findByText('juillet 2026')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Déposer des bulletins' })).not.toBeInTheDocument();
  });
});
