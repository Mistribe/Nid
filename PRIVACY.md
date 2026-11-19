# Privacy

Nid is self-hosted software, not a service. The Nid project runs no server for it, receives no data from any
installation, and has no account, analytics or telemetry of its own.

## Who holds the data

Each installation is run by its own operator, usually the household itself, on hardware it controls. The operator
decides who signs in, holds the database, the backups and the logs, and is responsible for protecting them
(`deploy/README.md` §9).

## What leaves an installation

Only the `nid` service makes outbound calls, and only to the hosts its operator allows (`deploy/README.md` §1):

- the provider catalogue and exchange rates, which send no household data;
- Enable Banking, only when the operator turns bank sync on: it reads balances and transactions of the accounts each
  adult links, read-only, with no payment call; each bank session is encrypted in the database;
- any other service the operator turns on, such as a cloud language model for document reading
  (`docs/ingestion/llm-providers.md`).

## Questions

Ask the operator of the installation you use. For Nid itself, open an issue on
[GitHub](https://github.com/Mistribe/Nid/issues); report a security problem as `SECURITY.md` explains.
