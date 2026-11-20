import { useTranslation } from 'react-i18next';
import { notEditable, type KindUi } from './kindParts';
import '@/locales/proposals';

type MakePrivatePayload = { ownerId: string; ownerName: string };

/** #384: what the other adult agrees to: the item private, seen by its owner alone. */
function MakePrivatePreview({ original }: { preview: unknown; original: unknown }) {
  const { t } = useTranslation();
  return <p>{t('proposal.make_private.effect', { owner: (original as MakePrivatePayload).ownerName })}</p>;
}

/** #384: the owner's request to make a shared item private, decided by the other adult only. */
export const makePrivate: KindUi = {
  label: 'proposals.kind.make_private',
  summary: (t, p) => t('proposals.summary.make_private', {
    owner: (p.payload as MakePrivatePayload).ownerName, name: p.targetName ?? t('proposals.unknown_target'),
  }),
  Preview: MakePrivatePreview,
  ...notEditable((t) => t('proposal.make_private.as_proposed')),
  link: () => undefined,
};
