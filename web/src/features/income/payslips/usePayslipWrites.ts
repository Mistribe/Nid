import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useApi } from '@/api/ApiProvider';
import { toProblem, type Problem } from '@/api/errors';
import { UNSUPPORTED } from '../../documents/DocumentUpload';
import { invalidateDocuments, type DocumentLink, type DocumentSummary } from '@/api/documents';
import { uploadDocument, useUnlinkDocument } from '../../documents/api';

type Target = { kind: string; id: string };
/** Where an attached payslip goes: its target, its document date and its title. */
export type AttachFields = { target: Target; date: string; title: string };

export type PayslipWrites = ReturnType<typeof usePayslipWrites>;

/**
 * Attach, replace and remove a payslip. Each write stops at its first failure and keeps it in `problem`; `file` names
 * the file when the failure is its upload (the documents page words a refused file by its name).
 */
export function usePayslipWrites() {
  const { fetchClient } = useApi();
  const queryClient = useQueryClient();
  const unlink = useUnlinkDocument();
  const [pending, setPending] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [file, setFile] = useState<string | null>(null);

  const run = async (work: () => Promise<void>) => {
    setPending(true);
    setProblem(null);
    setFile(null);
    try { await work(); } catch (error) { setProblem(toProblem(error)); } finally { setPending(false); }
  };
  const upload = async (picked: File, target: Target, date: string, title: string) => {
    setFile(picked.name);
    await uploadDocument(fetchClient, picked, { targetKind: target.kind, targetId: target.id, role: 'payslip', date, title });
    setFile(null);
    await invalidateDocuments(queryClient);
  };
  const unlinkAll = async (doc: DocumentSummary, links: DocumentLink[]) => {
    for (const l of links) await unlink.mutateAsync({ params: { path: { id: doc.id, targetKind: l.targetKind, targetId: l.targetId } } });
  };

  return {
    attach: (picked: File, target: Target, date: string, title: string) => run(() => upload(picked, target, date, title)),
    /** The new file first: if the old one cannot be removed, the new one stays and the error shows. */
    replace: (old: DocumentSummary, links: DocumentLink[], picked: File, target: Target, date: string, title: string) =>
      run(async () => { await upload(picked, target, date, title); await unlinkAll(old, links); }),
    remove: (doc: DocumentSummary, links: DocumentLink[]) => run(() => unlinkAll(doc, links)),
    /** A dropped file of a type the API does not take, worded as its refusal and not sent. */
    refuse: (picked: File) => { setProblem(UNSUPPORTED); setFile(picked.name); },
    pending, problem, file,
  };
}
