import type { TFunction } from 'i18next';
import { useApi } from '@/api/ApiProvider';
import type { Api } from '@/api/client';
import { ProblemError, toProblem, type Problem } from '@/api/errors';
import type { components } from '@/api/schema';
import { bankErrorText } from '@/features/settings/banks/api';
import '@/locales/banks';

export type BankTransaction = components['schemas']['BankTransactionView'];
export type BankTransactionPage = components['schemas']['BankTransactionPage'];
export type StatementImport = components['schemas']['StatementImportView'];

export const TRANSACTIONS = '/api/v1/banking/envelopes/{envelopeId}/transactions' as const;
export const STATEMENTS = '/api/v1/banking/statements' as const;

/** The lines of an envelope's bank account and its cards, newest first, 50 a page; `from`/`to` (ISO, or absent) bound the period. */
export function useTransactions(envelopeId: string, from?: string, to?: string) {
  const { $api } = useApi();
  // Through openapi-react-query, as the initial bundle already does: a direct `useInfiniteQuery` here splits react-query's core into a chunk of its own (+1 kB).
  return $api.useInfiniteQuery('get', TRANSACTIONS, { params: { path: { envelopeId }, query: { from, to } } }, {
    pageParamName: 'page',
    initialPageParam: 1,
    getNextPageParam: (last: BankTransactionPage, pages: BankTransactionPage[]) => (last.hasMore ? pages.length + 1 : undefined),
  });
}

/**
 * One statement file as `multipart/form-data` (as a document upload: the client keeps the CSRF header, the browser writes the
 * boundary). `confirm: false` previews, writing nothing; the same file is sent again with `confirm: true`. A refusal is thrown as a `ProblemError`.
 */
export async function importStatement(
  fetchClient: Api['fetchClient'], file: File, fields: { envelopeId?: string; confirm: boolean; keepProbablyKnown: boolean },
): Promise<StatementImport> {
  const body = new FormData();
  body.append('file', file);
  if (fields.envelopeId) body.append('envelopeId', fields.envelopeId);
  body.append('confirm', String(fields.confirm));
  body.append('keepProbablyKnown', String(fields.keepProbablyKnown));
  const { data, error } = await fetchClient.POST(STATEMENTS, { body: body as never });
  if (error !== undefined || !data) throw new ProblemError(toProblem(error));
  return data;
}

/** A refused statement in French: `line` rides on `banking.file_malformed` when the reader knows it. */
export function statementErrorText(t: TFunction, problem: Problem & { line?: number }): string {
  if (problem.code === 'banking.file_malformed' && problem.line !== undefined) return t('banks.error.banking_file_malformed_line', { line: problem.line });
  if (problem.status === 413) return t('banks.error.banking_file_too_large');
  return bankErrorText(t, problem.code ?? '', t('banks.import.failed'));
}
