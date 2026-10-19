import type { ReactNode } from 'react';

/**
 * An invitation, not an apology (frontend/screens.md §10): a title, one sentence, and the action. `action` is a link or
 * a button the caller styles with `buttonVariants` (`ui/button.tsx`); several sit side by side.
 */
export function EmptyState({ title, body, action }: { title: string; body: string; action?: ReactNode }) {
  return (
    <section className="mx-auto flex max-w-md flex-col items-center gap-3 rounded-lg border border-dashed px-6 py-12 text-center">
      <h2 className="text-lg font-semibold">{title}</h2>
      {body ? <p className="text-muted-foreground">{body}</p> : null}
      {action ? <div className="mt-2 flex flex-wrap items-center justify-center gap-2">{action}</div> : null}
    </section>
  );
}
