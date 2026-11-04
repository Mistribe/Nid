import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/utils';

/** The nest: two arcs and an egg. The arcs follow the text colour, the egg the accent (or the text, on a tile). */
export function Logo({ className, onTile }: { className?: string; onTile?: boolean }) {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" fill="none" className={className}>
      <path d="M3 12.5 Q12 23 21 12.5" stroke="currentColor" strokeWidth={2} strokeLinecap="round" />
      <path d="M6.5 14 Q12 19.5 17.5 14" stroke="currentColor" strokeWidth={2} strokeLinecap="round" />
      <ellipse cx="12" cy="9" rx="3" ry="3.8" fill={onTile ? 'currentColor' : 'var(--primary)'} />
    </svg>
  );
}

/** The mark, then « Nid »; the height comes from `className` (the mark fills it, the letters are 1.1x). */
export function Wordmark({ className, markOnly }: { className?: string; markOnly?: boolean }) {
  const { t } = useTranslation();
  return (
    <span className={cn('inline-flex items-center gap-[0.35em]', className)}>
      <Logo className="h-full w-auto shrink-0" />
      {markOnly ? null : (
        // The letters (Fraunces 600, OFL: credit in the file) load as a mask over the text colour, out of the entry chunk;
        // forced colours drop backgrounds, so they paint CanvasText there.
        <span data-wordmark aria-hidden="true"
          className="aspect-[3427/1512] h-[110%] shrink-0 bg-current mask-[url(/wordmark.svg)] mask-contain mask-center mask-no-repeat forced-colors:bg-[CanvasText] forced-colors:forced-color-adjust-none" />
      )}
      <span className="sr-only">{t('app.name')}</span>
    </span>
  );
}
