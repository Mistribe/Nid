import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { runExport } from './assemble';
import { downloadBlob } from './download';

type State =
  | { kind: 'rest' }
  | { kind: 'running'; received: number; total: number }
  | { kind: 'done'; count: number }
  | { kind: 'changed' }
  | { kind: 'error'; problem: Presentation };

/** `/export` (1E4 design §2): what the file will hold, then the export, which downloads only a consistent file. */
export function ExportPage() {
  const { t } = useTranslation();
  const { $api, fetchClient } = useApi();
  const manifest = $api.useQuery('get', '/api/v1/platform/export/manifest');
  const [state, setState] = useState<State>({ kind: 'rest' });
  const current = useRef<AbortController | null>(null);

  // Leaving the screen stops the export: nothing downloads behind the person's back.
  useEffect(() => () => current.current?.abort(), []);

  const start = async () => {
    current.current?.abort();
    const controller = new AbortController();
    current.current = controller;
    setState({ kind: 'running', received: 0, total: 0 });
    try {
      const result = await runExport(fetchClient, {
        signal: controller.signal,
        onProgress: (p) => { if (!controller.signal.aborted) setState({ kind: 'running', ...p }); },
      });
      if (controller.signal.aborted) return;
      if (result.kind === 'changed') { setState({ kind: 'changed' }); return; }
      downloadBlob(result.blob, result.fileName);
      setState({ kind: 'done', count: result.count });
      void manifest.refetch();
    } catch (error) {
      if (controller.signal.aborted) return;
      setState({ kind: 'error', problem: present(toProblem(error)) });
    }
  };

  const cancel = () => {
    current.current?.abort();
    setState({ kind: 'rest' });
  };

  const sectionName = (name: string) => t(`export.sections.${name}`, { defaultValue: name });

  let body;
  if (state.kind === 'running') {
    const percent = state.total === 0 ? 0 : Math.round((state.received / state.total) * 100);
    body = (
      <div className="space-y-3">
        <div role="progressbar" aria-label={t('export.progress_label')} aria-valuemin={0}
          aria-valuemax={state.total} aria-valuenow={state.received} className="h-2 w-full overflow-hidden rounded bg-muted">
          <div className="h-full bg-primary" style={{ width: `${percent}%` }} />
        </div>
        <p>{t('export.progress', { received: state.received, total: state.total })}</p>
        <Button variant="outline" onClick={cancel}>{t('export.cancel')}</Button>
      </div>
    );
  } else if (state.kind === 'done') {
    body = (
      <div className="space-y-3">
        <p role="status">{t('export.done', { count: state.count })}</p>
        <Button onClick={() => void start()}>{t('export.again')}</Button>
      </div>
    );
  } else if (state.kind === 'changed') {
    body = (
      <div className="space-y-3">
        <p role="alert" className="rounded-md border border-destructive p-3">{t('export.changed')}</p>
        <Button onClick={() => void start()}>{t('export.restart')}</Button>
      </div>
    );
  } else if (state.kind === 'error') {
    body = (
      <div className="space-y-3">
        <ProblemMessage problem={state.problem} />
        <Button onClick={() => void start()}>{t('export.retry')}</Button>
      </div>
    );
  } else if (manifest.error) {
    body = <ProblemMessage problem={present(toProblem(manifest.error))} />;
  } else if (!manifest.data) {
    body = <p>{t('export.loading')}</p>;
  } else {
    const filled = manifest.data.sections.filter((section) => section.count > 0);
    body = (
      <div className="space-y-3">
        {filled.length === 0
          ? <p>{t('export.empty')}</p>
          : (
            <ul aria-label={t('export.contents')} className="list-disc space-y-1 pl-5">
              {filled.map((section) => (
                <li key={section.name}>{t('export.section_count', { name: sectionName(section.name), count: section.count })}</li>
              ))}
            </ul>
          )}
        <Button onClick={() => void start()}>{t('export.start')}</Button>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-semibold">{t('export.title')}</h1>
      <p>{t('export.rule')}</p>
      {body}
    </div>
  );
}
