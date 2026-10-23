import { Link } from '@tanstack/react-router';
import type { ReactNode } from 'react';

/** A line of the dashboard to its item's detail; the router keeps Foyer / Moi. Underlined unless `className` says otherwise. */
export function ItemLink({ module, id, className = 'underline', children }: { module: string; id: string; className?: string; children: ReactNode }) {
  const to = module === 'subscriptions' ? '/subscriptions/$id' : module === 'income' ? '/income/$id'
    : module === 'properties' ? '/properties/$id' : module === 'loans' ? '/loans/$id' : '/investments/$id';
  return <Link to={to} params={{ id }} className={className}>{children}</Link>;
}
