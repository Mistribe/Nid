import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Field } from '../../subscriptions/edits/Field';
import { todayIso } from '../../subscriptions/today';
import { PERFORMANCE } from '../api';
import { Direction } from '../Figures';
import { presetRange, type Preset } from './periods';

const PRESETS: Preset[] = ['ytd', 'last12', 'previous_year', 'custom'];
const input = 'rounded-md border px-3 py-2';
const missing = (text: string) => <span className="text-muted-foreground">{text}</span>;

/** The gain of a chosen period: a preset, or two free dates checked (start before end) before anything is sent. */
export function PeriodPerformance({ envelopeId }: { envelopeId: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const today = todayIso();
  const [preset, setPreset] = useState<Preset>('ytd');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [applied, setApplied] = useState<{ from: string; to: string } | null>(null);
  const [problem, setProblem] = useState<'reversed' | 'from' | 'to' | null>(null);

  const range = preset === 'custom' ? applied : presetRange(preset, today);
  const answer = $api.useQuery(
    'get', PERFORMANCE,
    { params: { path: { id: envelopeId }, query: { from: range?.from ?? '', to: range?.to ?? '' } } },
    { enabled: range !== null },
  );

  const compute = (ev: FormEvent) => {
    ev.preventDefault();
    setApplied(null);
    if (!from) { setProblem('from'); return; }
    if (!to) { setProblem('to'); return; }
    if (from > to) { setProblem('reversed'); return; }
    setProblem(null);
    setApplied({ from, to });
  };

  const data = range ? answer.data : undefined;

  return (
    <div className="space-y-3">
      <fieldset className="flex flex-wrap gap-4">
        <legend className="sr-only">{t('envelope.period.legend')}</legend>
        {PRESETS.map((p) => (
          <label key={p} className="flex items-center gap-2">
            <input type="radio" name={`period-${envelopeId}`} checked={preset === p} onChange={() => { setPreset(p); setProblem(null); }} />
            {t(`envelope.period.${p}`)}
          </label>
        ))}
      </fieldset>
      {preset === 'custom' ? (
        <form aria-label={t('envelope.period.custom')} onSubmit={compute} className="flex flex-wrap items-end gap-3" noValidate>
          <Field label={t('envelope.period.from')} error={problem === 'from' ? t('edit.required') : undefined}>
            {(c) => <input type="date" max={today} {...c} value={from} onChange={(ev) => setFrom(ev.target.value)} className={input} />}
          </Field>
          <Field label={t('envelope.period.to')} error={problem === 'to' ? t('edit.required') : problem === 'reversed' ? t('envelope.period.reversed') : undefined}>
            {(c) => <input type="date" max={today} {...c} value={to} onChange={(ev) => setTo(ev.target.value)} className={input} />}
          </Field>
          <Button type="submit" variant="outline">{t('envelope.period.compute')}</Button>
        </form>
      ) : null}
      {range && answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} /> : null}
      {range && !answer.error && !data ? <div role="status" aria-busy="true" aria-label={t('envelope.period.loading')} className="h-24 animate-pulse rounded-md border bg-muted" /> : null}
      {data ? (
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
          <dt>{t('envelope.period.start')}</dt>
          <dd>
            {data.startValue && data.startAsOf
              ? <><Money value={data.startValue} tabular /> · <LocalDate value={data.startAsOf} /></>
              : missing(t('envelope.period.no_start'))}
          </dd>
          <dt>{t('envelope.period.end')}</dt>
          <dd>
            {data.endValue && data.endAsOf
              ? <><Money value={data.endValue} tabular /> · <LocalDate value={data.endAsOf} /></>
              : missing(t('envelope.period.no_end'))}
          </dd>
          <dt>{t('envelope.period.net')}</dt>
          <dd>{data.netContributions ? <Money value={data.netContributions} tabular /> : missing(t('investments.no_value'))}</dd>
          <dt>{t('envelope.period.performance')}</dt>
          <dd>{data.performance ? <Direction amount={data.performance} /> : missing(t('investments.no_value'))}</dd>
        </dl>
      ) : null}
    </div>
  );
}
