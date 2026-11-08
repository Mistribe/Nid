import { ChevronRightIcon } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';

/**
 * A folded part of the form (precision 13), its fields mounted only while open (RHF keeps their values); it opens itself
 * while a field inside holds an error, so a refusal is never hidden. `open` / `onOpenChange` let the caller drive it.
 */
export function Folded({ label, hasError, open, onOpenChange, children }: {
  label: string; hasError: boolean; open?: boolean; onOpenChange?: (open: boolean) => void; children: ReactNode;
}) {
  const [own, setOwn] = useState(false);
  const shown = (open ?? own) || hasError;
  return (
    <Collapsible open={shown} onOpenChange={onOpenChange ?? setOwn} className="space-y-3">
      <CollapsibleTrigger asChild>
        <Button type="button" variant="ghost" size="sm" className="group -ml-3 h-auto min-h-8 max-w-full py-1.5 text-left whitespace-normal">
          <ChevronRightIcon aria-hidden="true" className="transition-transform group-data-[state=open]:rotate-90" />
          {label}
        </Button>
      </CollapsibleTrigger>
      <CollapsibleContent className="space-y-3">{children}</CollapsibleContent>
    </Collapsible>
  );
}
