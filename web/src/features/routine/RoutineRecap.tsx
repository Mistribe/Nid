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
import { Direction } from '../investments/Figures';
import { invalidateInvestments } from '../investments/api';
import { ROUTINE, ROUTINE_UNDO, type RoutineRecap } from './api';

export const UNDO_MS = 10_000;

/** screens.md §5: the server's recap; « Annuler » for 10 seconds voids everything the routine recorded. */
export function RoutineRecapPanel({ recap, onUndone }: { recap: RoutineRecap; onUndone: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [canUndo, setCanUndo] = useState(true);
  const undo = $api.useMutation('post', ROUTINE_UNDO, {
    onSuccess: () => {
      void invalidateInvestments(queryClient);
      // The list of the month: what was voided is due again.
      void queryClient.invalidateQueries({ queryKey: ['get', ROUTINE] });
    },
  });

  useEffect(() => {
    const timer = setTimeout(() => setCanUndo(false), UNDO_MS);
    return () => clearTimeout(timer);
  }, []);

  const facts = recap.lines.map((l) => ({ envelopeId: l.envelope, valuationId: l.valuationId ?? null, flowId: l.flowId ?? null }));

  return (
    <div className="space-y-3">
      <div role="status" className="flex items-center gap-3">
        <span>{t('routine.saved')}</span>
        {canUndo || undo.isPending ? (
          <Button type="button" variant="outline" disabled={undo.isPending}
            onClick={() => undo.mutate({ body: { facts } }, { onSuccess: () => onUndone() })}>
            {t('routine.undo')}
          </Button>
        ) : null}
      </div>
      {undo.error ? <ProblemMessage problem={present(toProblem(undo.error))} /> : null}
      <Table aria-label={t('routine.recap')}>
        <TableHeader>
          <TableRow>
            <TableHead scope="col">{t('routine.col_envelope')}</TableHead>
            <TableHead scope="col" className="text-right">{t('routine.col_value')}</TableHead>
            <TableHead scope="col" className="text-right">{t('routine.col_contributions')}</TableHead>
            <TableHead scope="col" className="text-right">{t('routine.col_gain')}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {recap.lines.map((l) => (
            <TableRow key={l.envelope}>
              <TableHead scope="row" className="font-normal">{l.name}</TableHead>
              <TableCell className="text-right tabular-nums">{l.value ? formatMoney(l.value) : null}</TableCell>
              <TableCell className="text-right tabular-nums">{formatMoney(l.contributions)}</TableCell>
              <TableCell className="text-right">
                {l.gain ? <Direction amount={l.gain} /> : <span className="text-muted-foreground">{t('routine.gain_unknown')}</span>}
                {l.previousAsOf ? <small className="block text-xs text-muted-foreground">{t('routine.since', { date: formatLocalDate(l.previousAsOf) })}</small> : null}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
