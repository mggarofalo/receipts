"""Explicit offline executable bridge; no environment/credential discovery.

Inventories establish reviewed file identity, not reproducibility or provenance.
Only the paired runner's offline prepare/replay commands are exposed.
"""
import hashlib
from pathlib import Path
import subprocess

from campaign_ledger import LedgerError, _canonical, require_plain_absolute_path
from guarded_exchange import _parse, verify_artifacts


class FrozenRunner:
    def __init__(self, roots, artifacts, *, host, assembly):
        self.roots = _parse(_canonical(roots))
        self.artifacts = _parse(_canonical(artifacts))
        self.host, self.assembly = str(host), str(assembly)
        if set(self.roots) != {'source', 'executable', 'dependency'} or set(self.artifacts) != set(self.roots):
            raise LedgerError('source, publish and runtime dependency inventories required')
        if (Path(self.host).name.lower() != 'dotnet.exe'
                or Path(self.assembly).name != 'Receipts.JevPairedRunner.dll'
                or self.host not in self.artifacts['dependency']
                or self.assembly not in self.artifacts['executable']):
            raise LedgerError('explicit reviewed dotnet host and paired assembly required')
        self.verify()

    def verify(self):
        for kind, root_name in self.roots.items():
            root = require_plain_absolute_path(root_name)
            if not root.is_dir():
                raise LedgerError('frozen inventory root directory required')
            found = set()
            for path in root.rglob('*'):
                require_plain_absolute_path(path)
                if path.is_file():
                    found.add(str(path))
            if found != set(self.artifacts[kind]):
                raise LedgerError('frozen inventory membership drift')
        return verify_artifacts(self.artifacts)

    def run(self, command, input_path, replies_path, output_path):
        if command not in ('prepare', 'replay'):
            raise LedgerError('offline command required')
        self.verify()
        inputs = [require_plain_absolute_path(p) for p in (input_path, replies_path)]
        output = require_plain_absolute_path(output_path, allow_missing_leaf=True)
        if output.exists() or any((p / '.git').exists() for p in (output.parent, *output.parents)):
            raise LedgerError('new private output outside checkout required')
        before = [hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs]
        # No shell, PATH lookup, build, restore, environment inheritance, or CLI
        # argument supplied by data. stdout/stderr are discarded, never logged.
        process = subprocess.run([self.host, self.assembly, command, *map(str, inputs), str(output)],
                                 cwd=str(Path(self.assembly).parent), env={},
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL, timeout=60, check=False,
                                 creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        self.verify()
        if process.returncode != 0 or before != [hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs]:
            raise LedgerError('offline runner failed or input drifted')
        require_plain_absolute_path(output)
        if not 0 < output.stat().st_size <= 8 * 1024 * 1024:
            raise LedgerError('offline output size invalid')
        return _parse(output.read_bytes())
