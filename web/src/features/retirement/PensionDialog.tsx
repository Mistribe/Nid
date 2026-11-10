import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { PROJECTION } from '@/api/dashboard';
import { invalidateIncome, SOURCES } from '@/api/income';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf, messageFor } from '@/components/form/fieldErrors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { toWireAmount } from '@/lib/amount';
import '@/locales/retirement';

/** The first day of the month after a server month: « 2030-06 » is « 2030-07-01 » (a date, not an amount). */
function startsOnAfter(yyyyMm: string) {
  const [year = 0, m = 1] = yyyyMm.split('-').map(Number);
  return m === 12 ? `${year + 1}-01-01` : `${year}-${String(m + 1).padStart(2, '0')}-01`;
}

/** One's own retirement pension as an income source, from the month after the departure. */
export function PensionDialog({ personId, departure, currency, onClose, onDone }: {
  personId: string; departure: string; currency: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const create = $api.useMutation('post', SOURCES);
  const [amount, setAmount] = useState('');
  const [shared, setShared] = useState(true);
  const fields = fieldErrorsOf(create.error);
  const amountError = fields.find((f) => f.path.startsWith('phases'));
  const unplaced = create.error && !(fields.length > 0 && fields.every((f) => f.path.startsWith('phases')));

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
              void queryClient.invalidateQueries({ queryKey: ['get', PROJECTION] });
              done(t('retirement.pension_saved'));
            },
          });
        }}>
          {unplaced ? <ProblemMessage problem={present(toProblem(create.error))} /> : null}
          <Field label={t('retirement.pension_amount')} hint={t('retirement.pension_hint', { currency })} error={amountError ? messageFor(t, amountError) : undefined}>
            {(c) => <Input {...c} inputMode="decimal" autoComplete="off" value={amount} onChange={(e) => setAmount(e.target.value)} />}
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
