import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
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
import { invalidateProperties, RECURRING_COST, type PropertyDetail, type PropertyRecurringCost } from '../api';
import { categoryText, everyText } from '../format';
import { CostForm } from './CostForm';

const th = 'px-2 py-1 text-left font-medium';
const td = 'px-2 py-1';
const num = 'px-2 py-1 text-right tabular-nums';

/**
 * Spec 3D2 §4: « Charges prévues » in the server's order (by start), each with its monthly equivalent and next due date as
 * the server computed them, edited in place as the tenancies: the section owns its forms, its live region and its focus.
 */
export function CostsSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [focusId, setFocusId] = useState<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addButtonRef = useRef<HTMLButtonElement>(null);

  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const startAdd = () => { setAnnouncement(''); setEditingId(null); setAdding(true); };
  const closeAdd = (message?: string) => { setAdding(false); if (message) announce(message); else addButtonRef.current?.focus(); };
  const startEdit = (id: string) => { setAnnouncement(''); setAdding(false); setEditingId(id); };
  const closeEdit = (message?: string) => { const was = editingId; setEditingId(null); if (message) announce(message); else setFocusId(was); };
  useEffect(() => {
    if (!focusId) return;
    (document.getElementById(`correct-cost-${focusId}`) ?? headingRef.current)?.focus();
    setFocusId(null);
  }, [focusId]);
  // A correction whose row is gone (deleted elsewhere, then « Recharger ») closes with it, onto the heading.
  useEffect(() => {
    if (editingId && !property.recurringCosts.some((c) => c.id === editingId)) { setEditingId(null); headingRef.current?.focus(); }
  }, [editingId, property.recurringCosts]);

  const cols = writable ? 9 : 8;
  return (
    <Section title={t('property_costs.title')} headingRef={headingRef} announcement={announcement}>
      <p className="text-sm text-muted-foreground">{t('property_costs.intro')}</p>
      {writable ? <Button ref={addButtonRef} type="button" variant="outline" onClick={startAdd}>{t('property_costs.add')}</Button> : null}
      {writable && adding ? <CostForm property={property} onDone={closeAdd} /> : null}
      <div className="overflow-x-auto">
        <table className="w-full text-sm" aria-label={t('property_costs.table')}>
          <thead>
            <tr>
              <th scope="col" className={th}>{t('property_costs.category')}</th>
              <th scope="col" className={th}>{t('property_costs.amount')}</th>
              <th scope="col" className={th}>{t('property_costs.period')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_start')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_end')}</th>
              <th scope="col" className={th}>{t('property_costs.monthly')}</th>
              <th scope="col" className={th}>{t('property_costs.next_due')}</th>
              <th scope="col" className={th}>{t('loan_detail.col_note')}</th>
              {writable ? <th scope="col" className={th}>{t('loan_detail.col_actions')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {property.recurringCosts.length === 0 ? (
              <tr><td colSpan={cols} className={`${td} text-muted-foreground`}>{t('property_costs.empty')}</td></tr>
            ) : property.recurringCosts.map((c) => writable && editingId === c.id ? (
              <tr key={c.id}><td colSpan={cols} className={td}><CostForm property={property} editing={c} onDone={closeEdit} /></td></tr>
            ) : (
              <tr key={c.id} className="border-t">
                <td className={td}>{categoryText(t, c.category)}</td>
                <td className={num}>{money(property.currency, c.amount)}</td>
                <td className={td}>{everyText(t, c.every)}</td>
                <td className={td}><LocalDate value={c.startsOn} /></td>
                <td className={td}>{c.endsOn ? <LocalDate value={c.endsOn} /> : null}</td>
                <td className={num}>{money(property.currency, c.monthlyEquivalent)}</td>
                {/* No next due date from the server: the cost has ended. */}
                <td className={td}>{c.nextDueOn ? <LocalDate value={c.nextDueOn} /> : t('property_costs.ended')}</td>
                <td className={td}>{c.note}</td>
                {writable ? (
                  <td className={td}>
                    <CostRowActions property={property} cost={c} onEdit={() => startEdit(c.id)} onDeleteStarted={() => setAnnouncement('')}
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

/** « Corriger » swaps the row for its form; « Supprimer » asks first and sends the version read. */
function CostRowActions({ property, cost, onEdit, onDeleteStarted, onDeleted, onReloaded }: {
  property: PropertyDetail; cost: PropertyRecurringCost; onEdit: () => void; onDeleteStarted: () => void; onDeleted: (message: string) => void;
  onReloaded: () => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', RECURRING_COST, {
    onSuccess: () => { void invalidateProperties(queryClient); onDeleted(t('property_costs.deleted')); },
  });
  const problem = remove.error ? presentRefusal(t, remove.error) : null;

  return (
    <span className="flex flex-wrap items-center gap-2">
      <Button id={`correct-cost-${cost.id}`} type="button" variant="outline" size="sm" onClick={onEdit}>{t('loan_detail.correct')}</Button>
      <ConfirmAction label={t('loan_detail.delete')} confirmation={t('property_costs.delete_confirm')} pending={remove.isPending}
        onConfirm={() => {
          onDeleteStarted();
          remove.mutate({ params: { path: { id: property.id, costId: cost.id }, query: { version: cost.version } } });
        }} />
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
