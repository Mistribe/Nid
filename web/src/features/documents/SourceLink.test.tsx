import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { SourceLink } from './SourceLink';

async function renderLink(documentId?: string | null) {
  const root = createRootRoute();
  const home = createRoute({ getParentRoute: () => root, path: '/', component: () => <p>row <SourceLink documentId={documentId} /></p> });
  const router = createRouter({ routeTree: root.addChildren([home]), history: createMemoryHistory({ initialEntries: ['/'] }) });
  render(<RouterProvider router={router} />);
  await router.load();
  await screen.findByText('row');
}

describe('SourceLink', () => {
  it('links the document, named « Source du chiffre », showing « Source »', async () => {
    await renderLink('d1');
    const link = screen.getByRole('link', { name: 'Source du chiffre' });
    expect(link).toHaveAttribute('href', '/documents/d1');
    expect(link).toHaveTextContent('Source');
  });

  it.each([null, undefined])('renders nothing for %s', async (id) => {
    await renderLink(id);
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });
});
