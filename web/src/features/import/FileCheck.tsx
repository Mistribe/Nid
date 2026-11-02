import { useTranslation } from 'react-i18next';
import type { ReadyFile } from './draft';
import { StepCard } from './StepCard';
import '@/locales/import';

/** Step 2 of web design §6.2 once the file passes: what will be sent. */
export function FileCheck({ file }: { file: ReadyFile }) {
  const { t } = useTranslation();
  return (
    <StepCard>
    <section aria-labelledby="import-check-title" className="space-y-2">
      <h2 id="import-check-title" className="text-lg font-semibold">{t('import.check_title')}</h2>
      <p>{t('import.check_file', { name: file.name })}</p>
      <ul className="list-disc pl-5">
        {file.sections.map((section) => (
          <li key={section.name}>{t('import.check_section', { name: section.name, count: section.count })}</li>
        ))}
      </ul>
      <p>{t('import.check_batches', { n: file.batches.length })}</p>
      <p className="text-sm text-muted-foreground">{t('import.check_note')}</p>
    </section>
    </StepCard>
  );
}
