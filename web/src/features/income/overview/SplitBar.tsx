import { useTranslation } from 'react-i18next';
import { formatMoney } from '@/components/Money';
import { NEUTRAL_COLOR } from '@/components/ProviderIcon';
import { percent } from '@/features/dashboard/format';
import { cn } from '@/lib/utils';
import type { IncomeOverviewShare } from '../api';

const isolable = (s: IncomeOverviewShare) => s.kind !== 'one_off' && s.kind !== 'other' && s.item != null;

/**
 * Where the income comes from, as one bar of the server's shares. `one_off` / `other` shares carry a neutral name and
 * are labelled here; the others isolate their source on click (again to release). An empty split with missing rates
 * says which currency lacks one rather than showing a partial bar.
 */
export function SplitBar({ split, missingRates, selected, onSelect }: {
  split: IncomeOverviewShare[]; missingRates: string[]; selected?: string; onSelect: (source?: string) => void;
}) {
  const { t } = useTranslation();
  if (split.length === 0)
    return missingRates.length > 0
      ? <p role="note" className="text-sm text-muted-foreground">{t('income.overview.split_unavailable', { currencies: missingRates.join(', ') })}</p>
      : null;

  const name = (s: IncomeOverviewShare) =>
    s.kind === 'one_off' ? t('income.overview.one_off') : s.kind === 'other' ? t('income.overview.other') : s.name;
  const label = (s: IncomeOverviewShare) => `${name(s)} ${formatMoney(s.perMonth)} · ${t('income.overview.pct', { value: percent(s.percent) })}`;
  // Colours follow the order of the coloured shares; « other » is the neutral one.
  let rank = 0;
  const colored = split.map((s) => ({ s, color: s.kind === 'other' ? NEUTRAL_COLOR : `var(--chart-${(rank++ % 6) + 1})` }));

  return (
    <div role="group" aria-label={t('income.overview.split')} className="space-y-2">
      <div className="flex h-6 gap-0.5">
        {colored.map(({ s, color }) => {
          const style = { width: `${s.percent}%` };
          // The 24px box is the hit target; the bar itself is the thin coloured line inside it.
          const line = <span aria-hidden="true" className="block h-2.5 w-full group-first:rounded-l-full group-last:rounded-r-full" style={{ background: color }} />;
          return isolable(s)
            ? (
              <button key={s.item} type="button" style={style} aria-label={t('income.overview.isolate', { name: label(s) })} aria-pressed={selected === s.item}
                onClick={() => onSelect(selected === s.item ? undefined : s.item!)}
                className={cn('group flex h-full items-center rounded-sm outline-none focus-visible:ring-[3px] focus-visible:ring-inset focus-visible:ring-ring', selected && selected !== s.item && 'opacity-50')}>
                {line}
              </button>
            )
            : <div key={s.kind} style={style} className="group flex h-full items-center">{line}</div>;
        })}
      </div>
      <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
        {colored.map(({ s, color }) => (
          <li key={s.item ?? s.kind} className="inline-flex items-center gap-1.5">
            <span aria-hidden="true" className="size-2.5 rounded-sm" style={{ background: color }} />
            {label(s)}
          </li>
        ))}
      </ul>
    </div>
  );
}
