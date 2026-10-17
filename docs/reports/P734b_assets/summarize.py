#!/usr/bin/env python3
"""Audit completed P734b evidence without invoking Git or mutating merge state."""
import gzip,hashlib,json,re
from pathlib import Path
import xml.etree.ElementTree as ET
OUT=Path(__file__).resolve().parent
ROOT=OUT.parents[2]
rows=json.loads((OUT/'verification.json').read_text())
cohort=json.loads((OUT/'cohort.json').read_text())
focus=[r for r in rows if r['name'].startswith('focus-')]
parking=[r for r in rows if r['name'].startswith('parking-')]
suites=[r for r in rows if r['name'].startswith('suite-') and not r['name'].startswith('suite-retry-')]
retries=[r for r in rows if r['name'].startswith('suite-retry-')]
def complete(r):
    c=r.get('counters',{})
    return (bool(c) and int(c['total'])>0
        and all(int(c.get(k,0))==0 for k in ['error','timeout','aborted','notRunnable'])
        and int(c['passed'])+int(c['failed'])+len(r.get('skipped',[]))==int(c['total'])
        and r['exit'] in (0,1) and (r['exit']==0)==(int(c['failed'])==0))
valid=lambda r:complete(r) and r['exit']==0 and int(r['counters']['executed'])>0 and not r.get('skipped')
triplets=all(len(group:=[r for r in focus if r['project']==p and r['method']==m])==3 and all(map(valid,group)) for p,m in cohort)
failures={(r['project'],f['method']) for r in suites+focus for f in r.get('failures',[])}
recovered=all(len(group:=[r for r in retries if r['project']==p and r['method']==m])==3 and all(map(valid,group)) for p,m in failures)
showcase=json.loads((OUT/'showcase.json').read_text())
manifest=json.loads((OUT/'source-manifest.json').read_text())
source_ok=all((ROOT/p).is_file() and hashlib.sha256((ROOT/p).read_bytes()).hexdigest()==h for p,h in manifest.items())
matrix=json.loads((OUT/'matrix-audit.json').read_text())
matrix_path=ROOT/'docs/parity/sim-rules-matrix.csv'
matrix_ok=(hashlib.sha256(matrix_path.read_bytes()).hexdigest()==matrix['sha256']
    and b'\r' not in matrix_path.read_bytes() and matrix['all_other_master_rows_preserved']
    and matrix['periodic_rows_unchanged'] and all(r['status']=='implemented' for r in matrix['P708_rows']))
native=json.loads((OUT/'native-manifest.json').read_text())
native_ok=all(hashlib.sha256((Path('/home/domingo/Development/Allegiance/src')/p).read_bytes()).hexdigest()==h for p,h in native.items())
build=(OUT/'build.log').read_text()
build_ok=bool(re.search(r'^\s*0 Warning\(s\)\s*$',build,re.M) and re.search(r'^\s*0 Error\(s\)\s*$',build,re.M))
driver_manifest=json.loads((OUT/'showcase-source-manifest.json').read_text())
driver_ok=all(hashlib.sha256((OUT/p).read_bytes()).hexdigest()==h for p,h in driver_manifest.items())
with gzip.open(OUT/'showcase-run.log.gz','rt') as log:
    showcase_exit_ok='Showcase process exit=0' in log.read()
with gzip.open(OUT/'suite-ClientCore.MonoGame.trx.gz','rb') as log:
    mono_tree=ET.fromstring(log.read())
soak=next(r for r in mono_tree.findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
    if r.attrib['testName'].endswith('.Human_flies_for_ten_minutes_with_turns_boost_tidi_and_recovers_from_correction'))
soak_info={k:soak.attrib[k] for k in ['testName','outcome','duration']}
h,m,s=soak_info['duration'].split(':')
soak_ok=soak_info['outcome']=='Passed' and int(h)*3600+int(m)*60+float(s)>=600
initial=json.loads((OUT/'merge-metadata-before.json').read_text())
meta=Path((ROOT/'.git').read_text().strip().split(': ',1)[1]);common=(meta/(meta/'commondir').read_text().strip()).resolve()
current={p:hashlib.sha256((meta/p).read_bytes()).hexdigest() for p in ['HEAD','index','MERGE_HEAD','ORIG_HEAD']}
current['branch_ref']=(common/'refs/heads/codex/p734b').read_text().strip()
(OUT/'merge-metadata-after.json').write_text(json.dumps(current,indent=2)+'\n')
markers=any(re.search(r'^(<<<<<<< |=======\s*$|>>>>>>> )', (ROOT/p).read_text(), re.M) for p in ['src/Allegiance.ClientCore/State/ClientWorld.cs','docs/TEST_FLAKES.md'])
summary=dict(focused_methods=len(cohort),focused_invocations=len(focus),focused_passes=sum(int(r['counters']['passed']) for r in focus),exact_triplets_passed=triplets,
 parking_runs=len(parking),parking_clean=len(parking)==5 and all(map(valid,parking)),
 suites=[dict(project=r['project'],passed=int(r['counters']['passed']),failed=int(r['counters']['failed']),skipped=r['skipped'],seconds=r['seconds']) for r in suites],
 one_complete_sweep=len(suites)==4 and {r['project'] for r in suites}=={'Core','ClientCore','ClientCore.MonoGame','Lobby'} and all(complete(r) and r['method'] is None for r in suites),
 suite_failures_recovered=recovered,failed_methods=[dict(project=p,method=m) for p,m in sorted(failures)],
 showcase=dict(techMode=showcase['techMode'],civs=showcase['civs'],stageOverCount=showcase['stageOverCount'],simFailures=showcase['simFailures'],ends=[x for x in showcase['timeline'] if x['kind']=='Stage Over']),
 source_fingerprints_match=source_ok,native_fingerprints_match=native_ok,whole_solution_build_0_0=build_ok,
 showcase_driver_fingerprints_match=driver_ok,showcase_process_exit_zero=showcase_exit_ok,
 human_flight_soak=soak_info,human_flight_soak_passed=soak_ok,
 matrix_final_check=matrix_ok,merge_metadata_unchanged=initial==current,no_conflict_markers=not markers,matrix=matrix)
allowed_skips={'Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_built_content_directory_exists','Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_has_the_full_meshy_set','Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_merge_yields_identical_resolution_to_before'}
summary['only_optional_skips']=all(s in allowed_skips for r in suites for s in r['skipped'])
summary['clean']=all([triplets,summary['parking_clean'],summary['one_complete_sweep'],recovered,source_ok,native_ok,build_ok,driver_ok,showcase_exit_ok,soak_ok,matrix_ok,initial==current,not markers,summary['only_optional_skips'],showcase['stageOverCount']>=2,showcase['simFailures']==0])
(OUT/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print(json.dumps({k:v for k,v in summary.items() if k not in ['matrix','showcase']},indent=2))
if not summary['clean']:raise SystemExit('Evidence audit is not clean')
