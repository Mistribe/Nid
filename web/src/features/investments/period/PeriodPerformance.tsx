import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Segmented } from '@/components/form/Segmented';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { ISO_DATE } from '@/lib/amount';
import { Field } from '@/components/form/Field';
import { todayIso } from '@/lib/today';
import { PERFORMANCE } from '@/api/investments';
import { Direction } from '@/components/Direction';
import { presetRange, type Preset } from './periods';
import '@/locales/investments';
import '@/locales/forms';

const PRESETS: Preset[] = ['ytd', 'last12', 'previous_year', 'custom'];
const missing = (text: string) => <span className="text-sm font-normal text-muted-foreground">{text}</span>;

/** The gain of a chosen period: a preset, or two free dates checked (start before end) before anything is sent. */
export function PeriodPerformance({ envelopeId }: { envelopeId: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const today = todayIso();
  const [preset, setPreset] = useState<Preset>('ytd');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [applied, setApplied] = useState<{ from: string; to: string } | null>(null);
  const [problem, setProblem] = useState<'reversed' | 'from' | 'to' | 'from_future' | 'to_future' | null>(null);

  const range = preset === 'custom' ? applied : presetRange(preset, today);
  const answer = $api.useQuery(
    'get', PERFORMANCE,
    { params: { path: { id: envelopeId }, query: { from: range?.from ?? '', to: range?.to ?? '' } } },
    { enabled: range !== null },
  );

  const compute = (ev: FormEvent) => {
    ev.preventDefault();
    setApplied(null);
    if (!ISO_DATE.test(from)) { setProblem('from'); return; }
    if (!ISO_DATE.test(to)) { setProblem('to'); return; }
    if (from > today) { setProblem('from_future'); return; }
    if (to > today) { setProblem('to_future'); return; }
    if (from > to) { setProblem('reversed'); return; }
    setProblem(null);
    setApplied({ from, to });
  };

  const data = range ? answer.data : undefined;

  return (
    <div className="space-y-3">
      <Segmented value={preset} aria-label={t('envelope.period.legend')}
        onValueChange={(next) => { setPreset(next as Preset); setProblem(null); }}
        options={PRESETS.map((p) => ({ value: p, label: t(`envelope.period.${p}`) }))} />
      {preset === 'custom' ? (
        <form aria-label={t('envelope.period.custom')} onSubmit={compute} className="flex flex-wrap items-end gap-3" noValidate>
          <Field label={t('envelope.period.from')} error={problem === 'from' ? t('edit.required') : problem === 'from_future' ? t('field_error.core_date_in_future') : undefined}>
            {(c) => <DateField {...c} max={today} value={from} onChange={setFrom} />}
          </Field>
          <Field label={t('envelope.period.to')} error={problem === 'to' ? t('edit.required') : problem === 'to_future' ? t('field_error.core_date_in_future') : problem === 'reversed' ? t('envelope.period.reversed') : undefined}>
            {(c) => <DateField {...c} max={today} value={to} onChange={setTo} />}
          </Field>
          <Button type="submit" variant="outline">{t('envelope.period.compute')}</Button>
        </form>
      ) : null}
      {range && answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {range && !answer.error && !data ? <div role="status" aria-busy="true" aria-label={t('envelope.period.loading')} className="h-24 shimmer rounded-md border bg-muted" /> : null}
      {data ? (
        <StatLine items={[
          // #376: the value on its line, its date under it.
          data.startValue && data.startAsOf
            ? { label: t('envelope.period.start'), value: <Money value={data.startValue} tabular />, hint: <LocalDate value={data.startAsOf} /> }
            // Opened during the period: the server counts it from 0.
            : data.startValue
              ? { label: t('envelope.period.start'), value: <Money value={data.startValue} tabular />, hint: t('envelope.period.opened') }
              : { label: t('envelope.period.start'), value: missing(t('envelope.period.no_start')) },
          data.endValue && data.endAsOf
            ? { label: t('envelope.period.end'), value: <Money value={data.endValue} tabular />, hint: <LocalDate value={data.endAsOf} /> }
            : { label: t('envelope.period.end'), value: missing(t('envelope.period.no_end')) },
          { label: t('envelope.period.net'), value: data.netContributions ? <Money value={data.netContributions} tabular /> : missing(t('investments.no_value')) },
          { label: t('envelope.period.performance'), value: data.performance ? <Direction amount={data.performance} /> : missing(t('investments.no_value')) },
        ]} />
      ) : null}
    </div>
  );
}
