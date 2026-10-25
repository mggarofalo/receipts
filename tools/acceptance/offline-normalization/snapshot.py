"""Read-only portable evidence. Never repairs or normalizes its input."""
import json
import pathlib
import sqlite3
import sys

source = pathlib.Path(sys.argv[1]).resolve()
with sqlite3.connect(source.as_uri() + '?mode=ro', uri=True) as db:
    db.row_factory = sqlite3.Row
    integrity = [row[0] for row in db.execute('pragma integrity_check')]
    if integrity != ['ok']:
        raise ValueError('SQLite integrity check failed')
    if list(db.execute('pragma foreign_key_check')):
        raise ValueError('SQLite foreign key check failed')
    names = [r[0] for r in db.execute("select name from sqlite_master where type='table' and name not like 'sqlite_%' order by name")]
    tables = {}
    for name in names:
        quoted = '"' + name.replace('"', '""') + '"'
        rows = [dict(r) for r in db.execute('select * from ' + quoted)]
        tables[name] = sorted(rows, key=lambda r: json.dumps(r, sort_keys=True))
    # Exhaustively preserve every non-normalization table, including unknown/new tables.
    normalization_tables = {'backup_metadata', 'normalization_catalog', 'normalized_descriptions', 'normalized_description_settings'}
    facts = {}
    for name, rows in tables.items():
        if name in normalization_tables:
            continue
        facts[name] = [{k: v for k, v in r.items() if not k.startswith('normalization_') and not k.startswith('normalized_description_')} for r in rows]
        facts[name].sort(key=lambda r: json.dumps(r, sort_keys=True))
    # Metadata timestamps are export-specific; version/format remain evidence below.
    tables.pop('backup_metadata', None)
    metadata = dict(db.execute('select key,value from backup_metadata'))
    print(json.dumps({'integrity': integrity, 'metadata': metadata, 'tables': tables, 'facts': facts}, sort_keys=True))
