import { useId, type ReactNode } from 'react';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';

/** A labelled on/off filter (« show the closed ones »): the label names the switch for assistive technology, the hint describes it; `tip` sits beside the label (an InfoTip). */
export function SwitchField({ label, hint, tip, checked, onCheckedChange }: { label: string; hint?: string; tip?: ReactNode; checked: boolean; onCheckedChange: (checked: boolean) => void }) {
  const id = useId();
  const hintId = useId();
  return (
    <div className="space-y-1">
      <div className="flex items-center gap-2 text-sm">
        <Switch id={id} checked={checked} onCheckedChange={onCheckedChange} aria-describedby={hint ? hintId : undefined} />
        <Label htmlFor={id}>{label}</Label>
        {tip}
      </div>
      {hint ? <p id={hintId} className="text-sm text-muted-foreground">{hint}</p> : null}
    </div>
  );
}
