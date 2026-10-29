"""Explicit live launcher. Results and raw evidence stay in the private root."""
import argparse
import json
import os
from pathlib import Path
import sys
import tempfile

from campaign_ledger import CampaignLedger, LedgerError, Policy, _canonical, _unique, require_plain_absolute_path
from cohort_live_transport import CohortLiveTransport
from offline_coordinator import OfflineCoordinator
from selected_path_cohort import SelectedPathCohort


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise LedgerError('invalid arguments')


def publish_results(root, encoded):
    """Publish a flushed private file once; an interrupted temp is harmless."""
    output = require_plain_absolute_path(root / 'selected-path-results.json', allow_missing_leaf=True)
    if output.exists():
        if output.read_bytes() != encoded:
            raise LedgerError('existing results differ')
        return
    fd, name = tempfile.mkstemp(prefix='.selected-path-results-', suffix='.partial', dir=root)
    temporary = Path(name)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        # The ledger only permits Windows fixed disks, where rename does not
        # replace an existing destination. Never overwrite a published result.
        os.rename(temporary, output)
    finally:
        if temporary.exists():
            temporary.unlink()


def main(argv=None):
    parser = Parser(description='Run one frozen selected-path cohort')
    for name in ('private-root', 'input', 'host', 'dll', 'key-environment'):
        parser.add_argument('--' + name, required=True)
    for name in ('budget-nanodollars', 'prior-spend-nanodollars'):
        parser.add_argument('--' + name, required=True, type=int)
    parser.add_argument('--create', action='store_true')
    try:
        args = parser.parse_args(argv)
        root = require_plain_absolute_path(Path(args.private_root))
        source = require_plain_absolute_path(Path(args.input))
        if (not root.is_dir() or not source.is_file()
                or any((p / '.git').exists() for p in (source.parent, *source.parents))):
            raise LedgerError('private input and directory required')
        cases = json.loads(source.read_bytes(), object_pairs_hook=_unique)
        policy = Policy('selected-path-cohort', 10, args.budget_nanodollars, args.prior_spend_nanodollars)
        policy.validate()
        transport = CohortLiveTransport(args.key_environment)
        with CampaignLedger(root / 'ledger.jsonl', create_policy=policy if args.create else None) as ledger:
            if ledger.snapshot()['policy'] != policy.__dict__:
                raise LedgerError('ledger policy changed')
            cohort = SelectedPathCohort(OfflineCoordinator(ledger, root), args.host, args.dll, cases,
                prior_spend_nanodollars=args.prior_spend_nanodollars, live_transport=transport)
            results = cohort.run({})
            publish_results(root, _canonical(results))
        return 0
    except Exception:
        print('Cohort stopped; inspect private evidence.', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
