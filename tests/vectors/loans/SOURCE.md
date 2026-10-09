# Loan schedule reference vectors

`vectors.json` is written by `generate.py`: an implementation of `docs/realestate/loans.md` §3 and of spec
`docs/superpowers/specs/2026-10-09-loan-schedule-engine-design.md` §4 in Python's `decimal` at 60 digits, written from
the documents, not translated from the C#. Fixed seed; 220 loans.

Regenerate (Python 3.9+, standard library only): `python3 -I tests/vectors/loans/generate.py`

Each loan has its `terms` (decimal strings, ISO dates, snake_case enum names) and its `rows`:
`[payment, interest, principal, insurance, balance]` per payment, the balance after any early repayment applied right
after that payment. `Nid.RealEstate.Tests.LoanVectorTests` compares every row to the cent.
