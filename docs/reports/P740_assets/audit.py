#!/usr/bin/env python3
"""P740 source/evidence/row and final gate audit; read-only git commands."""
import csv, hashlib, io, json, re, subprocess
from pathlib import Path
ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
RAW = Path('/tmp/allegiance-evidence/P740')
def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def main():
    fingerprints = json.loads((OUT / 'build-fingerprints.json').read_text())
    mismatches = [name for key in ('sources', 'test_binaries') for name, expected in fingerprints[key].items()
                  if digest(ROOT / name) != expected]
    assert not mismatches, mismatches
    ui = json.loads((OUT / 'ui-revalidation.json').read_text())
    assert all(digest(ROOT / name) == expected for name, expected in ui['core_dependencies_byte_identical'].items())
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    assert head == fingerprints['head']
    modified = subprocess.check_output(['git', 'diff', '--name-only'], cwd=ROOT, text=True).splitlines()
    assert not any(name.startswith(('src/Allegiance.Core/', 'src/Allegiance.Server/')) for name in modified)
    subprocess.run(['git', 'diff', '--check'], cwd=ROOT, check=True)
    before = list(csv.DictReader(io.StringIO(subprocess.check_output(['git', 'show', 'HEAD:docs/parity/parity-matrix.csv'], cwd=ROOT, text=True))))
    after = list(csv.DictReader((ROOT / 'docs/parity/parity-matrix.csv').open()))
    changed = [dict(native_item=a['native_item'], before=b['port_status'], after=a['port_status'], weight=a['weight'])
               for a, b in zip(after, before) if a != b]
    assert len(changed) == 10
    assert sum(r['after'] == 'done' for r in changed) == 8
    assert sum(r['after'] == 'na' for r in changed) == 2
    sim = subprocess.check_output(['git', 'show', 'HEAD:docs/parity/sim-rules-matrix.csv'], cwd=ROOT)
    assert sim == (ROOT / 'docs/parity/sim-rules-matrix.csv').read_bytes()
    lf_files = ['docs/parity/parity-matrix.csv', 'docs/parity/fedmsg-wiring.csv']
    lf_files += [line[3:] for line in subprocess.check_output(['git', 'status', '--short', '--untracked-files=all'], cwd=ROOT, text=True).splitlines()
                 if line[3:].endswith(('.cs', '.py', '.md')) and not line[3:].startswith('P740_BRIEF')]
    assert all(b'\r' not in (ROOT / name).read_bytes() for name in lf_files)
    records = json.loads((OUT / 'verification.json').read_text())
    methods = json.loads((OUT / 'focused-methods.json').read_text())
    focused = [r for r in records if r['label'].startswith('focused-')]
    assert len(focused) == len(methods) * 3
    assert all(r['exit_code'] == 0 and int(r['counters']['passed']) > 0 and not r['skips'] for r in focused)
    for r in records:
        assert digest(Path(r['command'][r['command'].index('--results-directory')+1]) / (r['label']+'.trx')) == r['trx_sha256']
    showcases = [r for r in records if r['label'].startswith('showcase-')]
    assert len(showcases) == 5
    suites = [r for r in records if r['label'].startswith('suite-')]
    assert len(suites) == 3 and len({r['project'] for r in suites}) == 3
    assert all('--filter' not in r['command'] for r in suites)
    parking_retry = [r for r in records if r['label'].startswith('parking-retry-')] or [r for r in records if r['label'].startswith('isolation-') and r['command'][-1].endswith('P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds')]
    if any(r['exit_code'] for r in showcases):
        assert len(parking_retry) == 3 and all(r['exit_code'] == 0 and int(r['counters']['passed']) > 0 for r in parking_retry)
        comparison = json.loads((OUT / 'baseline-comparison.json').read_text())
        assert comparison['base_head']['runs'] == 5
    else: comparison = None
    metadata = json.loads((OUT / 'metadata-revalidation.json').read_text())
    metadata_records = [r for r in records if r['label'].startswith('metadata-')]
    assert len(metadata_records) == len(metadata['methods']) * 3
    assert all(r['exit_code'] == 0 and int(r['counters']['passed']) > 0 for r in metadata_records)
    failed = sorted({(r['project'], f['method']) for r in suites for f in r.get('failures', [])})
    isolation = [r for r in records if r['label'].startswith('isolation-')]
    for index, (project, method) in enumerate(failed):
        retries = ([r for r in metadata_records if r['command'][-1] == 'FullyQualifiedName=' + method]
                   if method == metadata['fixed_method'] else [r for r in isolation if r['label'].startswith(f'isolation-{index:02}-')])
        assert len(retries) == 3 and all(r['exit_code'] == 0 and int(r['counters']['passed']) > 0 for r in retries), method
    totals = lambda group: dict(invocations=len(group), passed=sum(int(r['counters']['passed']) for r in group),
        failed=sum(int(r['counters']['failed']) for r in group), skipped=sum(len(r['skips']) for r in group))
    native = Path('/home/domingo/Development/Allegiance/src')
    scan = {}
    for token in ('NEW_SHIP_INSTANCE', 'TREASURE_SETS'):
        matches=[]; count=0
        for directory in ('FedSrv','clintlib','WinTrek'):
            for path in (native / directory).rglob('*'):
                if path.suffix.lower() not in ('.cpp','.h'): continue
                count+=1
                for number,line in enumerate(path.read_text(errors='replace').splitlines(),1):
                    if token in line:matches.append(dict(file=str(path.relative_to(native)),line=number,text=line.strip()))
        scan[token]=dict(files_scanned=count,matches=matches)
    size = sum(p.stat().st_size for p in OUT.rglob('*') if p.is_file())
    assert size < 2_000_000
    summary = dict(head=head, source_and_test_binaries_unchanged=True, simulation_byte_identical=True,
        lf=True, diff_check=True, changed_rows=changed, focused_methods=len(methods), focused=totals(focused),
        showcases=totals(showcases), showcase_isolation=totals(parking_retry), baseline_comparison=comparison, suites=[dict(project=r['project'],**totals([r])) for r in suites],
        post_suite_isolation=totals(isolation), load_flake_methods=[m for m in failed if m[1] != metadata['fixed_method']], fixed_product_methods=[m for m in failed if m[1] == metadata['fixed_method']], metadata_cohort=totals(metadata_records), native_inventory_scan=scan,
        raw_evidence=str(RAW), evidence_bytes=size, aot='Not required: src/Allegiance.Core and src/Allegiance.Server unchanged')
    (OUT / 'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
    print(json.dumps(summary,indent=2))
if __name__=='__main__':main()
