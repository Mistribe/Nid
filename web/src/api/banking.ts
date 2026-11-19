import type { TFunction } from 'i18next';
import { useApi } from './ApiProvider';
import '@/locales/banks';
import type { components } from './schema';

export type BankAccount = components['schemas']['BankAccountView'];
export type Connection = components['schemas']['ConnectionView'];
export type BankRule = components['schemas']['BankRuleView'];

export const BANKING_STATUS = '/api/v1/banking/status' as const;
export const ASPSPS = '/api/v1/banking/aspsps' as const;
export const CONNECTIONS = '/api/v1/banking/connections' as const;
export const RENEW = '/api/v1/banking/connections/{id}/renew' as const;
export const CONNECTION = '/api/v1/banking/connections/{id}' as const;
export const ACCOUNTS = '/api/v1/banking/accounts' as const;
export const ACCOUNT = '/api/v1/banking/accounts/{id}' as const;
export const ACCOUNT_ENVELOPE = '/api/v1/banking/accounts/{id}/envelope' as const;
export const SYNC = '/api/v1/banking/sync' as const;
export const RULES = '/api/v1/banking/rules' as const;
export const RULE = '/api/v1/banking/rules/{id}' as const;

/** Whether bank sync is on for the household, and configured by its administrator. */
export function useBankingStatus() {
  const { $api } = useApi();
  return $api.useQuery('get', BANKING_STATUS);
}

/** The envelope kinds a bank account goes with (the server refuses any other, `banking.envelope_kind`). */
export const ASSOCIABLE = ['checking', 'savings', 'other'];

const NONE: BankAccount[] = [];

/**
 * The bank accounts the person reads (those of one envelope with `envelopeId`): none while bank sync is off or unreadable; with
 * bank sync on they are read even without Enable Banking (statement files); `undefined` while that is not known yet.
 */
export function useBankAccounts(envelopeId?: string): BankAccount[] | undefined {
  const { $api } = useApi();
  const status = useBankingStatus();
  const on = status.data?.enabled === true;
  const accounts = $api.useQuery('get', ACCOUNTS, { params: { query: envelopeId ? { envelopeId } : {} } }, { enabled: on });
  if (status.error || accounts.error || (status.data && !on)) return NONE;
  return accounts.data;
}

/** The IBAN's last four characters as people read them: « •••• 1234 ». */
export const maskedIban = (last4: string) => `•••• ${last4}`;

/** A bank account as people read it: its name, else its masked IBAN, else its kind (a bank may send no name at all). */
export const accountLabel = (t: TFunction, account: Pick<BankAccount, 'name' | 'ibanMasked' | 'kind'>) =>
  account.name.trim() || (account.ibanMasked ? maskedIban(account.ibanMasked) : t(`banks.kind.${account.kind}`, { defaultValue: account.kind }));

/** An envelope's bank lines, the statement import and the lines left to link (`features/operations`). */
export type BankTransaction = components['schemas']['BankTransactionView'];
export type BankTransactionPage = components['schemas']['BankTransactionPage'];
export type ExpectedCharge = components['schemas']['ExpectedChargeView'];
export type StatementImport = components['schemas']['StatementImportView'];
export const TRANSACTIONS = '/api/v1/banking/envelopes/{envelopeId}/transactions' as const;
export const STATEMENTS = '/api/v1/banking/statements' as const;
export const UNLINKED_LINES = '/api/v1/banking/transactions' as const;
export const CANDIDATES = '/api/v1/banking/transactions/{id}/candidates' as const;

/** The household's bank categories (archived and merged ones included) and a line's category (`features/operations`). */
export type BankCategory = components['schemas']['BankCategoryView'];
export const CATEGORIES = '/api/v1/banking/categories' as const;
export const CATEGORY = '/api/v1/banking/categories/{id}' as const;
export const CATEGORY_MERGE = '/api/v1/banking/categories/{id}/merge' as const;
export const TRANSACTION_CATEGORY = '/api/v1/banking/transactions/{id}/category' as const;

/** Every bank category of the household, archived and merged ones included. */
export function useCategories() {
  const { $api } = useApi();
  return $api.useQuery('get', CATEGORIES);
}

/** « 2026-09-30 » as « 30/09 »: the day of a balance, as the notes and the routine write it (a string swap, nothing computed). */
export const dayMonth = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;

/** A server code as a `banks.error` key (its dots would read as nesting); an unknown one reads as `fallback`. */
export const bankErrorText = (t: TFunction, code: string, fallback: string) => t(`banks.error.${code.replaceAll('.', '_')}`, { defaultValue: fallback });
