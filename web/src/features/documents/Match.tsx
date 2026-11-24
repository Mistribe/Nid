import { useTranslation } from 'react-i18next';
import type { DocumentSummary } from '@/api/documents';
import '@/locales/documents';

// #169: the server marks the words found with these two characters (DocumentMatch.StartMark, EndMark); the snippet stays text.
export const START_MARK = '\uE000';
export const END_MARK = '\uE001';

/** #169: where the search found the document in its text: the snippet, the words found highlighted, its page, how it was read. */
export function Match({ match }: { match: NonNullable<DocumentSummary['match']> }) {
  const { t } = useTranslation();
  const parts = match.snippet.split(START_MARK).flatMap((piece, i) => {
    if (i === 0) return [{ text: piece, found: false }];
    const [found = '', rest = ''] = piece.split(END_MARK);
    return [{ text: found, found: true }, { text: rest, found: false }];
  });
  const about = [
    match.byMeaning ? t('documents_page.match.by_meaning') : null,
    t('documents_page.match.page', { page: match.page }),
    match.ocr ? t('documents_page.match.ocr') : null,
  ].filter(Boolean).join(' · ');
  return (
    <span className="block max-w-prose text-sm font-normal text-muted-foreground">
      <span className="block">
        {match.byMeaning ? '' : '…'}
        {parts.map((p, i) => (p.found ? <mark key={i} className="rounded-sm bg-primary/15 px-0.5 text-foreground">{p.text}</mark> : <span key={i}>{p.text}</span>))}
        …
      </span>
      <span className="block text-xs">{about}</span>
    </span>
  );
}
