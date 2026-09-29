import { screen, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import type { EnvelopeDetail } from './api';
import { detail, eur, figures, PEA } from './testData';

const serve = (body: EnvelopeDetail) => server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json(body)));

describe('EnvelopeDetailPage', () => {
  it('shows the figures, before tax, and the two tables newest first', async () => {
    serve(detail());
    const { container } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByRole('heading', { name: 'PEA', level: 1 })).toBeInTheDocument();
    const figuresBlock = screen.getByRole('region', { name: 'Valeur et gain' });
    expect(figuresBlock).toHaveTextContent('25 000,00 €');
    expect(figuresBlock).toHaveTextContent('22 000,00 €');
    expect(figuresBlock).toHaveTextContent('+3 000,00 €');
    expect(figuresBlock).toHaveTextContent('avant fiscalité');
    const values = within(screen.getByRole('table', { name: 'Valeurs de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(values[0]).toHaveTextContent('30 septembre 2025');
    const flows = within(screen.getByRole('table', { name: 'Flux de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(flows[0]).toHaveTextContent('Versement');
    expect(flows[1]).toHaveTextContent('Versements antérieurs');
    await expectNoAxeViolations(container);
  });

  it('warns when the opening may be missing', async () => {
    serve(detail({ figures: figures({ missingOpening: true }), flows: [] }));
    await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Le gain comprend peut-être des versements faits avant le début du suivi.')).toBeInTheDocument();
  });

  it('shows what was paid in or out since the last value, and fees and income only when there are any', async () => {
    serve(detail({ figures: figures({ contributionsSince: eur('700.0000'), withdrawalsSince: eur('50.0000'), fees: eur('120.0000'), gainAfterFees: eur('2880.0000') }) }));
    await renderApp(`/investments/${PEA}`);

    const block = await screen.findByRole('region', { name: 'Valeur et gain' });
    expect(block).toHaveTextContent('700,00 € versés, 50,00 € retirés');
    expect(block).toHaveTextContent('120,00 €');
    expect(block).not.toHaveTextContent('Revenus perçus');
  });

  it('labels a transfer leg as a transfer', async () => {
    serve(detail({ flows: [{ id: 'f9', occurredOn: '2025-06-15', kind: 'withdrawal', amount: eur('100.0000'), category: 'transfer', externalRef: 'transfer:x', note: null }] }));
    await renderApp(`/investments/${PEA}`);

    const flows = within(await screen.findByRole('table', { name: 'Flux de l’enveloppe' })).getAllByRole('row').slice(1);
    expect(flows[0]).toHaveTextContent('Virement');
  });

  it('gives the other adult a read-only page without any form, action or Actions column', async () => {
    serve(detail({ canWrite: false }));
    const { container } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Vous pouvez consulter cette enveloppe, pas la modifier.')).toBeInTheDocument();
    const article = screen.getByRole('article');
    expect(within(article).queryByRole('button')).not.toBeInTheDocument();
    expect(within(article).queryByRole('textbox')).not.toBeInTheDocument();
    expect(within(article).queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('shows a placeholder row in each table when there are no values or flows yet', async () => {
    serve(detail({ valuations: [], flows: [] }));
    const { container } = await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Aucune valeur pour l’instant')).toBeInTheDocument();
    expect(screen.getByText('Aucun flux pour l’instant')).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  it('says an unknown envelope is not found', async () => {
    server.use(http.get('*/api/v1/investments/envelopes/:id', () => HttpResponse.json({ status: 404, code: null, title: 'Not found' }, { status: 404 })));
    await renderApp(`/investments/${PEA}`);

    expect(await screen.findByText('Cet élément est introuvable.')).toBeInTheDocument();
  });
});
