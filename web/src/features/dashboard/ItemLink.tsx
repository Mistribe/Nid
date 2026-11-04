import { Link } from '@tanstack/react-router';
import type { LinkProps } from '@tanstack/react-router';
import type { ReactNode } from 'react';

/** The detail route of a dashboard item, by its module. */
export function itemRoute(module: string, id: string): LinkProps {
  const to = module === 'subscriptions' ? '/subscriptions/$id' : module === 'income' ? '/income/$id'
    : module === 'properties' ? '/properties/$id' : module === 'loans' ? '/loans/$id' : '/investments/$id';
  return { to, params: { id } };
}

/** A line of the dashboard to its item's detail; the router keeps Foyer / Moi. Underlined unless `className` says otherwise. */
export function ItemLink({ module, id, className = 'underline', children }: { module: string; id: string; className?: string; children: ReactNode }) {
  return <Link {...itemRoute(module, id)} className={className}>{children}</Link>;
}
