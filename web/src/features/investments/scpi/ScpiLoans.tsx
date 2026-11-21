import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { LOANS } from '@/api/loans';
import { tileOf } from '@/components/nav';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { Section } from '@/components/page/Section';
import { money } from '../../loans/format';
import '@/locales/investments';

/** #180: the loans financing this SCPI, filtered by the server (`?envelope=`), each with its outstanding capital. */
export function ScpiLoans({ envelopeId }: { envelopeId: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', LOANS, { params: { query: { scope: 'me', ended: true, envelope: envelopeId } } });
  const loans = answer.data?.items ?? [];
  return (
    <Section title={t('envelope.scpi_loans.title')}>
      {answer.isPending ? null : answer.isError ? <p className="text-muted-foreground">{t('envelope.scpi_loans.error')}</p> : loans.length === 0 ? <p className="text-muted-foreground">{t('envelope.scpi_loans.none')}</p> : (
        <GroupedList>
          {loans.map((l) => (
            <GroupedRow key={l.id} link={{ to: '/loans/$id', params: { id: l.id } }} tile={tileOf('/loans')} title={l.name}
              detail={t('envelope.scpi_loans.balance', { amount: money(l.currency, l.remainingBalance) })} />
          ))}
        </GroupedList>
      )}
    </Section>
  );
}
