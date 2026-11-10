import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { invalidateIncome, SOURCES } from '@/api/income';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { CurrencyField } from '@/components/form/CurrencyField';
import { Field } from '@/components/form/Field';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { toWireAmount } from '@/lib/amount';
import { RETIREMENT } from './api';

/** The first day of the month after a server month: « 2030-06 » is « 2030-07-01 » (a date, not an amount). */
export function startsOnAfter(yyyyMm: string) {
  const [year = 0, m = 1] = yyyyMm.split('-').map(Number);
  return m === 12 ? `${year + 1}-01-01` : `${year}-${String(m + 1).padStart(2, '0')}-01`;
}

/** One's own retirement pension as an income source, from the month after the departure. */
export function PensionDialog({ personId, departure, currency: initialCurrency, onClose, onDone }: {
  personId: string; departure: string; currency: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const create = $api.useMutation('post', SOURCES);
  const [amount, setAmount] = useState('');
  const [currency, setCurrency] = useState(initialCurrency);
  const [shared, setShared] = useState(true);

  return (
    <FormDialog title={t('retirement.enter_pension')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          create.mutate({
            body: {
              displayName: t('retirement.pension_name'), kind: 'pension', currency, visibility: shared ? 'shared' : 'private',
              owners: [{ personId, shareBp: 10000 }],
              phases: [{ label: t('retirement.pension_phase'), startsOn: startsOnAfter(departure), netAmount: { amount: toWireAmount(amount), currency }, every: { count: 1, unit: 'month' } }],
            },
          }, {
            onSuccess: () => {
              void invalidateIncome(queryClient);
              void queryClient.invalidateQueries({ queryKey: ['get', RETIREMENT] });
              done(t('retirement.pension_saved'));
            },
          });
        }}>
          {create.error ? <ProblemMessage problem={present(toProblem(create.error))} /> : null}
          <Field label={t('retirement.pension_amount')} hint={t('retirement.pension_hint')}>
            {(c) => <Input {...c} inputMode="decimal" autoComplete="off" value={amount} onChange={(e) => setAmount(e.target.value)} />}
          </Field>
          <Field label={t('retirement.pension_currency')}>
            {(c) => <CurrencyField {...c} value={currency} onValueChange={setCurrency} />}
          </Field>
          <div className="flex items-center gap-2 text-sm">
            <Checkbox id="pension-shared" checked={shared} onCheckedChange={(v) => setShared(v === true)} />
            <Label htmlFor="pension-shared">{t('retirement.pension_shared')}</Label>
          </div>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={create.isPending}>{t('retirement.pension_save')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
