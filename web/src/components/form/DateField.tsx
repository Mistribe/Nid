import { lazy, Suspense, useState, type ClipboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { CalendarIcon } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { parsePastedDate } from '@/lib/date-paste';
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

/** `min` and `max` (AAAA-MM-JJ) grey out the earlier and the later days of the calendar; typed text is passed through as is and left to the form's own validation. */
export function DateField({ value, onChange, readOnly, min, max, ...control }: FieldControl & { value: string; onChange: (v: string) => void; readOnly?: boolean; min?: string; max?: string }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const fieldLabel = useFieldLabel();
  const iso = ISO.exec(value);
  const [, iy = '', im = '', id = ''] = iso ?? [];
  const shown = iso ? `${id}/${im}/${iy}` : value;
  const maxIso = max ? ISO.exec(max) : null;
  const minIso = min ? ISO.exec(min) : null;
  const minDate = minIso ? calendarDate(+minIso[1]!, +minIso[2]!, +minIso[3]!) ?? undefined : undefined;
  const maxDate = maxIso ? calendarDate(+maxIso[1]!, +maxIso[2]!, +maxIso[3]!) ?? undefined : undefined;
  const selected = iso ? calendarDate(+iy, +im, +id) ?? undefined : undefined;

  function type(text: string) {
    const match = FR.exec(text);
    const [, d = '', m = '', y = ''] = match ?? [];
    onChange(match && calendarDate(+y, +m, +d) ? `${y}-${m}-${d}` : text);
  }

  // A pasted date may come in any shape (2026-10-18, 18.10.26, 18 octobre 2026); typing stays strict so a
  // half-typed 18/10/20 is never read as 2020. Unreadable text is left to the browser.
  function paste(e: ClipboardEvent<HTMLInputElement>) {
    if (readOnly) return;
    const pasted = parsePastedDate(e.clipboardData.getData('text/plain'));
    if (!pasted) return;
    e.preventDefault();
    onChange(pasted);
  }

  return (
    <div className="flex gap-2">
      <Input placeholder={t('form.date_placeholder')} value={shown}
        onChange={(e) => type(e.target.value)} onPaste={paste} readOnly={readOnly} {...control} />
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button type="button" variant="outline" size="icon" disabled={readOnly} aria-label={t('form.pick_date', { label: fieldLabel })}><CalendarIcon /></Button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="end">
          <Suspense fallback={null}>
            <DatePanel selected={selected} min={minDate} max={maxDate}
              onSelect={(d) => { onChange(`${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`); setOpen(false); }} />
          </Suspense>
        </PopoverContent>
      </Popover>
    </div>
  );
}
