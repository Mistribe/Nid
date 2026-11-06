import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { toFieldAmount } from '../subscriptions/edits/amount';
import { fieldErrorsOf, messageFor } from '../subscriptions/fieldErrors';
import { monthText } from '@/components/LocalDate';
import { ASSET_CLASSES, PROJECTION, PROJECTION_ASSUMPTIONS, YEARLY_CLASSES, projectionYears } from './api';

export type Assumptions = {
  classRates: Record<string, string>; inflation: string; monthlySavings?: string | null; savingsClass: string; freedPaymentsToSavings: boolean;
  /** The household reference currency, the unit of the fixed monthly savings. */
  currency: string;
  /** Yearly rates (percent strings by calendar year) and income growth over inflation. */
  inflationByYear: Record<string, string>; classRatesByYear: Record<string, Record<string, string>>; incomeGrowth: string;
  observedInflation?: { percent: string; to: string } | null;
};

const RATE_PREFIX = 'classRates.';

/** A typed percent as the wire string: a French comma reads as a point, nothing else is touched (the server checks it). */
const wire = (text: string) => text.trim().replace(',', '.');

/** The typed years only, as the wire reads them: an empty year is not sent. */
const typed = (byYear: Record<string, string>) => Object.fromEntries(Object.entries(byYear).filter(([, v]) => v.trim()).map(([y, v]) => [y, wire(v)]));

/** The household's growth rates, inflation and savings (« Modifier » of the Hypothèses section); one PUT replaces the document. */
export function AssumptionsDialog({ current, onClose, onDone }: { current: Assumptions; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const save = $api.useMutation('put', PROJECTION_ASSUMPTIONS);
  const [rates, setRates] = useState<Record<string, string>>(() => Object.fromEntries(Object.entries(current.classRates).map(([k, v]) => [k, toFieldAmount(v)])));
  const [inflation, setInflation] = useState(toFieldAmount(current.inflation));
  const [years] = useState(projectionYears);
  const [inflationYears, setInflationYears] = useState<Record<string, string>>(() => Object.fromEntries(years.map((y) => [y, toFieldAmount(current.inflationByYear[y] ?? '')])));
  const [classYears, setClassYears] = useState<Record<string, Record<string, string>>>(() => Object.fromEntries(YEARLY_CLASSES.map((c) => [c, Object.fromEntries(years.map((y) => [y, toFieldAmount(current.classRatesByYear[c]?.[y] ?? '')]))])));
  const [growth, setGrowth] = useState(toFieldAmount(current.incomeGrowth));
  const [mode, setMode] = useState<'auto' | 'amount'>(current.monthlySavings ? 'amount' : 'auto');
  const [amount, setAmount] = useState(toFieldAmount(current.monthlySavings ?? ''));
  const [savingsClass, setSavingsClass] = useState(current.savingsClass);
  const [freed, setFreed] = useState(current.freedPaymentsToSavings);
  const freedId = useId();
  const modeId = useId();

  const fields = fieldErrorsOf(save.error);
  const errorOf = (path: string) => { const f = fields.find((x) => x.path === path); return f ? messageFor(t, f) : undefined; };
  const placed = (path: string) => path === 'inflation' || path === 'incomeGrowth' || /^(inflationByYear|classRatesByYear\.[a-z_]+)\.\d{4}$/.test(path) || path === 'monthlySavings' || path === 'savingsClass'
    || (path.startsWith(RATE_PREFIX) && (ASSET_CLASSES as readonly string[]).includes(path.slice(RATE_PREFIX.length)));
  const unplaced = save.error && !(fields.length > 0 && fields.every((f) => placed(f.path)));

  return (
    <FormDialog title={t('settings.projection.assumptions_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          save.mutate({
            body: {
              classRates: Object.fromEntries(ASSET_CLASSES.map((c) => [c, wire(rates[c] ?? '')])),
              inflation: wire(inflation),
              inflationByYear: typed(inflationYears),
              classRatesByYear: Object.fromEntries(YEARLY_CLASSES.flatMap((c): [string, Record<string, string>][] => { const v = typed(classYears[c] ?? {}); return Object.keys(v).length > 0 ? [[c, v]] : []; })),
              incomeGrowth: wire(growth) || '0',
              monthlySavings: mode === 'amount' ? wire(amount) : null,
              savingsClass,
              freedPaymentsToSavings: freed,
            },
          }, {
            onSuccess: () => {
              void queryClient.invalidateQueries({ queryKey: ['get', PROJECTION] });
              void queryClient.invalidateQueries({ queryKey: ['get', PROJECTION_ASSUMPTIONS] });
              done(t('settings.projection.assumptions_saved'));
            },
          });
        }}>
          {unplaced ? <ProblemMessage problem={present(toProblem(save.error))} /> : null}
          <fieldset className="space-y-3">
            <legend className="text-sm font-medium">{t('settings.projection.rates')}</legend>
            <div className="grid grid-cols-2 gap-3">
              {ASSET_CLASSES.map((c) => (
                <Field key={c} label={t(`settings.projection.class.${c}`)} error={errorOf(`${RATE_PREFIX}${c}`)}>
                  {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={rates[c] ?? ''} onChange={(e) => setRates((all) => ({ ...all, [c]: e.target.value }))} />}
                </Field>
              ))}
            </div>
          </fieldset>
          <fieldset className="space-y-3">
            <legend className="text-sm font-medium">{t('settings.projection.inflation_years')}</legend>
            <div className="grid grid-cols-3 gap-3">
              {years.map((y) => (
                <Field key={y} label={t('settings.projection.year_label', { name: t('settings.projection.inflation'), year: y })} error={errorOf(`inflationByYear.${y}`)}
                  hint={y === years[0] && current.observedInflation && !inflationYears[y]?.trim() ? t('settings.projection.observed_hint', { percent: current.observedInflation.percent.replace('.', ','), month: monthText(current.observedInflation.to) }) : undefined}>
                  {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" placeholder={y === years[0] && current.observedInflation ? undefined : inflation} value={inflationYears[y] ?? ''} onChange={(e) => setInflationYears((all) => ({ ...all, [y]: e.target.value }))} />}
                </Field>
              ))}
              <Field label={t('settings.projection.long_term')} error={errorOf('inflation')}>
                {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={inflation} onChange={(e) => setInflation(e.target.value)} />}
              </Field>
            </div>
          </fieldset>
          {YEARLY_CLASSES.map((c) => (
            <fieldset key={c} className="space-y-3">
              <legend className="text-sm font-medium">{t('settings.projection.class_years', { name: t(`settings.projection.class.${c}`) })}</legend>
              <div className="grid grid-cols-3 gap-3">
                {years.map((y) => (
                  <Field key={y} label={t('settings.projection.year_label', { name: t(`settings.projection.class.${c}`), year: y })} error={errorOf(`classRatesByYear.${c}.${y}`)}>
                    {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" placeholder={rates[c] ?? ''} value={classYears[c]?.[y] ?? ''}
                      onChange={(e) => setClassYears((all) => ({ ...all, [c]: { ...all[c], [y]: e.target.value } }))} />}
                  </Field>
                ))}
              </div>
            </fieldset>
          ))}
          <div className="max-w-64">
            <Field label={t('settings.projection.income_growth')} error={errorOf('incomeGrowth')}>
              {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={growth} onChange={(e) => setGrowth(e.target.value)} />}
            </Field>
          </div>
          <div className="space-y-1">
            <p id={modeId} className="text-sm">{t('settings.projection.savings_mode')}</p>
            <Segmented aria-labelledby={modeId} value={mode} onValueChange={(v) => setMode(v as 'auto' | 'amount')} options={[
              { value: 'auto', label: t('settings.projection.savings_auto') },
              { value: 'amount', label: t('settings.projection.savings_amount') },
            ]} />
          </div>
          {mode === 'amount' ? (
            <div className="max-w-48">
              <Field label={t('settings.projection.monthly_savings_in', { currency: current.currency })} error={errorOf('monthlySavings')}>
                {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={amount} onChange={(e) => setAmount(e.target.value)} />}
              </Field>
            </div>
          ) : errorOf('monthlySavings') ? <p className="text-sm text-destructive">{errorOf('monthlySavings')}</p> : null}
          <div className="max-w-64">
            <Field label={t('settings.projection.savings_class')} error={errorOf('savingsClass')}>
              {(ctl) => <SelectField {...ctl} value={savingsClass} onValueChange={setSavingsClass}
                options={ASSET_CLASSES.map((c) => ({ value: c, label: t(`settings.projection.class.${c}`) }))} />}
            </Field>
          </div>
          <div className="flex items-center gap-2 text-sm">
            <Checkbox id={freedId} checked={freed} onCheckedChange={(checked) => setFreed(checked === true)} />
            <Label htmlFor={freedId}>{t('settings.projection.freed_label')}</Label>
          </div>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('settings.projection.save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
