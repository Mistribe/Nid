import type { LinkProps } from '@tanstack/react-router';
import { createContext, useContext, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';

export type Crumb = { label: string; to: LinkProps['to']; params?: LinkProps['params']; search?: LinkProps['search'] };
export type Trail = { crumbs?: Crumb[]; current?: string };

type Slot = { owner: string; trail: Trail } | null;
type Setter = { set: (owner: string, trail: Trail) => void; clear: (owner: string) => void };

const TrailContext = createContext<Trail | null>(null);
// Apart from the value, so a page header registering never re-renders from its own registration.
const SetterContext = createContext<Setter | null>(null);

/** Holds the trail of the page on screen; the top bar reads it, `PageHeader` feeds it. */
export function TrailProvider({ children }: { children: ReactNode }) {
  const [slot, setSlot] = useState<Slot>(null);
  const setter = useMemo<Setter>(() => ({
    set: (owner, trail) => setSlot({ owner, trail }),
    clear: (owner) => setSlot((s) => (s?.owner === owner ? null : s)),
  }), []);
  return <SetterContext.Provider value={setter}><TrailContext.Provider value={slot?.trail ?? null}>{children}</TrailContext.Provider></SetterContext.Provider>;
}

export function useTrail(): Trail | null {
  return useContext(TrailContext);
}

/** Registers the page's trail; the last one mounted wins, and leaving clears it only if it still owns it. No-op without a provider. */
export function usePageTrail(trail: Trail): void {
  const setter = useContext(SetterContext);
  const owner = useId();
  const latest = useRef(trail);
  latest.current = trail;
  // Callers rebuild `crumbs` each render: key on content, not identity.
  const key = JSON.stringify([trail.current, trail.crumbs?.map((c) => [c.label, c.to, c.params, c.search])]);
  useLayoutEffect(() => {
    setter?.set(owner, latest.current);
    return () => setter?.clear(owner);
  }, [setter, owner, key]);
}

/** A page's trail without its `PageHeader` (a detail still loading, or refused): the bar keeps its way back. Renders nothing. */
export function PageTrail(trail: Trail) {
  usePageTrail(trail);
  return null;
}
