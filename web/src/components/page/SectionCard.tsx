import { useId, type ReactNode } from 'react';
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

/** A titled region of an overview (a chart, a short list): a note after the title, the actions at the end of the header. */
export function SectionCard({ title, note, actions, guide, className, children }: {
  title: string; note?: ReactNode; actions?: ReactNode; guide?: string; className?: string; children: ReactNode;
}) {
  const titleId = useId();
  return (
    <Card role="region" aria-labelledby={titleId} data-guide={guide} className="min-w-0 gap-3 py-4">
      <CardHeader className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <CardTitle id={titleId} className="mr-auto text-base">{title}</CardTitle>
        {note ? <span className="text-sm text-muted-foreground">{note}</span> : null}
        {actions ? <CardAction className="static flex flex-wrap items-center gap-2">{actions}</CardAction> : null}
      </CardHeader>
      <CardContent className={className}>{children}</CardContent>
    </Card>
  );
}
