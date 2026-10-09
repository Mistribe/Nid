"""Loan schedule reference vectors (spec 3A1 §5).

An implementation of docs/realestate/loans.md §3 and of spec 3A1 §4's decided rules in Python's decimal at 60 digits,
written from the documents, not translated from the C#. Writes vectors.json next to this file.

Run: python3 -I tests/vectors/loans/generate.py   (Python 3.9+, standard library only)
"""
import calendar
import json
import random
from datetime import date, timedelta
from decimal import ROUND_DOWN, ROUND_HALF_UP, Decimal as D, getcontext
from pathlib import Path

getcontext().prec = 60
CENT = D("0.01")
MAX_ROWS = 1200
SEED = 20261009
COUNT = 220


class Refused(Exception):
    pass


def half_up(x):
    return x.quantize(CENT, rounding=ROUND_HALF_UP)


def interest_round(x, mode):
    return x.quantize(CENT, rounding=ROUND_DOWN if mode == "truncate" else ROUND_HALF_UP)


def add_months(d, months):
    m = d.month - 1 + months
    y = d.year + m // 12
    m = m % 12 + 1
    return date(y, m, min(d.day, calendar.monthrange(y, m)[1]))


def annuity(balance, rate, n):
    if n <= 0:
        return balance
    if rate == 0:
        return half_up(balance / n)
    r = rate / 12
    return half_up(balance * r / (1 - 1 / (1 + r) ** n))


def insurance_for(loan, due_date, balance_before):
    total = D(0)
    for ins in loan["insurances"]:
        if ins["starts_on"] <= due_date and (ins["ends_on"] is None or due_date < ins["ends_on"]):
            if ins["mode"] == "fixed_monthly":
                total += ins["monthly_amount"]
            elif ins["mode"] == "rate_on_initial":
                total += half_up(ins["annual_rate"] * loan["principal"] / 12)
            else:
                total += half_up(ins["annual_rate"] * balance_before / 12)
    return total


def build(loan):
    phases, n_total = loan["phases"], loan["total_payments"]
    rounding = loan["rounding"]
    starts, s = [], 1
    for ph in phases:
        starts.append(s)
        s += ph["payments"] if ph["payments"] is not None else 10 ** 9
    known_end = s if phases[-1]["payments"] is not None else None

    def phase_at(k):
        if known_end is not None and k >= known_end:
            return None
        for i in range(len(phases) - 1, -1, -1):
            if k >= starts[i]:
                return i
        return 0

    due = lambda k: add_months(loan["first_payment_date"], k - 1)
    balance = loan["principal"]
    rows = []
    payment, override_active, term_reduction = None, False, D(0)
    current, projected = -1, False
    pending = sorted(loan["events"], key=lambda e: e["on"])
    first_due = due(1)
    for e in [e for e in pending if e["on"] < first_due]:
        if e["amount"] <= 0 or e["amount"] > balance:
            raise Refused("event_above_balance")
        balance -= e["amount"]
        if e["effect"] == "reduce_term":
            term_reduction += e["amount"]
    pending = [e for e in pending if e["on"] >= first_due]

    k = 0
    while balance > 0:
        k += 1
        if k > MAX_ROWS:
            raise Refused("schedule_too_long")
        due_date = due(k)
        i = phase_at(k)
        if i is None:
            rate, mode, seq = phases[-1]["annual_rate"], "annuity", phases[-1]["seq"]
            if not projected:
                projected = True
                payment = annuity(balance + term_reduction, rate, n_total - (k - 1))
                term_reduction, override_active = D(0), False
        else:
            ph = phases[i]
            rate, mode, seq = ph["annual_rate"], ph["mode"], ph["seq"]
            if i != current:
                current = i
                if ph["opening_balance_override"] is not None:
                    balance = ph["opening_balance_override"]
                if mode == "annuity":
                    if ph["payment_override"] is not None:
                        payment, override_active = ph["payment_override"], True
                    else:
                        payment, override_active = annuity(balance + term_reduction, rate, n_total - (k - 1)), False
                    term_reduction = D(0)
        before = balance
        if k == 1 and loan["first_period"] == "prorata_actual_365":
            days = (first_due - loan["disbursed_on"]).days
            interest = interest_round(balance * rate * days / 365, rounding)
        else:
            interest = interest_round(balance * rate / 12, rounding)
        if k >= n_total and not (mode == "annuity" and override_active):
            # The contractual last payment, and any later one, repays everything in every mode (spec 3A1 §4).
            pay = balance + interest
            principal = balance
            balance = D(0)
        elif mode == "deferred_total":
            pay, principal = D(0), D(0)
            balance += interest
        elif mode == "deferred_partial":
            pay, principal = interest, D(0)
        else:
            if override_active and payment <= interest:
                raise Refused("payment_below_interest")
            pay = min(payment, balance + interest)
            principal = pay - interest
            balance -= principal
        row = [pay, interest, principal, insurance_for(loan, due_date, before), balance]
        rows.append(row)
        next_due = due(k + 1)
        here = [e for e in pending if e["on"] < next_due]
        pending = [e for e in pending if e["on"] >= next_due]
        for e in here:
            if e["amount"] <= 0 or e["amount"] > balance:
                raise Refused("event_above_balance")
            balance -= e["amount"]
            row[4] = balance
            if e["effect"] == "reduce_payment":
                if mode == "annuity":
                    override_active = False
                    payment = annuity(balance, rate, n_total - k)
            elif mode != "annuity":
                term_reduction += e["amount"]
        if balance == 0:
            break
    if pending:
        raise Refused("event_above_balance")  # dated after the loan ended: the balance at its date is 0
    return rows


RATES = [D("0"), D("0.0076"), D("0.076"), D("0.0125"), D("0.035"), D("0.0199"), D("0.042"), D("0.0599"), D("0.08")]


def money(rng, low, high):
    return (D(rng.randint(low * 100, high * 100)) / 100).quantize(CENT)


def rate(rng):
    return rng.choice(RATES) if rng.random() < 0.6 else (D(rng.randint(1, 8000)) / D(100000)).normalize()


def phase(seq, mode, annual_rate, payments=None, override=None, opening=None):
    return dict(seq=seq, mode=mode, annual_rate=annual_rate, rate_type="variable" if seq > 1 else "fixed",
                payments=payments, payment_override=override, opening_balance_override=opening)


def random_loan(rng, index):
    principal = money(rng, 5000, 400000)
    total = rng.randint(12, 360)
    disbursed = date(2020, 1, 1) + timedelta(days=rng.randint(0, 1500))
    first = add_months(disbursed, 1) if rng.random() < 0.8 else disbursed + timedelta(days=rng.randint(10, 60))
    kind = rng.random()
    if kind < 0.55 or total < 24:
        phases = [phase(1, "annuity", rate(rng))]
    elif kind < 0.68:
        phases = [phase(1, "deferred_total", rate(rng), rng.randint(1, total // 4)), phase(2, "annuity", rate(rng))]
    elif kind < 0.78:
        phases = [phase(1, "deferred_partial", rate(rng), rng.randint(1, total // 4)), phase(2, "annuity", rate(rng))]
    elif kind < 0.83:
        # In fine: a deferral over the whole term, the principal repaid with the last payment.
        phases = [phase(1, rng.choice(["deferred_partial", "deferred_total"]), rate(rng))]
    elif kind < 0.92:
        a = rng.randint(1, total // 3)
        b = rng.randint(1, total // 3)
        phases = [phase(1, "annuity", rate(rng), a), phase(2, "annuity", rate(rng), b), phase(3, "annuity", rate(rng))]
    else:
        # Phases that run out before the loan ends: projected at the last rate.
        phases = [phase(1, "annuity", rate(rng), rng.randint(1, total // 2))]
    loan = dict(id=f"L{index:03d}", currency="EUR", principal=principal, disbursed_on=disbursed, first_payment_date=first,
                total_payments=total, rounding="truncate" if rng.random() < 0.15 else "half_up",
                first_period="prorata_actual_365" if rng.random() < 0.15 else "full",
                phases=phases, insurances=[], events=[])
    if rng.random() < 0.4:
        mode = rng.choice(["fixed_monthly", "rate_on_initial", "rate_on_remaining"])
        loan["insurances"].append(dict(mode=mode, monthly_amount=money(rng, 5, 80) if mode == "fixed_monthly" else None,
                                       annual_rate=None if mode == "fixed_monthly" else D(rng.randint(5, 60)) / D(10000),
                                       starts_on=first, ends_on=add_months(first, rng.randint(12, total)) if rng.random() < 0.3 else None))
    if rng.random() < 0.3:
        for _ in range(rng.randint(1, 2)):
            k = rng.randint(1, total - 1)
            on = add_months(first, k - 1) + (timedelta(days=rng.randint(1, 20)) if rng.random() < 0.25 else timedelta(0))
            loan["events"].append(dict(on=on, amount=money(rng, 500, max(600, int(principal) // 8)),
                                       effect=rng.choice(["reduce_term", "reduce_payment"])))
    if rng.random() < 0.4 and phases[0]["mode"] == "annuity" and len(phases) > 1:
        try:
            first_payment = build(loan)[0][0]
            phases[0]["payment_override"] = half_up(first_payment * D(rng.randint(98, 115)) / 100)
        except Refused:
            pass
    return loan


def text(x):
    return format(x, "f") if isinstance(x, D) else x


def encode(loan, rows):
    def clean(d):
        return {k: (text(v) if isinstance(v, D) else v.isoformat() if isinstance(v, date) else v)
                for k, v in d.items() if v is not None}
    terms = clean({k: v for k, v in loan.items() if k not in ("phases", "insurances", "events", "id")})
    terms["phases"] = [clean(p) for p in loan["phases"]]
    terms["insurances"] = [clean(i) for i in loan["insurances"]]
    terms["events"] = [clean(e) for e in loan["events"]]
    return dict(id=loan["id"], terms=terms, rows=[[text(v) for v in r] for r in rows])


def main():
    rng = random.Random(SEED)
    out = []
    index = 0
    while len(out) < COUNT:
        index += 1
        loan = random_loan(rng, index)
        try:
            rows = build(loan)
        except Refused:
            loan["events"] = []
            for p in loan["phases"]:
                p["payment_override"] = None
            try:
                rows = build(loan)
            except Refused:
                continue
        out.append(encode(loan, rows))
    path = Path(__file__).with_name("vectors.json")
    path.write_text(json.dumps(dict(seed=SEED, loans=out), separators=(",", ":")), encoding="utf-8")
    print(f"{len(out)} loans, {sum(len(l['rows']) for l in out)} rows, {path.stat().st_size} bytes")


if __name__ == "__main__":
    main()
