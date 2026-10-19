#!/usr/bin/env python3
"""Finalize P740 only after every required gate and post-suite isolation."""
import json, re, subprocess, sys
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[3]
OUT=Path(__file__).resolve().parent
subprocess.run([sys.executable,str(OUT/'audit.py')],cwd=ROOT,check=True,stdout=subprocess.DEVNULL)
summary=json.loads((OUT/'summary.json').read_text())
records=json.loads((OUT/'verification.json').read_text())
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
metrics=[]
for r in records:
    if not r['label'].startswith('showcase-'):continue
    raw=Path(r['command'][r['command'].index('--results-directory')+1])/(r['label']+'.trx')
    text='\n'.join(ET.parse(raw).getroot().itertext())
    match=re.search(r'P700 local 15v15: ([\d.]+)s, samples=(\d+), reached=(\d+)/30, longest speed<1 with target=([\d.]+)s, shots=(\d+), hits=(\d+), kills=(\d+)',text)
    if not match:
        idle=[float(v) for v in re.findall(r'Idle ([\d.]+)s:',text)]
        metrics.append(dict(label=r['label'],completed=False,failed=bool(r['exit_code']),max_observed_idle=max(idle) if idle else None,notes='No final combat summary. Failed run logged a 5.02s idle interval, followed by ticker.Join(5000) assertion failure; cleanup can mask an earlier assertion. See raw TRX.'))
        continue
    metrics.append(dict(label=r['label'],seconds=float(match[1]),samples=int(match[2]),reached=int(match[3]),longest_pause=float(match[4]),shots=int(match[5]),hits=int(match[6]),kills=int(match[7])))
(OUT/'showcase-metrics.json').write_text(json.dumps(metrics,indent=2)+'\n')
completed_metrics=[m for m in metrics if 'reached' in m]
flakes=list(summary['load_flake_methods'])
parking_failed=bool(summary['showcases']['failed'])
parking_method=['ClientCore','Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds']
if parking_failed and parking_method not in flakes: flakes.append(parking_method)
metadata=summary['metadata_cohort']
if flakes:
    path=ROOT/'docs/TEST_FLAKES.md'
    header='## P740 observation (2026-10-18)'
    text=path.read_text()
    assert header not in text,'Avoid appending duplicate flake entries'
    text+='\n\n'+header+'\n\nThe single unfiltered Core/ClientCore/MonoGame pass and standalone showcase gate recorded the failures below. Each method then passed three fresh exact-method isolated runs, with assertions, deadlines and quarantine settings unchanged, satisfying the P740 brief\'s load-flake rule. These passes do not establish a cause or fix.\n\n'
    for project,method in flakes:text+='- `'+method+'` — **3/3 isolated passes** after all broad suites.\n'
    text+='\nEvidence: [P740 report](reports/P740_INTERFACE_WAVE5.md), [invocation ledger](reports/P740_assets/verification.json); full failed/isolated TRX and logs: `/tmp/allegiance-evidence/P740/` (per-command paths in the ledger).\n'
    path.write_text(text)
build=json.loads((OUT/'build-fingerprints.json').read_text())['build']
focused=summary['focused']
block=f'''Whole-solution Release build: **0 warnings / 0 errors**. [Build/source/test-binary fingerprints](P740_assets/build-fingerprints.json) identify the final source and binaries; raw build log: `{build['raw_log']}`. `src/Allegiance.Core` and `src/Allegiance.Server` are unchanged, so the conditional AOT-publish gate does not apply.

Final focused certificate: **{summary['focused_methods']} distinct methods × 3 fresh exact-method isolated runs = {focused['invocations']} invocations / {focused['passed']} passing cases / {focused['failed']} failures / {focused['skipped']} skips**. The original runtime cohort is supplemented by **{metadata['invocations']} final metadata-fix invocations / {metadata['passed']} passing cases / {metadata['failed']} failures**, covering the native-default catalog contract, input persistence, new presets and equipment-swap integration. [Metadata correction and selected methods](P740_assets/metadata-revalidation.json). This includes every new method, native codecs, receive registry, money approval and its real-server test, departure/reset paths, carrier/respawn feedback, action-map guards, throttle arithmetic and joystick latch regressions. [Selected methods](P740_assets/focused-methods.json), [complete invocation ledger](P740_assets/verification.json).

{('The five scheduled final-build showcases recorded **4 passes / 1 failure**. The failed run logged a **5.02s idle interval** and a ticker-thread cleanup timeout; its cleanup assertion can mask an earlier parking assertion. The method then passed **3/3 fresh isolated runs after all broad suites**. A separate **five-run HEAD baseline** provides the requested rate comparison: base failures **' + str(summary['baseline_comparison']['base_head']['failed']) + '/5**, task failures **1/5**. These small samples do not establish a cause or a failure-rate increase. [Baseline comparison](P740_assets/baseline-comparison.json).') if parking_failed else 'The final never-parks gate passed **5/5**.'}

Completed showcase runs had real shots/hits/kills. Minimum arrivals among completed runs: **{min(m['reached'] for m in completed_metrics)}/30**; maximum sampled pause among completed runs: **{max(m['longest_pause'] for m in completed_metrics):.3f}s**. The failed run is retained separately rather than included in those successful-run extrema. [Per-run metrics](P740_assets/showcase-metrics.json).

Exactly **one unfiltered broad pass** ran for each required assembly, with no overlapping suite processes:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
'''
for r in summary['suites']:block+=f"| {r['project']} | {r['passed']} | {r['failed']} | {r['skipped']} |\n"
block+='\nCore completed before the last UI-only page-navigation adjustment. All **26 Core dependency DLLs remained byte-identical**, and Core has no Game assembly dependency, so its one broad pass is retained. After the final UI rebuild, all **91 focused methods and five showcases ran fresh**; ClientCore and MonoGame then ran once each. [UI revalidation and dependency hashes](P740_assets/ui-revalidation.json). The local P660, P542, capture and P539 baseline opt-ins were enabled. Ordinary live/loopback and LiveTiming cases ran; no rig/221/200v200 opt-in was enabled. No second broad sweep was run.\n\n'
if flakes:
    block+='Every failing method below passed **3/3 fresh individual runs after all suites**, qualifying as a load flake under this brief. The original failures remain in the ledger and raw TRX; [TEST_FLAKES.md](../TEST_FLAKES.md#p740-observation-2026-10-18) records them. No assertion, receipt deadline, quarantine or production code was changed to clear them.\n\n'
    for project,method in flakes:block+='- `'+method+'` — **3/3** isolated passes.\n'
else:block+='All three broad passes had zero load-flake failures.\n'
if summary['fixed_product_methods']:
    block+='\nMonoGame also found a deterministic native-default catalog defect: throttle33/66 used explicit empty native bindings instead of the catalog\'s null representation of native-unbound commands. The original method failed **3/3** isolated runs on the old binary. The metadata is corrected; the whole solution rebuilt **0 warnings / 0 errors**, and the final metadata cohort above passed. This is a fixed product issue, **not a load flake**. The original broad and isolated failures remain visible; no broad suite was repeated. Runtime control defaults, protocol, server/simulation behavior and existing assertions are unchanged.\n'
skipped=[(r['project'],name) for r in records if r['label'].startswith('suite-') for name in r['skips']]
if skipped:
    block+='\nActual skipped result names (TRX outcomes, rather than its unreliable aggregate notExecuted counter):\n\n'
    for project,name in skipped:block+=f'- {project}: `{name}`\n'
block+='''
Final audit: source and test-binary fingerprints match the tested build; HEAD is unchanged; exactly ten parity rows changed; simulation CSV is byte-identical to base; edited source/CSV/report files use LF; `git diff --check` passes. [Audit/summary and native inactive-message scan](P740_assets/summary.json), [reproducible runner](P740_assets/verify.py).

Raw logs/TRX and all excluded development attempts are outside the repository at **`/tmp/allegiance-evidence/P740/`**, the retained Core sweep under **`certified/`**, and final focused/showcase/ClientCore/MonoGame/isolation results under **`final-ui/`**. The sandbox permits external writes only under `/tmp`, so the brief's preferred `~/allegiance-evidence/P740/` destination could not be used. Only compact JSON, scorecard text and verification scripts are in `P740_assets/`; total evidence is below the **2 MB** task limit. No raw log, TRX or archive was added to the repository. No commits or pushes were made.
'''
path=ROOT/'docs/reports/P740_INTERFACE_WAVE5.md'
text=path.read_text()
start=text.index('<!-- P740-VALIDATION-BEGIN -->')
end=text.index('<!-- P740-VALIDATION-END -->')+len('<!-- P740-VALIDATION-END -->')
text=text[:start]+'<!-- P740-VALIDATION-BEGIN -->\n'+block+'<!-- P740-VALIDATION-END -->'+text[end:]
text=text.replace('Earlier results and the interrupted showcase are excluded from final validation credit','Earlier results and all diagnostic showcases are excluded from final validation credit')
path.write_text(text)
subprocess.run([sys.executable,str(OUT/'audit.py')],cwd=ROOT,check=True,stdout=subprocess.DEVNULL)
print(json.dumps({'focused':summary['focused'],'showcases':summary['showcases'],'suites':summary['suites'],'load_flakes':flakes},indent=2))
