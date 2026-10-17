#!/usr/bin/env python3
"""Complete the 25/3 case's post-timeout triplet after the Core process exits."""
import importlib.util
import json
from pathlib import Path
import time

OUT = Path(__file__).resolve().parent
while 'Final Core pass and any required isolation finished.' not in (OUT / 'finish-progress.log').read_text():
    time.sleep(5)

spec = importlib.util.spec_from_file_location('p738_verify', OUT / 'verify.py')
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)
verify.records = json.loads((OUT / 'verification.json').read_text())
proof = str(OUT / 'research-followup-25-3')
verify.ENV['ALLEGIANCE_P705_PROOF_DIRECTORY'] = proof
record = verify.csharp('P705-25-3-followup', 'ClientCore',
                      'FullyQualifiedName=Allegiance.ClientCore.Tests.P705TechModeLiveTests.'
                      'Research_upgrades_and_resets_through_two_complete_local_matches&DisplayName~civA: 25,')
record['diagnostic_directory'] = proof
(OUT / 'verification.json').write_text(json.dumps(verify.records, indent=2) + '\n')
assert record['exit_code'] == 0 and int(record['counters']['passed']) == 1 and int(record['counters']['total']) == 1, record
print('Standalone 25/3 follow-up passed; three clean isolated case results after its timeout.', flush=True)
