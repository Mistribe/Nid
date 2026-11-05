import { File as NodeFile } from 'node:buffer';
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw/http';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { createApi } from '@/api/client';
import { createQueryClient } from '@/app/bootstrap';
import { Providers } from '@/app/Providers';
import { Toaster } from '@/components/ui/sonner';
import { expectNoAxeViolations } from '@/test/axe';
import { aliceSession, allCapabilities } from '@/test/fixtures';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import type { ExpectedDocument } from './api';
import { ExpectedDocuments } from './ExpectedDocuments';
import { ALICE, ITEM } from './testData';

const expected = (over: Partial<ExpectedDocument> = {}): ExpectedDocument => ({
  subject: 'property', targetKind: 'core.item', targetId: ITEM, label: 'Maison', module: 'realestate', role: 'deed', period: 0, ...over,
});
const statement = expected({ subject: 'life_insurance', label: 'Assurance vie', module: 'investments', role: 'statement', period: 2025 });
const notice = expected({ subject: 'tax_year', targetKind: 'core.person', targetId: ALICE, label: 'Alice', module: null, role: 'tax_notice', period: 2025 });

function serve(items: ExpectedDocument[]) {
  const writes: { path: string; body: unknown }[] = [];
  const asked: (string | null)[] = [];
  server.use(
    http.get('*/api/v1/documents/expected', ({ request }) => {
      asked.push(new URL(request.url).searchParams.get('itemId'));
      return HttpResponse.json({ items });
    }),
    http.post('*/api/v1/documents/expected/dismissals', async ({ request }) => {
      writes.push({ path: '/api/v1/documents/expected/dismissals', body: await request.json() });
      return HttpResponse.json({ id: ITEM });
    }),
    http.post('*/api/v1/documents', async ({ request }) => {
      const form = await request.formData();
      writes.push({ path: '/api/v1/documents', body: Object.fromEntries([...form.entries()].filter(([k]) => k !== 'file')) });
      return HttpResponse.json({ id: ITEM }, { status: 201 });
    }),
  );
  return { writes, asked };
}

async function renderList(itemId?: string) {
  const api = createApi({ getCsrf: () => aliceSession.csrf, onUnauthenticated: vi.fn() });
  const location = { pathname: '/', search: '', assign: vi.fn(), reload: vi.fn() };
  const router = createRouter({ routeTree: createRootRoute({ component: () => <ExpectedDocuments itemId={itemId} /> }), history: createMemoryHistory() });
  await router.load();
  const utils = render(
    <Providers api={api} queryClient={createQueryClient()} session={aliceSession} capabilities={allCapabilities} location={location}>
      <RouterProvider router={router} />
      <Toaster containerAriaLabel="Messages" />
    </Providers>,
  );
  return { ...utils, user: userEvent.setup() };
}

describe('ExpectedDocuments (#414)', () => {
  beforeAll(async () => {
    // As ItemDocuments: jsdom's FormData and File do not cross into Node's Request (MSW).
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
  });
  afterAll(() => { vi.unstubAllGlobals(); });

  it('names each missing document and its item, and shows nothing when none is', async () => {
    serve([expected(), statement, notice]);
    const { container } = await renderList();
    const section = await screen.findByRole('region', { name: 'Documents attendus' });
    const lines = within(section).getAllByRole('listitem');
    expect(lines.map((l) => l.textContent)).toEqual([
      expect.stringContaining('Acte d’achatMaison'), expect.stringContaining('Relevé annuel 2025Assurance vie'), expect.stringContaining('Avis d’imposition 2025Alice'),
    ]);
    await expectNoAxeViolations(container);
  });

  it('on an item, asks for that item and leaves its name out', async () => {
    const api = serve([expected()]);
    await renderList(ITEM);
    const line = await screen.findByRole('listitem');
    expect(line).toHaveTextContent('Acte d’achat');
    expect(line).not.toHaveTextContent('Maison');
    expect(api.asked).toEqual([ITEM]);
  });

  it('is absent with nothing expected', async () => {
    const api = serve([]);
    await renderList();
    await waitFor(() => expect(api.asked).toHaveLength(1));
    expect(screen.queryByRole('region', { name: 'Documents attendus' })).not.toBeInTheDocument();
  });

  it('« Pas disponible » stops asking for that document', async () => {
    const api = serve([statement]);
    const { user } = await renderList();
    await user.click(await screen.findByRole('button', { name: 'Pas disponible : Relevé annuel 2025' }));
    await waitFor(() => expect(api.writes).toEqual([{ path: '/api/v1/documents/expected/dismissals',
      body: { targetKind: 'core.item', targetId: ITEM, role: 'statement', period: 2025 } }]));
    await waitFor(() => expect(toasts()).toHaveTextContent('Ce document ne sera plus demandé.'));
  });

  it('« Déposer » files onto the right target with the type and the year preset', async () => {
    const api = serve([notice]);
    const { user } = await renderList();
    await user.click(await screen.findByRole('button', { name: 'Déposer : Avis d’imposition 2025' }));
    const dialog = await screen.findByRole('dialog', { name: 'Déposer un document' });
    expect(within(dialog).getByRole('combobox', { name: 'Type' })).toHaveTextContent('Avis d’imposition');
    expect(within(dialog).getByRole('textbox', { name: 'Date du document' })).toHaveValue('31/12/2025');
    await user.upload(within(dialog).getByLabelText('Fichiers'), new File(['%PDF-1.7'], 'avis.pdf', { type: 'application/pdf' }));
    await user.click(within(dialog).getByRole('button', { name: 'Déposer' }));
    await waitFor(() => expect(api.writes).toEqual([{ path: '/api/v1/documents',
      body: { targetKind: 'core.person', targetId: ALICE, role: 'tax_notice', date: '2025-12-31', warnDuplicate: 'true' } }]));
  });
});
