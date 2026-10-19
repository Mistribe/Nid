import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CalendarIcon } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { useFieldLabel, type FieldControl } from './Field';

const DatePanel = lazy(() => import('./DatePanel'));

const ISO = /^(\d{4})-(\d{2})-(\d{2})$/;
// The slashes are optional: `01022025` is `01/02/2025` (iOS has no `/` on its numeric pad).
const FR = /^(\d{2})\/?(\d{2})\/?(\d{4})$/;

const pad = (n: number) => String(n).padStart(2, '0');

/** Builds a calendar date from parts, or null when the day does not exist (31/02). */
function calendarDate(y: number, m: number, d: number): Date | null {
  const date = new Date(y, m - 1, d);
  return date.getFullYear() === y && date.getMonth() === m - 1 && date.getDate() === d ? date : null;
}

/** `max` (AAAA-MM-JJ) greys out the later days of the calendar; typed text is passed through as is and left to the form's own validation. */
export function DateField({ value, onChange, readOnly, max, ...control }: FieldControl & { value: string; onChange: (v: string) => void; readOnly?: boolean; max?: string }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const fieldLabel = useFieldLabel();
  const iso = ISO.exec(value);
  const [, iy = '', im = '', id = ''] = iso ?? [];
  const shown = iso ? `${id}/${im}/${iy}` : value;
  const maxIso = max ? ISO.exec(max) : null;
  const maxDate = maxIso ? calendarDate(+maxIso[1]!, +maxIso[2]!, +maxIso[3]!) ?? undefined : undefined;
  const selected = iso ? calendarDate(+iy, +im, +id) ?? undefined : undefined;

  function type(text: string) {
    const match = FR.exec(text);
    const [, d = '', m = '', y = ''] = match ?? [];
    onChange(match && calendarDate(+y, +m, +d) ? `${y}-${m}-${d}` : text);
  }

  return (
    <div className="flex gap-2">
      <Input placeholder={t('form.date_placeholder')} value={shown}
        onChange={(e) => type(e.target.value)} readOnly={readOnly} {...control} />
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button type="button" variant="outline" size="icon" disabled={readOnly} aria-label={t('form.pick_date', { label: fieldLabel })}><CalendarIcon /></Button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="end">
          <Suspense fallback={null}>
            <DatePanel selected={selected} max={maxDate}
              onSelect={(d) => { onChange(`${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`); setOpen(false); }} />
          </Suspense>
        </PopoverContent>
      </Popover>
    </div>
  );
}
