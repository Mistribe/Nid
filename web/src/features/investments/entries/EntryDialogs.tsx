import { useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { ModalDialog } from '@/components/ModalDialog';
import type { EnvelopeDetail, Flow, Valuation } from '../api';
import { FlowForm } from './FlowForm';
import { ValueForm } from './ValueForm';
import '@/locales/investments';

/**
 * The value and flow forms in their dialogs. A save hands the dialog back (`close()`: the page then owns the focus
 * and the announcement, since a corrected row gets a new id); « Annuler » just closes it and Radix returns the focus.
 */
export function ValueDialog({ envelope, editing, onClose, onDone }: {
  envelope: EnvelopeDetail; editing?: Valuation; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={editing ? t('envelope.edit_value') : t('envelope.new_value')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <ValueForm envelope={envelope} editing={editing} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

export function FlowDialog({ envelope, editing, opening, onClose, onDone }: {
  envelope: EnvelopeDetail; editing?: Flow; opening?: boolean; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={editing ? t('envelope.edit_flow') : t('envelope.new_flow')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <FlowForm envelope={envelope} editing={editing} opening={opening} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}
