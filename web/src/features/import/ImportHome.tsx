import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { useDraft } from './draft';
import { FileCheck } from './FileCheck';
import { FileStep } from './FileStep';
import { RecentImports } from './RecentImports';
import { StartImport } from './StartImport';
import '@/locales/import';

/** `/import`: recent imports, then the file, its local check and its adults. */
export function ImportHome() {
  const { t } = useTranslation();
  const { file, setFile } = useDraft();

  if (!file) {
    return (
      <>
        <RecentImports />
        <FileStep title={t('import.file_title')} hint={t('import.file_hint')} onReady={setFile} />
      </>
    );
  }

  return (
    <>
      <FileCheck file={file} />
      <StartImport file={file} />
      <Button variant="outline" onClick={() => setFile(null)}>{t('import.another')}</Button>
    </>
  );
}
