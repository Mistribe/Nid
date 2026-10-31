import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ConfirmAction } from '@/components/ConfirmAction';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { INSURANCE, invalidateSubscriptions, type Detail } from '../api';
import '@/locales/subscriptions';

/** The insurance kinds the API knows; an unknown one is shown as « Autre ». */
export const INSURANCE_KINDS = ['home', 'car', 'health', 'provident', 'other'] as const;

/**
 * Design 2026-10-30-contract-terms-insurance §10: the subscription's policy under the contract, shown only when it has one.
 * A property the reader cannot see comes without name or link: only the insured label is shown. Ceilings and deductibles are
 * the household's own words, never read as amounts.
 */
export function InsuranceSection({ detail, onRemoved }: { detail: Detail; onRemoved?: (message: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const remove = $api.useMutation('delete', INSURANCE, {
    onSuccess: () => { void invalidateSubscriptions(queryClient); onRemoved?.(t('insurance.removed')); },
  });
  const insurance = detail.insurance;
  if (!insurance) return null;
  const kind = INSURANCE_KINDS.find((k) => k === insurance.kind) ?? 'other';
  const rows = [
    { label: t('insurance.kind'), value: t(`insurance.kind_options.${kind}`) },
    insurance.property ? {
      label: t('insurance.property'),
      value: <Link to="/properties/$id" params={{ id: insurance.property.id }} className="underline">{insurance.property.name}</Link>,
    } : null,
    insurance.insuredLabel ? { label: t('insurance.insured_label'), value: insurance.insuredLabel } : null,
    insurance.policyNumber ? { label: t('insurance.policy_number'), value: insurance.policyNumber } : null,
    insurance.insurerContact ? { label: t('insurance.insurer_contact'), value: insurance.insurerContact } : null,
  ].filter((r) => r !== null);
  return (
    <Section title={t('insurance.title')}>
      <dl className="grid gap-x-6 gap-y-1 sm:grid-cols-[max-content_1fr]">
        {rows.map((r) => (
          <div key={r.label} className="contents">
            <dt className="text-muted-foreground">{r.label}</dt>
            <dd>{r.value}</dd>
          </div>
        ))}
      </dl>
      {insurance.guarantees.length === 0 ? <p className="text-muted-foreground">{t('insurance.no_guarantees')}</p> : (
        <Table aria-label={t('insurance.guarantees')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('insurance.col_guarantee')}</TableHead>
              <TableHead>{t('insurance.col_ceiling')}</TableHead>
              <TableHead>{t('insurance.col_deductible')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {insurance.guarantees.map((g, i) => (
              <TableRow key={i}>
                <TableCell className="whitespace-normal font-medium">{g.label}</TableCell>
                <TableCell className="whitespace-normal">{g.ceiling}</TableCell>
                <TableCell className="whitespace-normal">{g.deductible}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {detail.canWrite ? (
        <div>
          <ConfirmAction label={t('insurance.remove')} confirmation={t('insurance.remove_confirm')} pending={remove.isPending}
            onConfirm={() => remove.mutate({ params: { path: { id: detail.id } } })} />
        </div>
      ) : null}
      {remove.error ? <ProblemMessage problem={present(toProblem(remove.error))} /> : null}
    </Section>
  );
}
