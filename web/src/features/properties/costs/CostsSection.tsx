import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '../../income/Section';
import { money } from '../../loans/format';
import { invalidateProperties, RECURRING_COST, type PropertyDetail, type PropertyRecurringCost } from '../api';
import { categoryText, everyText } from '../format';
import { RowActions } from '@/components/page/RowActions';
import { presentRefusal } from '../../loans/refusal';
import { useRowDialog } from '../useRowDialog';
import { CostDialog } from './CostForm';
import '@/locales/loans';

/**
 * Spec 3D2 §4: « Charges prévues » in the server's order (by start), each with its monthly equivalent and next due date as
 * the server computed them; adding and correcting open a dialog, the section owns its live region and its focus.
 */
export function CostsSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
  const s = useRowDialog(property.recurringCosts, 'cost-menu');
  return (
    <Section title={t('property_costs.title')} headingRef={s.headingRef} announcement={s.announcement}>
      <p className="text-sm text-muted-foreground">{t('property_costs.intro')}</p>
      {writable ? <Button ref={s.addButtonRef} type="button" variant="outline" size="sm" onClick={() => s.open()}>{t('property_costs.add')}</Button> : null}
      {writable && s.dialog ? <CostDialog property={property} editing={s.dialog.editing} onClose={s.cancel} onDone={s.saved} /> : null}
      <Table aria-label={t('property_costs.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('property_costs.category')}</TableHead>
            <TableHead className="text-right">{t('property_costs.amount')}</TableHead>
            <TableHead>{t('property_costs.period')}</TableHead>
            <TableHead>{t('loan_detail.col_start')}</TableHead>
            <TableHead>{t('loan_detail.col_end')}</TableHead>
            <TableHead className="text-right">{t('property_costs.monthly')}</TableHead>
            <TableHead>{t('property_costs.next_due')}</TableHead>
            <TableHead>{t('loan_detail.col_note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('loan_detail.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {property.recurringCosts.length === 0 ? (
            <TableRow><TableCell colSpan={writable ? 9 : 8} className="text-muted-foreground">{t('property_costs.empty')}</TableCell></TableRow>
          ) : property.recurringCosts.map((c) => (
            <TableRow key={c.id}>
              <TableCell>{categoryText(t, c.category)}</TableCell>
              <TableCell className="text-right tabular-nums">{money(property.currency, c.amount)}</TableCell>
              <TableCell>{everyText(t, c.every)}</TableCell>
              <TableCell><LocalDate value={c.startsOn} /></TableCell>
              <TableCell>{c.endsOn ? <LocalDate value={c.endsOn} /> : null}</TableCell>
              <TableCell className="text-right tabular-nums">{money(property.currency, c.monthlyEquivalent)}</TableCell>
              {/* No next due date from the server: the cost has ended. */}
              <TableCell>{c.nextDueOn ? <LocalDate value={c.nextDueOn} /> : t('property_costs.ended')}</TableCell>
              <TableCell className="whitespace-normal">{c.note}</TableCell>
              {writable ? (
                <TableCell>
                  <CostRowActions property={property} cost={c} onEdit={() => s.open(c)} onDeleteStarted={() => s.setAnnouncement('')}
                    onDeleted={s.announce} onReloaded={() => s.headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

/** « Corriger » opens the dialog; « Supprimer » asks first and sends the version read. */
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
  return (
    <RowActions menuId={`cost-menu-${cost.id}`} menuLabel={t('loan_detail.row_menu_what', { date: formatLocalDate(cost.startsOn), what: categoryText(t, cost.category) })}
      items={[{ label: t('loan_detail.correct'), onSelect: onEdit }]} confirmation={t('property_costs.delete_confirm')} remove={remove} onDeleteStarted={onDeleteStarted} onReloaded={onReloaded}
      present={presentRefusal} invalidate={invalidateProperties}
      onConfirm={() => remove.mutate({ params: { path: { id: property.id, costId: cost.id }, query: { version: cost.version } } })} />
  );
}
