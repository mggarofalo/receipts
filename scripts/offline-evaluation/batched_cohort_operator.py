"""Explicit operator entry for a private batched paired cohort.

Carry earlier calls against this pilot and historical spend from other ledgers;
this ledger retains its own attempts. Never retry
an unknown reservation or delete/roll back the private campaign directory.
"""
import argparse
import json
import os
from pathlib import Path
import sys

from batched_paired_cohort import BatchedPairedCohort, CohortIdentityError
from campaign_ledger import CampaignLedger, LedgerError, Policy, _canonical, _unique, require_plain_absolute_path
from cohort_live_transport import CohortLiveTransport
from offline_coordinator import OfflineCoordinator


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise LedgerError('invalid arguments')


def main(argv=None):
    parser = Parser(description='Run a private batched paired cohort')
    for name in ('private-root', 'input', 'host', 'dll', 'key-environment'):
        parser.add_argument('--' + name, required=True)
    for name in ('aggregate-call-limit', 'prior-calls', 'max-calls', 'budget-nanodollars', 'prior-spend-nanodollars'):
        parser.add_argument('--' + name, type=int, required=True)
    parser.add_argument('--create', action='store_true', help='Create a new ledger; omit to reopen it')
    try:
        args = parser.parse_args(argv)
        if not 1 <= args.aggregate_call_limit <= 30:
            raise LedgerError('pilot aggregate call limit must be at most 30')
        root = require_plain_absolute_path(Path(args.private_root))
        source = require_plain_absolute_path(Path(args.input))
        if not root.is_dir() or not source.is_file() or any((p / '.git').exists() for p in (source.parent, *source.parents)):
            raise LedgerError('private input and directory required')
        cases = json.loads(source.read_bytes(), object_pairs_hook=_unique)
        if not isinstance(cases, list) or len(cases) != 1:
            raise LedgerError('pilot requires exactly one case')
        policy = Policy('batched-paired-cohort', args.max_calls, args.budget_nanodollars, args.prior_spend_nanodollars)
        policy.validate()
        transport = CohortLiveTransport(args.key_environment)
        ledger_path = root / 'ledger.jsonl'
        with CampaignLedger(ledger_path, create_policy=policy if args.create else None) as ledger:
            if ledger.snapshot()['policy'] != policy.__dict__:
                raise LedgerError('existing ledger policy differs')
            scheduler = BatchedPairedCohort(OfflineCoordinator(ledger, root), args.host, args.dll, cases,
                aggregate_call_limit=args.aggregate_call_limit, prior_calls=args.prior_calls, live_transport=transport)
            results = {}
            complete = unresolved = 0
            for case in scheduler.cases:
                try:
                    prediction = scheduler.run_case(case['id'], {})
                    item = {'status': 'complete', 'predictions': prediction}
                except CohortIdentityError:
                    raise
                except (LedgerError, OSError, UnicodeError):
                    item = {'status': 'unresolved'}
                results[case['id']] = item
                complete += item['status'] == 'complete'
                unresolved += item['status'] == 'unresolved'
                print(f'completed={complete} unresolved={unresolved} attempts={ledger.snapshot()["attempts"]}', flush=True)
            if unresolved:
                return 1
            output = require_plain_absolute_path(root / 'predictions.json', allow_missing_leaf=True)
            encoded = _canonical(results)
            if output.exists():
                if output.read_bytes() != encoded:
                    raise LedgerError('existing predictions differ')
            else:
                with output.open('xb') as stream:
                    stream.write(encoded)
                    stream.flush()
                    os.fsync(stream.fileno())
            return 0 if unresolved == 0 else 1
    except Exception:
        print('Cohort stopped; inspect the private campaign and inputs.', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
