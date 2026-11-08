import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw/http';
import { File as NodeFile } from 'node:buffer';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type { components } from '@/api/schema';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { toasts } from '@/test/toasts';
import { link, summary } from '../../documents/testData';
import { todayIso } from '@/lib/today';
import type { IncomeSourceDetail } from '../api';
import { detail, eur, SALARY } from '../testData';

type Sent = { method: string; path: string; form?: Record<string, string> };
type Body = components['schemas']['IncomeOneOffBody'];
const PROJECT = { id: 'o1', occurredOn: '2026-03-10', amount: eur('400.00'), note: 'Prime de projet' };
const OLDER = { id: 'o0', occurredOn: '2025-12-24', amount: eur('150.00'), note: null };

function serve(initial: IncomeSourceDetail = detail({ oneOffs: [OLDER, PROJECT] })) {
  let current = initial;
  let next = 2;
  const sent: { method: string; url: string; body?: Body }[] = [];
  const view = (id: string, b: Body) => ({ id, occurredOn: b.occurredOn!, amount: { amount: b.amount!.amount, currency: 'EUR' }, note: b.note ?? null });
  server.use(
    http.get('*/api/v1/income/sources/:id', () => HttpResponse.json(current)),
    http.post('*/api/v1/income/sources/:id/one-offs', async ({ request }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'POST', url: request.url, body });
      const id = `o${next++}`;
      current = { ...current, oneOffs: [...current.oneOffs, view(id, body)] };
      return HttpResponse.json({ id }, { status: 201 });
    }),
    http.put('*/api/v1/income/sources/:id/one-offs/:flowId', async ({ request, params }) => {
      const body = (await request.json()) as Body;
      sent.push({ method: 'PUT', url: request.url, body });
      if (!current.oneOffs.some((f) => f.id === params.flowId))
        return HttpResponse.json({ status: 409, code: 'core.fact_retired', title: 'Retired.' }, { status: 409 });
      const id = `o${next++}`;
      current = { ...current, oneOffs: current.oneOffs.map((f) => (f.id === params.flowId ? view(id, body) : f)) };
      return HttpResponse.json({ id });
    }),
    http.delete('*/api/v1/income/sources/:id/one-offs/:flowId', ({ request, params }) => {
      sent.push({ method: 'DELETE', url: request.url });
      if (!current.oneOffs.some((f) => f.id === params.flowId))
        return HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 });
      current = { ...current, oneOffs: current.oneOffs.filter((f) => f.id !== params.flowId) };
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { sent, retire: (id: string) => { current = { ...current, oneOffs: current.oneOffs.filter((f) => f.id !== id) }; } };
}

const region = () => screen.findByRole('region', { name: 'Revenus exceptionnels' });
/** The row's `⋯` menu, then its item. */
const choose = async (user: Awaited<ReturnType<typeof renderApp>>['user'], row: number, item: string) => {
  await user.click(within((await rows())[row]!).getByRole('button', { name: /^Actions du revenu exceptionnel/ }));
  await user.click(await screen.findByRole('menuitem', { name: item }));
};
const rows = async () => within(await screen.findByRole('table', { name: 'Revenus exceptionnels des 12 derniers mois' })).getAllByRole('row').slice(1);

describe('one-off income', () => {
  beforeAll(async () => {
    // jsdom's FormData and File do not cross into Node's Request (MSW): a browser sends them as is, the tests use Node's.
    const form = await new Response('a=1', { headers: { 'content-type': 'application/x-www-form-urlencoded' } }).formData();
    vi.stubGlobal('FormData', form.constructor);
    vi.stubGlobal('File', NodeFile);
    await import('../IncomeSourceDetailPage');
  }, 30_000);

  it('lists the last twelve months’ one-offs, newest first', async () => {
    serve();
    const { container } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    const [first, second] = await rows();
    expect(first).toHaveTextContent('10 mars 2026');
    expect(first).toHaveTextContent('400,00 €');
    expect(first).toHaveTextContent('Prime de projet');
    expect(second).toHaveTextContent('24 décembre 2025');
    await expectNoAxeViolations(container);
  });

  it('says when there is none', async () => {
    serve(detail({ oneOffs: [] }));
    await renderApp(`/income/${SALARY}?tab=oneoffs`);

    expect(await within(await region()).findByText('Aucun revenu exceptionnel sur les 12 derniers mois.')).toBeInTheDocument();
  });

  it('adds a one-off dated today by default, announces it and focuses the section', async () => {
    const { sent } = serve();
    const { user, container } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    const form = screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' });
    expect(within(form).getByLabelText('Date')).toHaveValue(todayIso().split('-').reverse().join('/'));
    expect(within(form).getByLabelText('Date')).toHaveFocus();
    expect(within(form).getByText('Une prime à venir se saisit une fois versée.')).toBeInTheDocument();
    await user.type(within(form).getByLabelText('Montant net'), '250,50');
    await user.type(within(form).getByLabelText('Note'), 'Intéressement');
    await expectNoAxeViolations(container);
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Revenu exceptionnel ajouté.'));
    expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { occurredOn: todayIso(), amount: { amount: '250.50', currency: 'EUR' }, note: 'Intéressement' } })]);
    expect(screen.getByRole('heading', { name: 'Revenus exceptionnels' })).toHaveFocus();
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('250,50 €'));
  });

  it('asks for the net taxable income of a salary’s one-off only', async () => {
    serve();
    const first = await renderApp(`/income/${SALARY}?tab=oneoffs`);
    await first.user.click(await within(await region()).findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    expect(within(screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' })).getByLabelText(/Net imposable/)).toBeInTheDocument();
    first.unmount();

    serve(detail({ kind: 'rent', oneOffs: [] }));
    const other = await renderApp(`/income/${SALARY}?tab=oneoffs`);
    await other.user.click(await within(await region()).findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    expect(within(screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' })).queryByLabelText(/Net imposable/)).not.toBeInTheDocument();
  });

  it('refuses a date after today before sending', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    const form = screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' });
    const tomorrow = todayIso(new Date(Date.now() + 86_400_000));
    await user.clear(within(form).getByLabelText('Date'));
    await user.type(within(form).getByLabelText('Date'), tomorrow);
    await user.type(within(form).getByLabelText('Montant net'), '100');
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));

    expect(await within(form).findByText('Cette date est dans le futur.')).toBeInTheDocument();
    expect(sent).toEqual([]);
  });

  it('corrects a one-off: a new id, the change announced', async () => {
    const { sent } = serve();
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await choose(user, 0, 'Corriger');
    const form = screen.getByRole('form', { name: 'Corriger le revenu exceptionnel' });
    expect(within(form).getByLabelText('Montant net')).toHaveValue('400');
    await user.clear(within(form).getByLabelText('Montant net'));
    await user.type(within(form).getByLabelText('Montant net'), '450');
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Revenu exceptionnel corrigé.'));
    await waitFor(async () => expect((await rows())[0]).toHaveTextContent('450,00 €'));
    expect(sent[0]).toMatchObject({ method: 'PUT', body: { occurredOn: '2026-03-10', amount: { amount: '450', currency: 'EUR' }, note: 'Prime de projet' } });
    expect(sent[0]!.url).toContain('/one-offs/o1');
  });

  it('gives the focus back to the row menu when the correction is cancelled', async () => {
    serve();
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await choose(user, 0, 'Corriger');
    await user.click(within(screen.getByRole('form', { name: 'Corriger le revenu exceptionnel' })).getByRole('button', { name: 'Annuler' }));

    await waitFor(async () => expect(within((await rows())[0]!).getByRole('button', { name: /^Actions du revenu exceptionnel/ })).toHaveFocus());
  });

  it('deletes a one-off after a confirmation', async () => {
    serve(detail({ oneOffs: [PROJECT] }));
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await choose(user, 0, 'Supprimer');
    expect(await screen.findByText('Ce revenu exceptionnel sera retiré.')).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

    await waitFor(() => expect(toasts()).toHaveTextContent('Revenu exceptionnel supprimé.'));
    expect(await within(await region()).findByText('Aucun revenu exceptionnel sur les 12 derniers mois.')).toBeInTheDocument();
  });

  it('shows a correction of a one-off changed meanwhile with its own sentence and « Recharger »', async () => {
    const { retire } = serve();
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await choose(user, 0, 'Corriger');
    retire('o1');
    const form = screen.getByRole('form', { name: 'Corriger le revenu exceptionnel' });
    await user.click(within(form).getByRole('button', { name: 'Enregistrer' }));

    expect(await within(form).findByText('Cette ligne a déjà été corrigée ou supprimée.')).toBeInTheDocument();
    expect(within(form).getByRole('button', { name: 'Recharger' })).toBeInTheDocument();

    await user.click(within(form).getByRole('button', { name: 'Recharger' }));

    await waitFor(() => expect(screen.queryByRole('form', { name: 'Corriger le revenu exceptionnel' })).not.toBeInTheDocument());
    expect(screen.getByRole('heading', { name: 'Revenus exceptionnels' })).toHaveFocus();
  });

  it('puts the focus on the section when « Recharger » removes the row of a deletion refused as gone', async () => {
    const { retire } = serve();
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await choose(user, 0, 'Supprimer');
    retire('o1');
    await user.click(await screen.findByRole('button', { name: 'Confirmer' }));
    await user.click(await screen.findByRole('button', { name: 'Recharger' }));

    await waitFor(async () => expect(await rows()).toHaveLength(1));
    expect(screen.getByRole('heading', { name: 'Revenus exceptionnels' })).toHaveFocus();
  });

  it('gives a reader the list without any action', async () => {
    serve(detail({ canWrite: false, visibility: 'shared', oneOffs: [PROJECT] }));
    await renderApp(`/income/${SALARY}?tab=oneoffs`);

    const section = await region();
    await rows();
    expect(within(section).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
  });

  it('lists every one-off on « Tout », older ones included (issue #22)', async () => {
    serve();
    const ANCIENT = { id: 'oa', occurredOn: '2023-02-01', amount: eur('90.00'), note: 'Ancienne prime' };
    const asked: (string | null)[] = [];
    server.use(http.get('*/api/v1/income/sources/:id', ({ request }) => {
      const from = new URL(request.url).searchParams.get('from');
      asked.push(from);
      return HttpResponse.json(detail({ oneOffs: from ? [ANCIENT, OLDER, PROJECT] : [OLDER, PROJECT] }));
    }));
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    expect(await rows()).toHaveLength(2);
    await user.click(within(await region()).getByRole('radio', { name: 'Tout' }));

    const all = await screen.findByRole('table', { name: 'Tous les revenus exceptionnels' });
    await waitFor(() => expect(within(all).getAllByRole('row').slice(1)).toHaveLength(3));
    expect(within(all).getAllByRole('row')[3]).toHaveTextContent('Ancienne prime');
    expect(asked).toContain('0001-01-01');
  });

  it('adds a one-off to an ended source, dated on its end by default and not after it (issue #22)', async () => {
    const { sent } = serve(detail({ ended: true, endedOn: '2025-06-30', oneOffs: [] }));
    const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

    await user.click(await within(await region()).findByRole('button', { name: 'Ajouter un revenu exceptionnel' }));
    const form = screen.getByRole('form', { name: 'Nouveau revenu exceptionnel' });
    expect(within(form).getByLabelText('Date')).toHaveValue('30/06/2025');
    await user.clear(within(form).getByLabelText('Date'));
    await user.type(within(form).getByLabelText('Date'), '2025-07-01');
    await user.type(within(form).getByLabelText('Montant net'), '100');
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));
    expect(await within(form).findByText('La date est après la fin de la source.')).toBeInTheDocument();
    expect(sent).toEqual([]);

    await user.clear(within(form).getByLabelText('Date'));
    await user.type(within(form).getByLabelText('Date'), '2025-06-15');
    await user.click(within(form).getByRole('button', { name: 'Ajouter' }));
    await waitFor(() => expect(sent).toEqual([expect.objectContaining({ method: 'POST', body: { occurredOn: '2025-06-15', amount: { amount: '100', currency: 'EUR' } } })]));
  });

  describe('documents', () => {
    const DOC = '0192f0aa-0000-7000-8000-00000000d0b1';
    const prime = summary({ id: DOC, title: 'Avis de prime', links: [
      link({ targetKind: 'income.one_off', targetId: 'o1', role: 'payslip', label: 'Prime' }),
      link({ targetKind: 'core.item', targetId: SALARY, role: 'payslip', label: 'Salaire Alice' }),
    ] });
    const docs = (sent: Sent[]) => {
      server.use(
        http.get('*/api/v1/documents', () => HttpResponse.json({ items: [prime] })),
        http.post('*/api/v1/documents', async ({ request }) => {
          const form = Object.fromEntries([...(await request.formData()).entries()].filter(([key]) => key !== 'file')) as Record<string, string>;
          sent.push({ method: 'POST', path: new URL(request.url).pathname, form });
          return HttpResponse.json({ id: 'new' }, { status: 201 });
        }),
        http.delete('*/api/v1/documents/:id/links/:kind/:target', ({ request }) => {
          sent.push({ method: 'DELETE', path: new URL(request.url).pathname });
          return new HttpResponse(null, { status: 204 });
        }),
      );
    };

    it('shows a linked document as a paperclip in the note cell', async () => {
      serve();
      docs([]);
      await renderApp(`/income/${SALARY}?tab=oneoffs`);

      expect(await within((await rows())[0]!).findByRole('link', { name: 'Avis de prime' })).toBeInTheDocument();
      expect(within((await rows())[1]!).queryByRole('link')).not.toBeInTheDocument();
    });

    it('keeps the paperclip when the one-off is corrected: its links move to the new id', async () => {
      serve();
      let anchoredOn = 'o1';
      server.use(http.get('*/api/v1/documents', () => HttpResponse.json({ items: [{ ...prime, links: [link({ targetKind: 'income.one_off', targetId: anchoredOn, role: 'payslip', label: 'Prime' })] }] })));
      const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);
      expect(await within((await rows())[0]!).findByRole('link', { name: 'Avis de prime' })).toBeInTheDocument();

      anchoredOn = 'o2'; // the correction gives the one-off a new id; the server moves the link with it
      await choose(user, 0, 'Corriger');
      await user.click(within(screen.getByRole('form', { name: 'Corriger le revenu exceptionnel' })).getByRole('button', { name: 'Enregistrer' }));

      await waitFor(() => expect(toasts()).toHaveTextContent('Revenu exceptionnel corrigé.'));
      expect(await within((await rows())[0]!).findByRole('link', { name: 'Avis de prime' })).toBeInTheDocument();
    });

    it('attaches a document to the one-off with its date and note', async () => {
      serve();
      const sent: Sent[] = [];
      docs(sent);
      const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

      await choose(user, 0, 'Joindre un document');
      await user.upload(within((await rows())[0]!).getByLabelText('Joindre un document'), new File(['%PDF-1.7'], 'scan.pdf', { type: 'application/pdf' }));

      await waitFor(() => expect(sent).toHaveLength(1));
      expect(sent[0]).toMatchObject({ method: 'POST', form: { targetKind: 'income.one_off', targetId: 'o1', date: '2026-03-10', title: 'Prime de projet' } });
    });

    it('removes only the one-off link', async () => {
      serve();
      const sent: Sent[] = [];
      docs(sent);
      const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

      await user.click(await within((await rows())[0]!).findByRole('button', { name: 'Actions du bulletin Avis de prime' }));
      await user.click(await screen.findByRole('menuitem', { name: 'Retirer' }));
      await user.click(await screen.findByRole('button', { name: 'Confirmer' }));

      await waitFor(() => expect(sent).toEqual([{ method: 'DELETE', path: `/api/v1/documents/${DOC}/links/income.one_off/o1` }]));
    });

    it('shows the paperclip without write entries to a read-only visitor', async () => {
      serve(detail({ canWrite: false, oneOffs: [PROJECT] }));
      docs([]);
      const { user } = await renderApp(`/income/${SALARY}?tab=oneoffs`);

      await user.click(await within((await rows())[0]!).findByRole('button', { name: 'Actions du bulletin Avis de prime' }));
      expect((await screen.findAllByRole('menuitem')).map((i) => i.textContent)).toEqual(['Voir', 'Télécharger', 'Voir le document']);
    });
  });
});
