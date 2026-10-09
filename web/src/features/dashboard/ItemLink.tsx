import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';

/** A line of the dashboard to its item's detail; the router keeps Foyer / Moi. */
export function ItemLink({ module, id, children }: { module: string; id: string; children: ReactNode }) {
  const to = module === 'subscriptions' ? '/subscriptions/$id' : module === 'income' ? '/income/$id' : '/investments/$id';
  return <Link to={to} params={{ id }} className="underline">{children}</Link>;
}
