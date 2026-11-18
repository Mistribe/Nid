import { ChevronDownIcon, ChevronLeftIcon, ChevronRightIcon } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';

/**
 * A year as ‹ 2026 ⌄ ›: the arrows go to the nearest offered year below or above, the year opens a grid of every year with
 * what the page has for it. It stays the same size whatever the number of years. Years are 4-digit strings, compared as such.
 */
export function YearStepper({ label, value, years, onChange }: {
  label: string; value: string; years: readonly { value: string; hint: string }[]; onChange: (year: string) => void;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  // The current year may not be offered yet (a past year while the notices load): its neighbours are still the nearest ones.
  const previous = years.filter((y) => y.value < value).at(-1)?.value;
  const next = years.find((y) => y.value > value)?.value;
  const choose = (year: string) => { setOpen(false); onChange(year); };
  return (
    <div role="group" aria-label={label} className="inline-flex items-center rounded-md border border-input">
      <Button type="button" variant="ghost" size="icon" aria-label={t('year_stepper.previous')} disabled={!previous} onClick={() => onChange(previous!)}>
        <ChevronLeftIcon aria-hidden="true" />
      </Button>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button type="button" variant="ghost" className="gap-1 px-2 tabular-nums" aria-label={t('year_stepper.choose', { year: value })}>
            {value}<ChevronDownIcon aria-hidden="true" className="text-muted-foreground" />
          </Button>
        </PopoverTrigger>
        <PopoverContent align="end" aria-label={label} className="grid w-auto grid-cols-4 gap-1 p-2">
          {years.map((y) => {
            const selected = y.value === value;
            return (
              <Button key={y.value} type="button" variant={selected ? 'default' : 'ghost'} aria-pressed={selected}
                className="h-auto flex-col gap-0 px-3 py-1.5 tabular-nums" onClick={() => choose(y.value)}>
                {y.value}<span className={`text-xs font-normal ${selected ? '' : 'text-muted-foreground'}`}>{y.hint}</span>
              </Button>
            );
          })}
        </PopoverContent>
      </Popover>
      <Button type="button" variant="ghost" size="icon" aria-label={t('year_stepper.next')} disabled={!next} onClick={() => onChange(next!)}>
        <ChevronRightIcon aria-hidden="true" />
      </Button>
    </div>
  );
}
