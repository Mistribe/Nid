import { useId, type ReactNode } from 'react';
import { Card, CardAction, CardContent, CardHeader } from '@/components/ui/card';

/** A titled region of an overview (a chart, a short list): a note after the title, the actions at the end of the header; `level` 3 inside a `Section`. */
export function SectionCard({ title, note, actions, guide, className, level = 2, children }: {
  title: string; note?: ReactNode; actions?: ReactNode; guide?: string; className?: string; level?: 2 | 3; children: ReactNode;
}) {
  const titleId = useId();
  const Heading = level === 3 ? 'h3' : 'h2';
  return (
    <Card role="region" aria-labelledby={titleId} data-guide={guide} className="min-w-0 gap-3 py-4">
      <CardHeader className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <Heading id={titleId} className="mr-auto text-title">{title}</Heading>
        {note ? <span className="text-sm text-muted-foreground">{note}</span> : null}
        {actions ? <CardAction className="static flex flex-wrap items-center gap-2">{actions}</CardAction> : null}
      </CardHeader>
      <CardContent className={className}>{children}</CardContent>
    </Card>
  );
}
