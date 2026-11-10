import { screen, waitFor, within } from '@testing-library/react';
import { HttpResponse, http } from 'msw/http';
import { beforeAll, describe, expect, it } from 'vitest';
import { detail, SALARY } from '@/features/income/testData';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';

const preview = (version: number) => ({ name: 'Salaire Alice', version, imported: false, removes: [], blockers: [] });

/** The flow is mounted by the module pages; the income source's one exercises it end to end. */
async function open() {
  const { user, router } = await renderApp(`/income/${SALARY}`);
  await screen.findByRole('heading', { name: 'Salaire Alice', level: 1 });
  await user.click(screen.getByRole('button', { name: 'Autres actions' }));
  await user.click(await screen.findByRole('menuitem', { name: 'Supprimer…' }));
  const dialog = await screen.findByRole('dialog', { name: 'Supprimer Salaire Alice' });
  return { user, router, dialog };
}

describe('DeleteItemFlow', () => {
  beforeAll(async () => { await import('@/features/income/IncomeSourceDetailPage'); await import('./DeleteItemFlow'); }, 30_000);

  it('deletes with the preview version through the item route, then leaves for the list', async () => {
    let version: string | null = null;
    server.use(
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/income/sources/:id/deletion', () => HttpResponse.json(preview(6))),
      http.delete('*/api/v1/income/sources/:id', ({ request }) => { version = new URL(request.url).searchParams.get('version'); return new HttpResponse(null, { status: 204 }); }),
    );
    const { user, router, dialog } = await open();
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Salaire Alice');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/income'));
    expect(version).toBe('6');
  });

  it('a 409 reads the preview again and asks for the name again', async () => {
    let reads = 0;
    server.use(
      http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(detail())),
      http.get('*/api/v1/income/sources/:id/deletion', () => { reads += 1; return HttpResponse.json(preview(reads)); }),
      http.delete('*/api/v1/income/sources/:id', () => HttpResponse.json({ status: 409, code: 'core.item_changed', title: 'Changed.' }, { status: 409 })),
    );
    const { user, dialog } = await open();
    await user.type(await within(dialog).findByLabelText(/Tapez/), 'Salaire Alice');
    await user.click(within(dialog).getByRole('button', { name: 'Supprimer définitivement' }));
    await waitFor(() => expect(reads).toBe(2));
    await waitFor(() => expect(within(dialog).getByLabelText(/Tapez/)).toHaveValue(''));
  });
});
