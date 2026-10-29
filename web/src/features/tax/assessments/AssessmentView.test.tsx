import { QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { ApiContext } from '@/api/ApiProvider';
import { createApi } from '@/api/client';
import type { components } from '@/api/schema';
import { createQueryClient } from '@/app/bootstrap';
import { server } from '@/test/server';
import { AssessmentView } from './AssessmentView';
import type { TaxAssessment } from './api';
import { ALICE, BOB, DOCUMENT, jointAssessment, soloAssessment } from './testData';

type Person = components['schemas']['PersonDto'];
const persons: Person[] = [
  { id: ALICE, kind: 'adult', displayName: 'Alice', email: null, retirementAge: 64 }, { id: BOB, kind: 'adult', displayName: 'Bob', email: null, retirementAge: 64 },
];

/** The view on a bare router (it links) with the API and the persons it reads. */
async function show(assessment: TaxAssessment) {
  server.use(http.get('*/api/v1/core/persons', () => HttpResponse.json(persons)));
  const api = createApi({ getCsrf: () => 'csrf', onUnauthenticated: vi.fn() });
  const root = createRootRoute({ component: () => <AssessmentView assessment={assessment} persons={persons} /> });
  const documents = createRoute({ getParentRoute: () => root, path: '/documents/$id' });
  const router = createRouter({ routeTree: root.addChildren([documents]), history: createMemoryHistory({ initialEntries: ['/'] }) });
  render(
    <QueryClientProvider client={createQueryClient()}>
      <ApiContext.Provider value={api}><RouterProvider router={router} /></ApiContext.Provider>
    </QueryClientProvider>,
  );
  await router.load();
  return screen.findByRole('region', { name: `Avis ${assessment.year}` });
}

const figure = (label: string) => screen.getByText(label, { selector: 'dt' }).nextElementSibling;

describe('AssessmentView', () => {
  it('Shows_the_figures_of_a_joint_notice', async () => {
    await show(jointAssessment());
    expect(screen.getByText('établi en 2026')).toBeInTheDocument();
    expect(figure('Nombre de parts')).toHaveTextContent('3');
    expect(figure('Revenu fiscal de référence')).toHaveTextContent('72 800,00 €');
    expect(figure('Impôt net')).toHaveTextContent('2 688,00 €');
    expect(figure('Solde')).toHaveTextContent('312,00 € remboursé');
    const table = screen.getByRole('table', { name: 'Chiffres de l’avis 2025' });
    expect(within(table).getByRole('columnheader', { name: /Déclarant 1\s*Alice/ })).toBeInTheDocument();
    expect(within(table).getByRole('columnheader', { name: /Déclarant 2\s*Bob/ })).toBeInTheDocument();
    expect(within(table).getByRole('row', { name: /Déduction \(10 % \/ Frais réels\)/ })).toHaveTextContent('10 %Frais réels');
    expect(within(table).getByRole('row', { name: /Impôt sur le barème/ })).toHaveTextContent('3 200,00 €');
    expect(within(table).getByRole('row', { name: /Taux marginal/ })).toHaveTextContent('11 %');
  });

  it('Shows_the_detail_lines_under_the_notice_s_own_names', async () => {
    await show(jointAssessment());
    const table = screen.getByRole('table');
    expect(within(table).getByRole('row', { name: /^Salaires 52/ })).toHaveTextContent('52 000,00 €30 000,00 €');
    expect(within(table).getByRole('row', { name: /Heures supplémentaires et jours RTT/ })).toHaveTextContent('—1 250,00 €');
    expect(within(table).getByRole('row', { name: /RAS sur salaires et pensions/ })).toHaveTextContent('—6 100,00 €');
    expect(within(table).getByRole('row', { name: /Avance de 60 %/ })).toHaveTextContent('—1 250,00 €');
    expect(within(table).getByRole('row', { name: /Frais de garde des enfants de moins de 6 ans/ })).toHaveTextContent('1 800,00 €');
    expect(within(table).getByRole('row', { name: /Retenue à la source prélevée/ })).toHaveTextContent('3 000,00 €');
  });

  it('Shows_a_single_declarant_without_an_empty_column', async () => {
    await show(soloAssessment());
    const table = screen.getByRole('table');
    expect(within(table).getAllByRole('columnheader', { name: /Déclarant/ })).toHaveLength(1);
    expect(figure('Solde')).toHaveTextContent('150,00 € à payer');
    expect(within(table).queryByText(/Déclarant 2/)).not.toBeInTheDocument();
  });

  it('Leaves_out_rows_the_notice_does_not_print', async () => {
    await show(jointAssessment());
    const table = screen.getByRole('table');
    expect(within(table).queryByRole('row', { name: /Plafond calculé sur les revenus/ })).not.toBeInTheDocument();
    expect(within(table).queryByRole('row', { name: /Plafond non utilisé \(le suivant\)/ })).not.toBeInTheDocument();
    expect(within(table).queryByText('Reports')).not.toBeInTheDocument();
    expect(within(table).getByRole('row', { name: /Plafond non utilisé \(le plus ancien\)/ })).toHaveTextContent('1 000,00 €—');
  });

  it('Links_the_source_document', async () => {
    const view = await show(jointAssessment());
    expect(within(view).getByRole('link', { name: /Source/ })).toHaveAttribute('href', `/documents/${DOCUMENT}`);
  });

  it('Has_no_source_link_without_a_document', async () => {
    const view = await show(soloAssessment());
    expect(within(view).queryByRole('link')).not.toBeInTheDocument();
  });

  it('Deletes_after_confirmation', async () => {
    const deletes: string[] = [];
    server.use(http.delete('*/api/v1/tax/assessments/:id', ({ request }) => { deletes.push(new URL(request.url).pathname); return new HttpResponse(null, { status: 204 }); }));
    const a = jointAssessment();
    const view = await show(a);
    const user = userEvent.setup();
    await user.click(within(view).getByRole('button', { name: 'Actions de l’avis 2025' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Supprimer' }));
    expect(deletes).toEqual([]);
    await user.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Confirmer' }));
    await waitFor(() => expect(deletes).toEqual([`/api/v1/tax/assessments/${a.id}`]));
  });
});
