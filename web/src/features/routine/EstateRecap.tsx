import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { invalidateProperties } from '../properties/api';
import { categoryText } from '../properties/format';
import { ESTATE_ROUTINE, ESTATE_ROUTINE_UNDO, type EstateRecap } from './api';
import { UNDO_MS } from './RoutineRecap';

/** Spec 3E2 §3–§4: what was recorded, property by property; « Annuler » for 10 seconds voids exactly those facts. */
export function EstateRecapPanel({ recap, onUndone }: { recap: EstateRecap; onUndone: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [canUndo, setCanUndo] = useState(true);
  const undo = $api.useMutation('post', ESTATE_ROUTINE_UNDO, {
    onSuccess: () => {
      void invalidateProperties(queryClient);
      // The month's lines: what was voided is proposed again.
      void queryClient.invalidateQueries({ queryKey: ['get', ESTATE_ROUTINE] });
    },
  });

  useEffect(() => {
    const timer = setTimeout(() => setCanUndo(false), UNDO_MS);
    return () => clearTimeout(timer);
  }, []);

  const facts = recap.lines.flatMap((l) => [
    ...(l.valuationId ? [{ property: l.property, valuationId: l.valuationId }] : []),
    ...l.flows.map((f) => ({ property: l.property, flowId: f.flowId })),
  ]);

  return (
    <div className="space-y-3">
      <div role="status" className="flex items-center gap-3">
        <span>{t('routine_estate.saved')}</span>
        {canUndo || undo.isPending ? (
          <Button type="button" variant="outline" disabled={undo.isPending}
            onClick={() => undo.mutate({ body: { facts } }, { onSuccess: () => onUndone() })}>
            {t('routine.undo')}
          </Button>
        ) : null}
      </div>
      {undo.error ? <ProblemMessage problem={present(toProblem(undo.error))} /> : null}
      <Table aria-label={t('routine_estate.recap')}>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('routine_estate.col_property')}</TableHead>
            <TableHead scope="col" className="text-right">{t('routine_estate.col_estimate')}</TableHead>
            <TableHead scope="col">{t('routine_estate.col_flows')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {recap.lines.map((l) => (
            <TableRow key={l.property}>
              <TableHead scope="row" className="font-normal">{l.name}</TableHead>
              <TableCell className="text-right tabular-nums">{l.estimate ? formatMoney({ amount: l.estimate, currency: l.currency }) : null}</TableCell>
              <TableCell>
                <ul>
                  {l.flows.map((f) => (
                    <li key={f.flowId}>
                      {t('routine_estate.flow', {
                        category: categoryText(t, f.category), amount: formatMoney({ amount: f.amount, currency: l.currency }), date: formatLocalDate(f.occurredOn),
                      })}
                    </li>
                  ))}
                </ul>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
