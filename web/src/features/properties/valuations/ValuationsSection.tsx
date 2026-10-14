import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ConfirmAction } from '@/components/ConfirmAction';
import { LocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Section } from '../../income/Section';
import { money } from '../../loans/format';
import { presentRefusal } from '../../loans/refusal';
import { Reload } from '../../subscriptions/edits/Reload';
import { invalidateProperties, VALUATION, type PropertyDetail, type PropertyValuation } from '../api';
import { ValuationForm } from './ValuationForm';

const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';

/**
 * 3D1 design §6: the estimates, newest first (the server lists them oldest first), one added at a time; the section owns
 * its form, its live region and where the focus lands.
 */
export function ValuationsSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
  const [adding, setAdding] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const close = (message?: string) => { setAdding(false); if (message) announce(message); else addButtonRef.current?.focus(); };
  const newestFirst = [...property.valuations].reverse();
  const cols = writable ? 4 : 3;

  return (
    <Section title={t('property_valuations.title')} headingRef={headingRef} announcement={announcement}>
      {writable ? (
        <Button ref={addButtonRef} type="button" variant="outline" onClick={() => { setAnnouncement(''); setAdding(true); }}>
          {t('property_valuations.add')}
        </Button>
      ) : null}
      {writable && adding ? <ValuationForm property={property} onDone={close} /> : null}
      <div className="overflow-x-auto">
        <table className="w-full text-sm" aria-label={t('property_valuations.table')}>
          <thead>
            <tr>
              <th scope="col" className={th}>{t('loan_detail.col_date')}</th>
              <th scope="col" className={th}>{t('property_valuations.value')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
              {writable ? <th scope="col" className={th}>{t('loan_detail.col_actions')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {newestFirst.length === 0 ? (
              <tr><td colSpan={cols} className={`${td} text-muted-foreground`}>{t('property_valuations.empty')}</td></tr>
            ) : newestFirst.map((v) => (
              <tr key={v.id} className="border-t">
                <td className={td}><LocalDate value={v.asOf} /></td>
                <td className={num}>{money(property.currency, v.amount)}</td>
                <td className={td}>{v.note}</td>
                {writable ? (
                  <td className={td}>
                    <DeleteValuation property={property} valuation={v} onDeleteStarted={() => setAnnouncement('')}
                      onDeleted={announce} onReloaded={() => headingRef.current?.focus()} />
                  </td>
                ) : null}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Section>
  );
}

/** « Supprimer » asks first; an estimate already gone elsewhere says so, and « Recharger » reads the property again. */
function DeleteValuation({ property, valuation, onDeleteStarted, onDeleted, onReloaded }: {
  property: PropertyDetail; valuation: PropertyValuation; onDeleteStarted: () => void; onDeleted: (message: string) => void; onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', VALUATION, {
    onSuccess: () => { void invalidateProperties(queryClient); onDeleted(t('property_valuations.deleted')); },
  });
  const problem = remove.error ? presentRefusal(t, remove.error) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      <ConfirmAction label={t('loan_detail.delete')} confirmation={t('property_valuations.delete_confirm')} pending={remove.isPending}
        onConfirm={() => { onDeleteStarted(); remove.mutate({ params: { path: { id: property.id, valuationId: valuation.id } } }); }} />
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => { remove.reset(); return invalidateProperties(queryClient).then(onReloaded); }} />
            : null}
        </>
      ) : null}
    </span>
  );
}
