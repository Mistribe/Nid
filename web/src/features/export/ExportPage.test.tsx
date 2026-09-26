import { screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { expectNoAxeViolations } from '@/test/axe';
import { fakeExportServer } from '@/test/exportServer';
import { renderApp } from '@/test/renderApp';
import { server } from '@/test/server';
import { downloadBlob } from './download';

vi.mock('./download', () => ({ downloadBlob: vi.fn() }));

const pages = () => ({
  persons: [[{ ref: 'adult-1' }, { ref: 'adult-2' }]],
  providers: [[]],
  subscriptions: [[{ id: 's1' }, { id: 's2' }], [{ id: 's3' }]],
  billing_phases: [[]],
  purchases: [[]],
});

/** A page the test releases by hand, to look at the screen while it runs. */
function hold() {
  let release = () => {};
  const held = new Promise<void>((resolve) => { release = resolve; });
  return { held, release: () => release() };
}

describe('Export', () => {
  beforeEach(() => vi.mocked(downloadBlob).mockClear());

  it('says what the file will hold and whose items it leaves out', async () => {
    server.use(...fakeExportServer(pages()).handlers);
    const { container } = await renderApp('/export');

    expect(await screen.findByRole('heading', { level: 1, name: 'Export' })).toBeInTheDocument();
    const contents = await screen.findByRole('list', { name: "Contenu de l'export" });
    expect(within(contents).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Personnes : 2', 'Abonnements : 3']);
    expect(screen.getByText(/Les éléments privés de l'autre adulte n'y sont pas/)).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  // Review Focus 3: a future module's section keeps its own name.
  it('shows a section it has no label for under its own name', async () => {
    server.use(...fakeExportServer({ ...pages(), envelopes: [[{ id: 'e1' }]] }).handlers);
    await renderApp('/export');

    expect(await screen.findByText('envelopes : 1')).toBeInTheDocument();
  });

  it('exports, downloads one file, and says how many elements it holds', async () => {
    server.use(...fakeExportServer(pages()).handlers);
    const { container, user } = await renderApp('/export');

    await user.click(await screen.findByRole('button', { name: 'Exporter' }));

    expect(await screen.findByText('Export terminé : 5 éléments.')).toBeInTheDocument();
    expect(downloadBlob).toHaveBeenCalledTimes(1);
    expect(vi.mocked(downloadBlob).mock.calls[0]?.[1]).toMatch(/^nid-\d{4}-\d{2}-\d{2}\.nid\.json$/);
    expect(screen.getByRole('button', { name: 'Exporter de nouveau' })).toBeInTheDocument();
    await expectNoAxeViolations(container);
  });

  // Review Focus 1: the page that arrives after a cancel changes nothing.
  it('shows the progress, and a cancel returns to rest even if a page arrives afterwards', async () => {
    const fake = fakeExportServer(pages());
    const gate = hold();
    fake.state.onPage = (section, page) => (section === 'subscriptions' && page === 1 ? gate.held : undefined);
    server.use(...fake.handlers);
    const { container, user } = await renderApp('/export');

    await user.click(await screen.findByRole('button', { name: 'Exporter' }));
    const bar = await screen.findByRole('progressbar', { name: "Progression de l'export" });
    await waitFor(() => expect(bar).toHaveAttribute('aria-valuenow', '4'));
    expect(bar).toHaveAttribute('aria-valuemax', '5');
    expect(screen.getByText('Reçus : 4 sur 5')).toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Annuler' }));
    gate.release();

    expect(await screen.findByRole('button', { name: 'Exporter' })).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(screen.queryByText(/Export terminé/)).not.toBeInTheDocument();
    expect(downloadBlob).not.toHaveBeenCalled();
  });

  // Review Focus 2: leaving the screen mid-export never downloads afterwards.
  it('downloads nothing when the screen is left mid-export', async () => {
    const fake = fakeExportServer(pages());
    const gate = hold();
    fake.state.onPage = (section, page) => (section === 'subscriptions' && page === 1 ? gate.held : undefined);
    server.use(...fake.handlers);
    const { user, unmount } = await renderApp('/export');

    await user.click(await screen.findByRole('button', { name: 'Exporter' }));
    await screen.findByRole('progressbar');
    unmount();
    gate.release();

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(downloadBlob).not.toHaveBeenCalled();
  });

  it('downloads nothing when the data changed during the export, and offers to start again', async () => {
    const fake = fakeExportServer(pages());
    let moved = false;
    fake.state.onPage = (section) => { if (section === 'purchases' && !moved) { moved = true; fake.state.revision = 'r2'; } };
    server.use(...fake.handlers);
    const { container, user } = await renderApp('/export');

    await user.click(await screen.findByRole('button', { name: 'Exporter' }));

    expect(await screen.findByRole('alert')).toHaveTextContent("Vos données ont changé pendant l'export : rien n'a été téléchargé.");
    expect(downloadBlob).not.toHaveBeenCalled();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Relancer' }));
    expect(await screen.findByText('Export terminé : 5 éléments.')).toBeInTheDocument();
    expect(downloadBlob).toHaveBeenCalledTimes(1);
  });

  it('shows a failure and offers to try again', async () => {
    const fake = fakeExportServer(pages());
    fake.state.failWith = { section: 'subscriptions', page: 0, status: 500 };
    server.use(...fake.handlers);
    const { container, user } = await renderApp('/export');

    await user.click(await screen.findByRole('button', { name: 'Exporter' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Une erreur inattendue est survenue.');
    expect(downloadBlob).not.toHaveBeenCalled();
    await expectNoAxeViolations(container);

    fake.state.failWith = null;
    await user.click(screen.getByRole('button', { name: 'Réessayer' }));
    expect(await screen.findByText('Export terminé : 5 éléments.')).toBeInTheDocument();
  });

  it('exports an empty file when there is nothing to export', async () => {
    server.use(...fakeExportServer({ persons: [[]], subscriptions: [[]] }).handlers);
    const { user } = await renderApp('/export');

    expect(await screen.findByText("Rien à exporter pour l'instant : le fichier sera vide.")).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Exporter' }));
    expect(await screen.findByText('Export terminé : 0 élément.')).toBeInTheDocument();
  });
});
