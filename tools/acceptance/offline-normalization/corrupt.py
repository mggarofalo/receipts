"""Make isolated negative-test copies. Never edits the accepted input artifact."""
import json
import pathlib
import shutil
import sqlite3
import sys
import uuid

source, destination, case = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3]
if destination.exists() or source.resolve() == destination.resolve():
    raise ValueError('A new, separate corruption destination is required')
shutil.copyfile(source, destination)
with sqlite3.connect(destination) as db:
    def mutate(kind, edit):
        row = db.execute('select id,payload from normalization_catalog where kind=? order by id limit 1', (kind,)).fetchone()
        if row is None:
            raise ValueError('Required corruption prerequisite absent: ' + kind)
        payload = json.loads(row[1])
        edit(payload)
        db.execute('update normalization_catalog set payload=? where kind=? and id=?', (json.dumps(payload), kind, row[0]))
    if case == 'dangling-mapping':
        mutate('mapping', lambda p: p.update(TargetId=str(uuid.uuid4())))
    elif case == 'mapping-fingerprint':
        mutate('mapping', lambda p: p.update(SourceHash='0' * 64))
    elif case == 'mapping-revision':
        # Change the mapping revision while retaining every dependent evidence record.
        rows = db.execute("select payload from normalization_catalog where kind='review_evidence'").fetchall()
        revisions = {json.loads(r[0]).get('MappingRevision') for r in rows} - {None}
        rows = db.execute("select id,payload from normalization_catalog where kind='mapping'").fetchall()
        selected = [(i, json.loads(p)) for i, p in rows if json.loads(p)['Revision'] in revisions]
        if not selected:
            raise ValueError('No mapping-dependent review evidence: revision corruption is not exercised')
        identifier, payload = selected[0]
        payload['Revision'] = str(uuid.uuid4())
        db.execute("update normalization_catalog set payload=? where kind='mapping' and id=?", (json.dumps(payload), identifier))
    elif case == 'review-fingerprint':
        row = db.execute("select id,payload from normalization_catalog where kind='review_evidence' and json_extract(payload,'$.SourceHash') is not null order by id limit 1").fetchone()
        if row is None:
            raise ValueError('No reviewed source evidence')
        payload = json.loads(row[1]); payload['SourceHash'] = '0' * 64
        db.execute("update normalization_catalog set payload=? where kind='review_evidence' and id=?", (json.dumps(payload), row[0]))
    elif case == 'unsupported-evidence':
        db.execute('insert into normalization_catalog values(?,?,?)', ('unsupported_acceptance_evidence', str(uuid.uuid4()), '{}'))
    elif case == 'factual-tamper':
        db.execute('update receipt_items set unit_price=unit_price+1 where id=(select id from receipt_items order by id limit 1)')
    else:
        raise ValueError('Unknown corruption case')
