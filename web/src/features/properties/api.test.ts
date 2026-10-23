import { QueryClient } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { invalidateProperties, PROPERTY, PROPERTY_RECEIPTS } from './api';

describe('invalidateProperties', () => {
  it('drops the receipts with the detail: a flow or a lease changed changes the receipt', async () => {
    const client = new QueryClient();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await invalidateProperties(client);

    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['get', PROPERTY] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['get', PROPERTY_RECEIPTS] });
  });
});
