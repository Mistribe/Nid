import type { ReactNode } from 'react';

/** An invitation, not an apology (frontend/screens.md §10): a title, one sentence, and the action. */
export function EmptyState({ title, body, action }: { title: string; body: string; action?: ReactNode }) {
  return (
    <section className="mx-auto flex max-w-md flex-col items-center gap-3 py-12 text-center">
      <h2 className="text-lg font-semibold">{title}</h2>
      <p className="text-muted-foreground">{body}</p>
      {action}
    </section>
  );
}
