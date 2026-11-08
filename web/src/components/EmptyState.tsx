import type { ReactNode } from 'react';
import { tileOf, type Place } from '@/components/nav';
import { ModuleTile } from '@/components/ModuleTile';
import { NestArcs } from '@/components/Logo';

/**
 * An invitation, not an apology (frontend/screens.md §10): the module's tile over the nest, a title, one sentence, and
 * the action. `action` is a link or a button the caller styles with `buttonVariants` (`ui/button.tsx`); several sit side by side.
 */
export function EmptyState({ module, title, body, action, level = 2 }: { module: Place; title: string; body: string; action?: ReactNode; level?: 2 | 3 }) {
  const { icon, tile } = tileOf(module);
  const Heading = `h${level}` as const;
  return (
    <section className="relative isolate mx-auto flex max-w-md flex-col items-center gap-3 overflow-hidden px-6 py-12 text-center">
      <div aria-hidden="true" className="relative mb-6">
        <NestArcs className="absolute -top-6 left-1/2 -z-10 w-32 -translate-x-1/2 text-muted-foreground opacity-20" />
        <ModuleTile icon={icon} tile={tile} size="lg" />
      </div>
      <Heading className="text-title">{title}</Heading>
      {body ? <p className="text-muted-foreground">{body}</p> : null}
      {action ? <div className="mt-2 flex flex-wrap items-center justify-center gap-2">{action}</div> : null}
    </section>
  );
}
