import { Plus } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableRow } from '@/components/ui/table';
import type { IncomeSourceOverview } from '../api';
import { LastCard } from './LastPayments';

/** Income dashboard §4: the last one-offs received (the server's five, newest first), « + Ajouter » when writable. */
export function LastOneOffs({ items, onAdd }: { items: IncomeSourceOverview['lastOneOffs']; onAdd?: () => void }) {
  const { t } = useTranslation();
  return (
    <LastCard title={t('income_detail.last_one_offs.title')} tab="oneoffs" all={t('income_detail.last_one_offs.all')}
      action={onAdd ? <Button type="button" variant="outline" size="sm" onClick={onAdd}><Plus aria-hidden="true" />{t('income_detail.last_one_offs.add')}</Button> : null}>
      {items.length === 0 ? <p className="text-sm text-muted-foreground">{t('income_detail.last_one_offs.empty')}</p> : (
        <Table aria-label={t('income_detail.last_one_offs.title')}>
          <TableBody>
            {items.map((f) => (
              <TableRow key={f.id}>
                <TableCell><LocalDate value={f.occurredOn} /></TableCell>
                <TableCell className="whitespace-normal">{f.note}</TableCell>
                <TableCell className="text-right"><Money value={f.amount} tabular /></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </LastCard>
  );
}
