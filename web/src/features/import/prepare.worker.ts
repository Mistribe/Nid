import { prepareFile, type Prepared } from './prepare';

/** Parses, checks, cuts and hashes off the main thread (web design §6.2, step 4). */
const scope = self as unknown as { onmessage: ((event: MessageEvent<ArrayBuffer>) => void) | null; postMessage: (message: Prepared) => void };

scope.onmessage = (event) => {
  // A rejection inside the worker never reaches `Worker.onerror`: without this, the page would wait forever.
  void prepareFile(event.data)
    .then((prepared) => scope.postMessage(prepared))
    .catch(() => scope.postMessage({ ok: false, problems: [{ code: 'unreadable' }] }));
};
