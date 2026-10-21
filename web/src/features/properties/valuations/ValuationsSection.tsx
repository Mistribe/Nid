import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Section } from '../../income/Section';
import { money } from '../../loans/format';
import { invalidateProperties, VALUATION, type PropertyDetail, type PropertyValuation } from '../api';
import { RowActions } from '@/components/page/RowActions';
import { presentRefusal } from '../../loans/refusal';
import '@/locales/loans';
import '@/locales/properties';

/**
 * 3D1 design §6: the estimates, newest first (the server lists them oldest first). Adding is the page header's action (its
 * dialog and announcement are the page's); the section owns the deletion's live region and where the focus lands.
 */
export function ValuationsSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const writable = property.canWrite;
  const [announcement, setAnnouncement] = useState('');
  const headingRef = useRef<HTMLHeadingElement>(null);
  const announce = (message: string) => { setAnnouncement(message); headingRef.current?.focus(); };
  const newestFirst = [...property.valuations].reverse();

  return (
    <Section title={t('property_valuations.title')} headingRef={headingRef} announcement={announcement}>
      <Table aria-label={t('property_valuations.table')}>
        <TableHeader>
          <TableRow>
            <TableHead>{t('loan_detail.col_date')}</TableHead>
            <TableHead className="text-right">{t('property_valuations.value')}</TableHead>
            <TableHead>{t('loan_detail.col_note')}</TableHead>
            {writable ? <TableHead className="w-0"><span className="sr-only">{t('loan_detail.col_actions')}</span></TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {newestFirst.length === 0 ? (
            <TableRow><TableCell colSpan={writable ? 4 : 3} className="text-muted-foreground">{t('property_valuations.empty')}</TableCell></TableRow>
          ) : newestFirst.map((v) => (
            <TableRow key={v.id}>
              <TableCell><LocalDate value={v.asOf} /></TableCell>
              <TableCell className="text-right tabular-nums">{money(property.currency, v.amount)}</TableCell>
              <TableCell className="whitespace-normal">{v.note}</TableCell>
              {writable ? (
                <TableCell>
                  <DeleteValuation property={property} valuation={v} onDeleteStarted={() => setAnnouncement('')}
                    onDeleted={announce} onReloaded={() => headingRef.current?.focus()} />
                </TableCell>
              ) : null}
            </TableRow>
          ))}
        </TableBody>
      </Table>
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
  return (
    <RowActions menuId={`delete-valuation-${valuation.id}`} menuLabel={t('loan_detail.row_menu_what', { date: formatLocalDate(valuation.asOf), what: money(property.currency, valuation.amount) })}
      items={[]} confirmation={t('property_valuations.delete_confirm')} remove={remove} onDeleteStarted={onDeleteStarted} onReloaded={onReloaded}
      present={presentRefusal} invalidate={invalidateProperties}
      onConfirm={() => remove.mutate({ params: { path: { id: property.id, valuationId: valuation.id } } })} />
  );
}
