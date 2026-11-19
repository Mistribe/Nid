import { useId, type ReactNode } from 'react';

/** #308: one line of the Connexion tab, like a settings list: title (and its badge) with what it says, its actions at the end. */
export function SettingRow({ id, title, badge, actions, children }: {
  id?: string; title: string; badge?: ReactNode; actions?: ReactNode; children?: ReactNode;
}) {
  const headingId = useId();
  return (
    <section id={id} aria-labelledby={headingId} className="flex flex-wrap items-start justify-between gap-x-6 gap-y-3 py-4">
      <div className="min-w-0 max-w-prose flex-1 basis-64 space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <h2 id={headingId} className="font-medium">{title}</h2>
          {badge}
        </div>
        {children}
      </div>
      {actions ? <div data-section-actions className="flex flex-wrap items-center gap-2">{actions}</div> : null}
    </section>
  );
}
