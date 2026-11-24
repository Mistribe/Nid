// #603: the smoke test's only way between nid and its worker (compose.worker-gate.yaml). Each connection nid opens to
// worker:8443 goes on, byte for byte (its TLS included), to the worker on a network nid is not on; none goes the other way.
import net from 'node:net';

net
  .createServer((from) => {
    const to = net.connect(8443, 'worker-isolated');
    const close = () => {
      from.destroy();
      to.destroy();
    };
    from.on('error', close);
    to.on('error', close);
    from.pipe(to).pipe(from);
  })
  .listen(8443);
