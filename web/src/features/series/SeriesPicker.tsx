import { keepPreviousData } from '@tanstack/react-query';
import { XIcon } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearch } from '@tanstack/react-router';
import { useApi } from '@/api/ApiProvider';
import { SERIES_CATALOGUE, type SeriesPointsItem } from '@/api/series';
import type { RootSearch } from '@/lib/search';
import { ComboboxField } from '@/components/form/ComboboxField';
import { SeriesChip } from '@/components/page/SeriesView';
import { drawable, type SeriesMode } from '@/components/page/seriesRows';
import { seriesLabel, unitLabel } from '@/components/page/seriesFormat';
import { Button } from '@/components/ui/button';
import { kindsMatching } from './kindSearch';
import { blockOf, MAX_SERIES } from './selection';
import '@/locales/series';

/** The chips of the selected series (the legend, each with its remove button) and the catalogue to add one from. */
export function SeriesPicker({ keys, series, mode, focus, onAdd, onRemove }: {
  keys: readonly string[]; series: readonly SeriesPointsItem[]; mode: SeriesMode; focus: boolean;
  onAdd: (key: string) => void; onRemove: (key: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { scope }: RootSearch = useSearch({ strict: false });
  const id = useId();
  const [typed, setTyped] = useState('');
  const [sent, setSent] = useState('');
  useEffect(() => {
    const timer = setTimeout(() => setSent(typed), 300);
    return () => clearTimeout(timer);
  }, [typed]);
  useEffect(() => { if (focus) document.getElementById(id)?.focus(); }, [focus, id]);

  // The kinds are named in French only in the web: the typed text goes as the kind codes it names, beside `q`.
  const kinds = kindsMatching(sent).join(',');
  const catalogue = $api.useQuery('get', SERIES_CATALOGUE,
    { params: { query: { q: sent || undefined, kinds: kinds || undefined, scope: scope === 'me' ? 'me' : 'household' } } },
    { placeholderData: keepPreviousData });
  const items = catalogue.data?.items ?? [];
  const units = series.map((x) => x.unit);
  const options = items.map((item) => {
    const label = seriesLabel(t, item);
    const block = keys.includes(item.key) ? 'already' : blockOf(units, item.unit);
    const reason = block === 'already' ? t('series.already') : block ? t(`series.block.${block}`) : null;
    return reason ? { value: item.key, label: `${label} — ${reason}`, disabled: true } : { value: item.key, label };
  });
  const full = keys.length >= MAX_SERIES;
  return (
    <div className="space-y-2">
      {series.length ? (
        <ul aria-label={t('series.legend')} className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
          {series.map((s, i) => {
            const label = seriesLabel(t, s);
            return (
              <li key={s.key} className="inline-flex items-center gap-1.5">
                <SeriesChip i={i} />{label}
                <span className="text-muted-foreground">{unitLabel(t, s.unit)}</span>
                {drawable(s, mode) ? null : <span className="text-muted-foreground">({t('series.no_base')})</span>}
                <Button type="button" variant="ghost" size="icon" className="size-6" aria-label={t('series.remove', { label })} onClick={() => onRemove(s.key)}>
                  <XIcon />
                </Button>
              </li>
            );
          })}
        </ul>
      ) : null}
      <div className="flex flex-wrap items-center gap-3">
        <div className="w-64 max-w-full">
          <ComboboxField id={id} aria-label={t('series.add_label')} placeholder={t('series.add')} searchPlaceholder={t('series.search')}
            empty={t('series.no_match')} value="" onValueChange={onAdd} options={options} disabled={full}
            onSearch={setTyped} more={catalogue.data && items.length < catalogue.data.total ? t('series.more') : undefined} />
        </div>
        {full ? <span className="text-sm text-muted-foreground">{t('series.max')}</span> : null}
      </div>
    </div>
  );
}
