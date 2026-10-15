#!/usr/bin/env python3
"""Keep portable traces for the three repeated pod flight proofs."""
import hashlib, json, pathlib, re, shutil, xml.etree.ElementTree as ET
assets = pathlib.Path(__file__).resolve().parent
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
rows = json.loads((assets / 'affected-results.json').read_text())
roles = {
    'Mcp_pod_flies_two_jumps_to_real_garrison_without_oxygen_death': 'bot',
    'Human_eject_autopilot_flies_two_jumps_and_docks_without_oxygen_respawn': 'human',
    'EjectedPod_routes_through_two_warps_and_docks_at_rescue_garrison_zero_resets': 'human-clock',
}
records = []
for method, role in roles.items():
    source = json.loads((assets / 'human-complete-results.json').read_text()) if role == 'human' else rows
    runs = [row for row in source if row['filter'].endswith('.' + method)]
    assert len(runs) == 3 and all(row['exit'] == 0 and row['passed'] == 1 and not row['failed'] for row in runs), role
    for index, row in enumerate(runs, 1):
        original = pathlib.Path(row['trx'])
        target = assets / f'{role}-{index}.trx'
        shutil.copyfile(original, target)
        tree = ET.parse(target)
        output = '\n'.join(node.text or '' for node in tree.findall('.//t:StdOut', ns))
        lines = list(dict.fromkeys(line.strip() for line in output.splitlines()
            if any(key in line for key in ['MCP route=', 'HUMAN route=', 'POSE accepted=', '[p540b]', '[oxygen]', '[dock]'])))
        records.append({'role': role, 'run': index, 'seconds_wall': row['seconds'],
            'trace': target.name, 'sha256': hashlib.sha256(target.read_bytes()).hexdigest(),
            'oxygen_expiry_log_count': len(re.findall(r'\[oxygen\].*oxygen exhausted', output)),
            'summary': lines})
(assets / 'flight-evidence.json').write_text(json.dumps(records, indent=2) + '\n')
print(f'{len(records)} passing flight traces collected')
