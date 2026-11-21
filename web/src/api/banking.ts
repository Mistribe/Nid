import type { TFunction } from 'i18next';
import { useApi } from './ApiProvider';
import { present, toProblem, type Presentation } from './errors';
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
export const APPLICATION = '/api/v1/banking/application' as const;
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

/** A card's kind with its debit when the bank says which (« Carte à débit immédiat »), any other account's kind. */
export const accountKind = (t: TFunction, a: Pick<BankAccount, 'kind' | 'cardDebit'>) => (a.kind === 'card' && a.cardDebit === 'immediate' ? t('banks.kind_card_immediate')
  : a.kind === 'card' && a.cardDebit === 'deferred' ? t('banks.kind_card_deferred')
  : t(`banks.kind.${a.kind}`, { defaultValue: a.kind }));

/** #353: what « Régler » shows to recognise an account, and the checking account a card likely belongs to. */
export const ACCOUNT_OVERVIEW = '/api/v1/banking/accounts/{id}/overview' as const;

/** A bank account as people read it: its name, else its masked IBAN, else its kind (a bank may send no name at all). */
export const accountLabel = (t: TFunction, account: Pick<BankAccount, 'name' | 'ibanMasked' | 'kind'>) =>
  account.name.trim() || (account.ibanMasked ? maskedIban(account.ibanMasked) : t(`banks.kind.${account.kind}`, { defaultValue: account.kind }));

/** An envelope's bank lines, the statement import and the lines left to link (`features/operations`). */
export type BankTransaction = components['schemas']['BankTransactionView'];
export type OperationPage = components['schemas']['OperationPage'];
export const OPERATIONS = '/api/v1/banking/operations' as const;
export type BankTransactionPage = components['schemas']['BankTransactionPage'];
export type ExpectedCharge = components['schemas']['ExpectedChargeView'];
export type StatementImport = components['schemas']['StatementImportView'];
export const TRANSACTIONS = '/api/v1/banking/envelopes/{envelopeId}/transactions' as const;
export const STATEMENTS = '/api/v1/banking/statements' as const;
export const CANDIDATES = '/api/v1/banking/transactions/{id}/candidates' as const;

/** The household's bank categories (archived and merged ones included) and a line's category (`features/operations`). */
export type BankCategory = components['schemas']['BankCategoryView'];
export const CATEGORIES = '/api/v1/banking/categories' as const;
export const CATEGORY = '/api/v1/banking/categories/{id}' as const;
export const CATEGORY_MERGE = '/api/v1/banking/categories/{id}/merge' as const;
export const TRANSACTION_CATEGORY = '/api/v1/banking/transactions/{id}/category' as const;
/** #426: one category for several lines; the server answers the lines changed and the refused ones with their code. */
export const TRANSACTIONS_CATEGORY = '/api/v1/banking/transactions/category' as const;

/** Every bank category of the household, archived and merged ones included. */
export function useCategories() {
  const { $api } = useApi();
  return $api.useQuery('get', CATEGORIES);
}

/** « 2026-09-30 » as « 30/09 »: the day of a balance, as the notes and the routine write it (a string swap, nothing computed). */
export const dayMonth = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}`;

/** A server code as a `banks.error` key (its dots would read as nesting); an unknown one reads as `fallback`. */
export const bankErrorText = (t: TFunction, code: string, fallback: string) => t(`banks.error.${code.replaceAll('.', '_')}`, { defaultValue: fallback });

/** A refusal as people read it: a banking code in French, anything else as `present` words it. */
export function presentBankError(t: TFunction, error: unknown): Presentation {
  const problem = toProblem(error);
  const shown = present(problem);
  // The size cap and the JSON-only guard answer before any code: 413 is an oversized key file, 415 a malformed upload.
  if (problem.status === 413) return { ...shown, detail: t('banks.application.file_too_large') };
  if (problem.status === 415) return { ...shown, detail: t('banks.error.invalid_upload') };
  return problem.code?.startsWith('banking.') ? { ...shown, detail: bankErrorText(t, problem.code, shown.detail ?? '') } : shown;
}

/** The active categories as a combobox lists them: each parent, then its children as « Parent › Enfant ». */
export const activeCategoryOptions = (categories: BankCategory[] | undefined) => {
  const active = (categories ?? []).filter((c) => !c.archived && !c.mergedIntoId);
  return active.filter((p) => !p.parentId).flatMap((p) => [
    { value: p.id, label: p.name },
    ...active.filter((c) => c.parentId === p.id).map((c) => ({ value: c.id, label: `${p.name} › ${c.name}` })),
  ]);
};

/** The household's category rules (« CARREFOUR » → Courses), the lines « Appliquer aux opérations passées » would change, and that change. */
export type CategoryRule = components['schemas']['CategoryRuleView'];
export const CATEGORY_RULES = '/api/v1/banking/category-rules' as const;
export const CATEGORY_RULE = '/api/v1/banking/category-rules/{id}' as const;
export const CATEGORY_RULE_PREVIEW = '/api/v1/banking/category-rules/{id}/preview' as const;
export const CATEGORY_RULE_APPLY = '/api/v1/banking/category-rules/{id}/apply' as const;
/** #418: a rule not yet saved, previewed: the lines it would change, those it matches but would not, by reason. */
export const CATEGORY_RULE_DRAFT_PREVIEW = '/api/v1/banking/category-rules/preview' as const;
export type CategoryRuleDraftPreview = components['schemas']['CategoryRuleDraftPreview'];
