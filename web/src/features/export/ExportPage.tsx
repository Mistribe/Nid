import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { PageHeader } from '@/components/page/PageHeader';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { runExport } from './assemble';
import { downloadBlob } from '@/lib/download';
import '@/locales/export';

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

  // Every step removes the button just pressed: the focus goes to the new step's control (WCAG 2.4.3), never on
  // arrival, only once the person has acted.
  const primary = useRef<HTMLButtonElement>(null);
  const acted = useRef(false);
  useEffect(() => { if (acted.current) primary.current?.focus(); }, [state.kind]);

  const start = async () => {
    acted.current = true;
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
    acted.current = true;
    current.current?.abort();
    setState({ kind: 'rest' });
  };

  const sectionName = (name: string) => t(`export.sections.${name}`, { defaultValue: name });

  // One control per step, the header's primary (design-system.md §6.1); the body says where the export stands.
  let action;
  let body;
  if (state.kind === 'running') {
    const percent = state.total === 0 ? 0 : Math.round((state.received / state.total) * 100);
    action = <Button ref={primary} variant="outline" onClick={cancel}>{t('export.cancel')}</Button>;
    body = (
      <div className="space-y-3">
        <div role="progressbar" aria-label={t('export.progress_label')} aria-valuemin={0}
          aria-valuemax={state.total} aria-valuenow={state.received} className="h-2 w-full overflow-hidden rounded-full bg-muted">
          <div className="h-full bg-primary" style={{ width: `${percent}%` }} />
        </div>
        <p>{t('export.progress', { received: state.received, total: state.total })}</p>
      </div>
    );
  } else if (state.kind === 'done') {
    action = <Button ref={primary} onClick={() => void start()}>{t('export.again')}</Button>;
    body = <p role="status">{t('export.done', { count: state.count })}</p>;
  } else if (state.kind === 'changed') {
    action = <Button ref={primary} onClick={() => void start()}>{t('export.restart')}</Button>;
    body = <p role="alert" className="rounded-md border border-destructive p-3">{t('export.changed')}</p>;
  } else if (state.kind === 'error') {
    action = <Button ref={primary} onClick={() => void start()}>{t('export.retry')}</Button>;
    body = <ProblemMessage problem={state.problem} />;
  } else if (manifest.error) {
    body = <ProblemMessage problem={present(toProblem(manifest.error))} />;
  } else if (!manifest.data) {
    body = <p>{t('export.loading')}</p>;
  } else {
    const filled = manifest.data.sections.filter((section) => section.count > 0);
    action = <Button ref={primary} onClick={() => void start()}>{t('export.start')}</Button>;
    body = filled.length === 0
      ? <p>{t('export.empty')}</p>
      : (
        <Table aria-label={t('export.contents')}>
          <TableHeader>
            <TableRow><TableHead>{t('export.section')}</TableHead><TableHead className="text-right">{t('export.count')}</TableHead></TableRow>
          </TableHeader>
          <TableBody>
            {filled.map((section) => (
              <TableRow key={section.name}>
                <TableCell>{sectionName(section.name)}</TableCell>
                <TableCell className="text-right tabular-nums">{section.count}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      );
  }

  return (
    <div className="space-y-6">
      <PageHeader title={t('export.title')} actions={action} />
      <p>{t('export.rule')}</p>
      {body}
    </div>
  );
}
