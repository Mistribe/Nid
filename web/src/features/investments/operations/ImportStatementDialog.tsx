import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Problem } from '@/api/errors';
import { FormDialog } from '@/components/FormDialog';
import { Field } from '@/components/form/Field';
import { formatLocalDate } from '@/components/LocalDate';
import { formatCurrency } from '@/components/Money';
import { SwitchField } from '@/components/page/SwitchField';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ACCOUNTS, CONNECTIONS } from '@/features/settings/banks/api';
import { invalidateInvestments } from '../api';
import { importStatement, statementErrorText, TRANSACTIONS, type StatementImport } from './api';
import '@/locales/banks';

const ACCEPT = '.ofx,.qfx,.xml,application/x-ofx,application/xml,text/xml';
const PROPOSALS = '/api/v1/core/proposals' as const;

/**
 * Bank sync spec §5.2: a statement file read once to preview what it holds (nothing written), then sent again with « Importer ».
 * Dropped on an envelope (`envelopeId`), its account becomes that envelope's.
 */
export default function ImportStatementDialog({ envelopeId, onClose, onDone }: {
  envelopeId?: string; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<StatementImport | null>(null);
  const [keep, setKeep] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [pending, setPending] = useState(false);
  // The file picked now: an answer about another one (picked while it was read) is dropped.
  const picked = useRef<File | null>(null);

  const send = async (done: (message?: string) => void) => {
    if (!file) return;
    setProblem(null);
    setPending(true);
    try {
      const answer = await importStatement(fetchClient, file, { envelopeId, confirm: preview !== null, keepProbablyKnown: preview !== null && keep });
      if (picked.current !== file) return;
      if (!answer.written) { setPreview(answer); return; }
      // The lines, the accounts, and the closing balance turned into a value or a proposal.
      for (const path of [TRANSACTIONS, ACCOUNTS, CONNECTIONS, PROPOSALS]) void queryClient.invalidateQueries({ queryKey: ['get', path] });
      void invalidateInvestments(queryClient);
      done(t('banks.import.done', { count: answer.newCount }));
    } catch (error) {
      if (picked.current === file) setProblem(toProblem(error));
    } finally {
      setPending(false);
    }
  };

  return (
    <FormDialog title={t('banks.import.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-4" onSubmit={(event) => { event.preventDefault(); event.stopPropagation(); void send(done); }}>
          {/* An alert, as a document upload's: the focus stays on the button. */}
          {problem ? <p role="alert" className="text-sm text-destructive">{statementErrorText(t, problem)}</p> : null}
          <Field label={t('banks.import.file')}>
            {(control) => (
              <Input {...control} type="file" accept={ACCEPT} disabled={pending} onChange={(e) => {
                picked.current = e.currentTarget.files?.[0] ?? null;
                setFile(picked.current);
                setPreview(null);
                setKeep(false);
                setProblem(null);
              }} />
            )}
          </Field>
          {/* Always on the page, empty at first: a live region added with its first message is not announced. */}
          <div aria-live="polite" className="space-y-1 text-sm">
            {preview ? (
              <>
                <p className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{preview.accountName}</span>
                  {preview.ibanMasked ? <span className="text-muted-foreground">{preview.ibanMasked}</span> : null}
                  {preview.newAccount ? <Badge variant="secondary">{t('banks.import.new_account')}</Badge> : null}
                </p>
                <p>{[
                  t('banks.import.new', { count: preview.newCount }),
                  t('banks.import.known', { count: preview.knownCount }),
                  t('banks.import.probable', { count: preview.probablyKnownCount }),
                ].join(', ')}</p>
                {preview.from && preview.to ? <p>{t('banks.import.period', { from: formatLocalDate(preview.from), to: formatLocalDate(preview.to) })}</p> : null}
                {preview.closingBalance && preview.closingBalanceOn ? (
                  <p>{t('banks.import.closing', { amount: formatCurrency(preview.closingBalance, preview.currency), date: formatLocalDate(preview.closingBalanceOn) })}</p>
                ) : null}
              </>
            ) : null}
          </div>
          {preview && preview.probablyKnownCount > 0 ? (
            <SwitchField label={t('banks.import.keep')} hint={t('banks.import.keep_hint')} checked={keep} onCheckedChange={setKeep} />
          ) : null}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="outline" onClick={() => done()}>{t('common.cancel')}</Button>
            <Button type="submit" disabled={!file || pending}>{t(preview ? 'banks.import.submit' : 'banks.import.read')}</Button>
          </div>
        </form>
      )}
    </FormDialog>
  );
}
