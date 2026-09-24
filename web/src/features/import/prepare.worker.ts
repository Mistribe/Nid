import { prepareFile, type Prepared } from './prepare';

/** Parses, checks, cuts and hashes off the main thread (web design §6.2, step 4). */
const scope = self as unknown as { onmessage: ((event: MessageEvent<ArrayBuffer>) => void) | null; postMessage: (message: Prepared) => void };

scope.onmessage = (event) => {
  void prepareFile(event.data).then((prepared) => scope.postMessage(prepared));
};
