import { File as NodeFile } from 'node:buffer';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { CSRF_HEADER } from '@/api/client';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { detail, PEA } from '../testData';
import type { StatementImport } from '@/api/banking';

const preview = (over: Partial<StatementImport> = {}): StatementImport => ({
  format: 'ofx', accountId: null, newAccount: true, accountName: 'Relevé ·8901', ibanMasked: null, from: '2026-10-01', to: '2026-10-28',
  newCount: 12, knownCount: 3, probablyKnownCount: 2, currency: 'EUR', closingBalance: '1234.56', closingBalanceOn: '2026-10-28', written: false, ...over,
});

type Sent = { fields: Record<string, string>; file: string; csrf: string | null };

/** Banking on without Enable Banking, a checking envelope, its lines (counted) and an import answering `answer` to each form. */
function serve(answer: (fields: Record<string, string>) => Response = (f) => HttpResponse.json(preview({ written: f.confirm === 'true', newCount: f.confirm === 'true' ? 14 : 12 }))) {
  const sent: Sent[] = [];
  let lists = 0;
  server.use(
    http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(detail({ kind: 'checking' }))),
    http.get('*/api/v1/banking/status', () => HttpResponse.json({ enabled: true, configured: false })),
    http.get('*/api/v1/banking/envelopes/:id/transactions', () => { lists += 1; return HttpResponse.json({ items: [], hasMore: false }); }),
    http.post('*/api/v1/banking/statements', async ({ request }) => {
      const form = await request.formData();
      const fields = Object.fromEntries([...form.entries()].filter(([k]) => k !== 'file').map(([k, v]) => [k, v as string]));
      sent.push({ fields, file: (form.get('file') as File).name, csrf: request.headers.get(CSRF_HEADER) });
      return answer(fields);
    }),
  );
  return { sent, lists: () => lists };
}

const ofx = () => new File(['<OFX></OFX>'], 'releve.ofx', { type: 'application/x-ofx' });

async function openFromEnvelope() {
  const app = await renderApp(`/investments/${PEA}?tab=operations`);
  await app.user.click(await screen.findByRole('button', { name: 'Importer un relevé' }));
  const dialog = await screen.findByRole('dialog', { name: 'Importer un relevé' });
  await app.user.upload(within(dialog).getByLabelText('Relevé (OFX ou CAMT.053)'), ofx());
  await app.user.click(within(dialog).getByRole('button', { name: 'Lire le relevé' }));
  return { ...app, dialog };
}

describe('ImportStatementDialog', () => {
  beforeAll(async () => {
    // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
    await import('../EnvelopeDetailPage');
    await import('./OperationsTab');
    await import('./ImportStatementDialog');
  }, 30_000);
  afterAll(() => { vi.unstubAllGlobals(); });

  it('reads a dropped statement, and refuses a file of another type without sending it', async () => {
    const api = serve();
    const { user } = await renderApp(`/investments/${PEA}?tab=operations`);
    await user.click(await screen.findByRole('button', { name: 'Importer un relevé' }));
    const dialog = await screen.findByRole('dialog', { name: 'Importer un relevé' });
    const zone = within(dialog).getByText('Glissez un fichier ici').closest('[data-drop-zone]')!;

    fireEvent.drop(zone, { dataTransfer: { files: [new File(['%PDF'], 'releve.pdf', { type: 'application/pdf' })] } });
    expect(await within(dialog).findByText('Ce fichier n\'est ni un relevé OFX ni un relevé CAMT.053.')).toBeInTheDocument();

    fireEvent.drop(zone, { dataTransfer: { files: [ofx()] } });
    await user.click(within(dialog).getByRole('button', { name: 'Lire le relevé' }));
    await waitFor(() => expect(api.sent.map((s) => s.file)).toEqual(['releve.ofx']));
  });

  it('previews the file, then imports it with « Importer » and refreshes the tab', async () => {
    const api = serve();
    const { user, dialog, container } = await openFromEnvelope();

    expect(await within(dialog).findByText('12 nouvelles, 3 déjà connues, 2 probablement déjà connues')).toBeInTheDocument();
    expect(dialog).toHaveTextContent('Du 1 octobre 2026 au 28 octobre 2026');
    expect(dialog).toHaveTextContent('Solde de clôture : 1 234,56 € au 28 octobre 2026');
    expect(dialog).toHaveTextContent('Relevé ·8901');
    expect(within(dialog).getByText('Nouveau compte')).toBeInTheDocument();
    expect(api.sent.map(({ fields, file }) => ({ fields, file }))).toEqual([{ fields: { envelopeId: PEA, confirm: 'false', keepProbablyKnown: 'false' }, file: 'releve.ofx' }]);
    // The session's CSRF header goes with the form, as with a document.
    expect(api.sent[0]!.csrf).toBeTruthy();
    await expectNoAxeViolations(container);

    const lists = api.lists();
    await user.click(within(dialog).getByRole('switch', { name: 'Garder aussi les opérations probablement déjà connues' }));
    await user.click(within(dialog).getByRole('button', { name: 'Importer' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(api.sent[1]?.fields).toEqual({ envelopeId: PEA, confirm: 'true', keepProbablyKnown: 'true' });
    expect(api.sent[1]?.file).toBe('releve.ofx');
    expect(await screen.findByText('Relevé importé : 14 opérations ajoutées.')).toBeInTheDocument();
    await waitFor(() => expect(api.lists()).toBeGreaterThan(lists));
  });

  it('offers no switch when nothing is probably known, and says nothing of a known account', async () => {
    serve(() => HttpResponse.json(preview({ probablyKnownCount: 0, newAccount: false, accountName: 'Compte courant', ibanMasked: 'FR76 •••• 1234' })));
    const { dialog } = await openFromEnvelope();

    expect(await within(dialog).findByText('12 nouvelles, 3 déjà connues, 0 probablement déjà connue')).toBeInTheDocument();
    expect(dialog).toHaveTextContent('FR76 •••• 1234');
    expect(within(dialog).queryByText('Nouveau compte')).toBeNull();
    expect(within(dialog).queryByRole('switch')).toBeNull();
  });

  it.each([
    [422, { code: 'banking.file_format' }, "Ce fichier n'est ni un relevé OFX ni un relevé CAMT.053."],
    [422, { code: 'banking.file_malformed', line: 42 }, 'Le relevé est illisible (ligne 42).'],
    [422, { code: 'banking.file_malformed' }, 'Le relevé est illisible.'],
    [413, { code: 'banking.file_too_large' }, 'Le relevé dépasse 5 Mo.'],
    [422, { code: 'banking.file_account_mismatch' }, 'Ce relevé ne correspond pas au compte de cette enveloppe.'],
    [422, { code: 'banking.file_several_accounts' }, 'Ce fichier contient les relevés de plusieurs comptes : importez-les séparément.'],
    [422, { code: 'banking.currency_mismatch' }, "La devise du compte et celle de l'enveloppe diffèrent."],
  ])('announces a %i %o in French', async (status, body, text) => {
    serve(() => HttpResponse.json({ status, title: 'Refused.', ...body }, { status }));
    const { dialog } = await openFromEnvelope();

    // Announced: the focus stays on « Lire le relevé ».
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(text);
  });

  it('locks the file while it is read, and drops an answer about a file no longer picked', async () => {
    let answer!: () => void;
    const api = serve((fields) => {
      if (api.sent.length > 1) return HttpResponse.json(preview({ newCount: 5, written: fields.confirm === 'true' }));
      // The first file's answer waits until the test lets it go.
      return new Promise<Response>((resolve) => { answer = () => resolve(HttpResponse.json(preview())); }) as unknown as Response;
    });
    const { dialog } = await openFromEnvelope();
    const input = within(dialog).getByLabelText('Relevé (OFX ou CAMT.053)');

    await waitFor(() => expect(input).toBeDisabled());
    // Another file, picked anyway (the input was enabled in an older build): the first answer must not stand for it.
    fireEvent.change(input, { target: { files: [new File(['<OFX></OFX>'], 'autre.ofx', { type: 'application/x-ofx' })] } });
    answer();

    await waitFor(() => expect(input).toBeEnabled());
    expect(within(dialog).queryByText(/12 nouvelles/)).toBeNull();
    expect(within(dialog).getByRole('button', { name: 'Lire le relevé' })).toBeInTheDocument();
  });

  it('imports from Réglages › Banques without an envelope, banking on but not configured', async () => {
    const api = serve();
    server.use(http.get('*/api/v1/banking/connections', () => HttpResponse.json([])), http.get('*/api/v1/banking/accounts', () => HttpResponse.json([])));
    const { user } = await renderApp('/settings/banks');

    await user.click(await screen.findByRole('button', { name: 'Importer un relevé' }));
    const dialog = await screen.findByRole('dialog', { name: 'Importer un relevé' });
    await user.upload(within(dialog).getByLabelText('Relevé (OFX ou CAMT.053)'), ofx());
    await user.click(within(dialog).getByRole('button', { name: 'Lire le relevé' }));

    // The file's currency comes with the preview: no envelope is needed to read the balance.
    expect(await within(dialog).findByText('Solde de clôture : 1 234,56 € au 28 octobre 2026')).toBeInTheDocument();
    expect(api.sent[0]!.fields).toEqual({ confirm: 'false', keepProbablyKnown: 'false' });
  });
});
