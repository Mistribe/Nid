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
import { ASSET_CLASSES, PROJECTION, PROJECTION_ASSUMPTIONS } from './api';

export type Assumptions = {
  classRates: Record<string, string>; inflation: string; monthlySavings?: string | null; savingsClass: string; freedPaymentsToSavings: boolean;
  /** The household reference currency, the unit of the fixed monthly savings. */
  currency: string;
};

const RATE_PREFIX = 'classRates.';

/** A typed percent as the wire string: a French comma reads as a point, nothing else is touched (the server checks it). */
const wire = (text: string) => text.trim().replace(',', '.');

/** The household's growth rates, inflation and savings (« Modifier » of the Hypothèses section); one PUT replaces the document. */
export function AssumptionsDialog({ current, onClose, onDone }: { current: Assumptions; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const save = $api.useMutation('put', PROJECTION_ASSUMPTIONS);
  const [rates, setRates] = useState<Record<string, string>>(() => Object.fromEntries(Object.entries(current.classRates).map(([k, v]) => [k, toFieldAmount(v)])));
  const [inflation, setInflation] = useState(toFieldAmount(current.inflation));
  const [mode, setMode] = useState<'auto' | 'amount'>(current.monthlySavings ? 'amount' : 'auto');
  const [amount, setAmount] = useState(toFieldAmount(current.monthlySavings ?? ''));
  const [savingsClass, setSavingsClass] = useState(current.savingsClass);
  const [freed, setFreed] = useState(current.freedPaymentsToSavings);
  const freedId = useId();
  const modeId = useId();

  const fields = fieldErrorsOf(save.error);
  const errorOf = (path: string) => { const f = fields.find((x) => x.path === path); return f ? messageFor(t, f) : undefined; };
  const placed = (path: string) => path === 'inflation' || path === 'monthlySavings' || path === 'savingsClass'
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
          <div className="max-w-48">
            <Field label={t('settings.projection.inflation')} error={errorOf('inflation')}>
              {(ctl) => <Input {...ctl} inputMode="decimal" autoComplete="off" value={inflation} onChange={(e) => setInflation(e.target.value)} />}
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
