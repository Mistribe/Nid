import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { CANDIDATES, type BankTransaction, type ExpectedCharge } from '@/api/banking';
import { useLineActions } from './api';
import '@/locales/banks';

/** « 2026-10-27 » as « 27/10/2026 » (a string swap, nothing computed). */
const dmy = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)}`;

/** Bank sync spec §6.5: the expected charges close to a line, as the server ranks them; the picked one becomes the line's link. */
export default function LinkDialog({ line, onClose, onDone }: { line: BankTransaction; onClose: () => void; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { link } = useLineActions();
  const candidates = $api.useQuery('get', CANDIDATES, { params: { path: { id: line.id } } });
  const [picked, setPicked] = useState('');
  const charges = candidates.data ?? [];
  // A charge has no id of its own: its target is its identity, so a reordered refetch keeps the pick.
  const keyOf = (c: ExpectedCharge) => [c.module, c.itemId, c.key].join('|');
  const options = charges.map((c) => ({
    value: keyOf(c),
    label: [c.itemName, dmy(c.dueOn), formatCurrency(c.amount, c.currency), c.gap != null ? t('banks.link.gap', { gap: formatCurrency(c.gap, c.currency) }) : null].filter(Boolean).join(' · '),
  }));
  const chosen = charges.find((c) => keyOf(c) === picked);
  const problem = candidates.error ?? link.error;

  return (
    <FormDialog title={t('banks.link.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          if (chosen) link.mutate({ params: { path: { id: line.id } }, body: { module: chosen.module, itemId: chosen.itemId, key: chosen.key } }, { onSuccess: () => done(t('banks.operations.done_link')) });
        }}>
          <p className="text-sm text-muted-foreground">{line.label} · {formatCurrency(line.amount, line.currency)}</p>
          {problem ? <ProblemMessage problem={present(toProblem(problem))} /> : null}
          {candidates.data && charges.length === 0 ? <p className="text-muted-foreground">{t('banks.link.empty')}</p> : null}
          {charges.length > 0 ? (
            <Field label={t('banks.link.target')}>
              {(c) => <ComboboxField {...c} value={picked} onValueChange={setPicked} options={options} placeholder={t('banks.link.pick')} searchPlaceholder={t('banks.link.search')} empty={t('banks.link.none')} />}
            </Field>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={!chosen || link.isPending}>{t('banks.link.submit')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
