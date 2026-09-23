import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';

describe('App', () => {
  it('shows the application name through i18n', async () => {
    await renderApp('/subscriptions');
    expect(screen.getAllByText('Nid').length).toBeGreaterThan(0);
  });
});
