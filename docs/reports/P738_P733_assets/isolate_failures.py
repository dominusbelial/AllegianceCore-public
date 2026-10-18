"""Run each observed failing method in three fresh, separate invocations."""
import json, xml.etree.ElementTree as ET
from verify import OUT, records, test

failures = {}
for row in records:
    if row['label'].startswith('isolate-'): continue
    for failure in row.get('failures', []):
        assembly = next(arg for arg in row['command'] if arg.endswith('.Tests.dll'))
        project = assembly.rsplit('/', 1)[1][len('Allegiance.'):-len('.Tests.dll')]
        failures[(project, failure['method'], failure['name'])] = failure['name']

qualification = []
def case_outcome(row, name):
    path = OUT / (row['label'] + '.trx')
    if not path.exists(): return None
    doc = ET.parse(path)
    return next((r.attrib['outcome'] for r in doc.findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
                 if r.attrib['testName'] == name), None)

for index, ((project, method, name), _) in enumerate(sorted(failures.items())):
    prior = [r for r in records if r['label'].startswith(('early-isolate-', 'isolate-'))
             and any(arg.startswith('FullyQualifiedName=' + method) for arg in r['command'])
             and case_outcome(r, name) is not None][-3:]
    reruns = prior if len(prior) == 3 and all(r['exit_code'] == 0 and case_outcome(r, name) == 'Passed' for r in prior) else [
        test(f'isolate-{index:03}-{repeat}', project, 'FullyQualifiedName=' + method) for repeat in range(1, 4)]
    qualification.append(dict(project=project, method=method, original_case=name,
                              repetitions=[r['label'] for r in reruns],
                              three_clean_passes=all(r['exit_code'] == 0 and int(r.get('counters', {}).get('passed', 0)) > 0
                                                    and not r.get('skips') for r in reruns)))
(OUT / 'flake-qualification.json').write_text(json.dumps(qualification, indent=2) + '\n')
print(json.dumps(qualification, indent=2))
