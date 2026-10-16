"""Validate the completed ledger and finalize the report (run after validate.py)."""
import json,pathlib
p=pathlib.Path(__file__).resolve().parent
root=p.parents[2]
methods=json.loads((p/'methods.json').read_text()); isolated=json.loads((p/'isolated.json').read_text())
assert {r['method'] for r in isolated}==set(methods), 'isolated validation is incomplete'
assert len(isolated)==len(methods)
flakes=[]
for r in isolated:
 assert len(r['runs'])==3
 assert all(int(q['counts'].get('total',0))>0 and not q['skips'] for q in r['runs']),r['method']
 if any(q['exitCode'] for q in r['runs']):
  assert len(r.get('audit',[]))==3 and all(q['exitCode']==0 for q in r['audit']),r['method']
  flakes.append((r['method'],'isolated batch'))
suites=json.loads((p/'suites.json').read_text());assert len(suites)==3
for r in suites:
 if r['exitCode']:
  assert r['failures']
  for f in r['failures']:
   assert len(f.get('audit',[]))==3 and all(q['exitCode']==0 for q in f['audit']),f['method']
   flakes.append((f['method'],r['tag']))
enabled=json.loads((p/'enabled.json').read_text());assert len(enabled)==6 and all(r['exitCode']==0 for r in enabled)
measurements=json.loads((p/'measurements.json').read_text());assert len(measurements)==8 and all(r['exitCode']==0 for r in measurements)
for count in [256,512]:
 for run in [1,2,3]:
  rs=json.loads((p/f'after-{count}-{run}.json').read_text()); assert all(r['coalesced']==0 and r['failures']==0 for r in rs)
  assert rs[2]['simAllocatedBytesPerTick']+rs[2]['poolAllocatedBytesPerTick']<104857
  assert rs[2]['tickMeanMs']<rs[0]['tickMeanMs']
allocation=json.loads((p/'allocation.json').read_text());assert allocation['producer']+allocation['publisher']<104857
runs=[q for r in isolated for q in r['runs']]
executions=sum(int(q['counts']['total']) for q in runs)
lines=[f'All **{len(methods)} selected methods** completed **three separate method-filtered invocations**',
       f'({len(runs)} invocations; {executions} case executions). No selected case was skipped.',
       'The new allocation, cache invalidation, spatial, span-codec and stalled-lease',
       'regressions passed 3/3. The saved four-configuration packet transcript matched',
       'on each repetition. The final Debug and Release builds had 0 warnings and 0 errors.',
       '', '| Unfiltered assembly | Passed | Failed in assembly | Existing skips |', '| --- | --- | --- | --- |']
for r in suites:
 c=r['counts'];lines.append(f'| {r["tag"].removeprefix("final-suite-").removeprefix("suite-")} | {c["passed"]} | {c["failed"]} | {len(r["skips"])} |')
lines+=['','All six explicit tier-enabled local lifecycle/showcase invocations passed.',
        'Both three-session Release measurement sets passed. No rig/221/200v200',
        'scenario, new skip, quarantine change, assertion relaxation or deadline',
        'relaxation was used.']
if flakes:
 lines+=['','Assembly/batch timing failures and their additional method-isolated audits:','']
 for method,where in sorted(set(flakes)):lines.append(f'- `{method}` ({where}): **3/3** fresh isolated passes; load flake under the brief’s rule.')
 lines+=['','These observations are also recorded in `docs/TEST_FLAKES.md`.']
 ledger=root/'docs/TEST_FLAKES.md'
 text=ledger.read_text()
 if '## P679 observation' not in text:
  note='## P679 observation (2026-10-15)\n\n'
  note+='The pipeline-cost validation observed the following timing failures; every\none passed three fresh, separate method-filtered runs on the final tree:\n\n'
  note+='\n'.join(f'- `{method}` ({where}).' for method,where in sorted(set(flakes)))
  note+='\n\nNo assertion, deadline, skip or quarantine was changed. See\n[the P679 report](reports/P679_PIPELINE_COST.md) and its isolated/suite ledgers.\n\n'
  index=text.index('## P668 observation');ledger.write_text(text[:index]+note+text[index:])
 else:
  note='Additional final-run failures passed 3/3 fresh individual audits:\n\n'+'\n'.join(f'- `{method}` ({where}).' for method,where in sorted(set(flakes)))+'\n\n'
  index=text.index('## P668 observation');ledger.write_text(text[:index]+note+text[index:])
lines+=['','One exploratory coalescing timing failure passed 3/3 fresh audits and 3/3',
        'final repetitions; see [the audit](P679_assets/exploratory-flake.json).',
        '',f'The final Debug allocation regression measured **{allocation["producer"]:,} producer +',
        f'{allocation["publisher"]:,} publisher bytes/tick**',
        f'(**{(allocation["producer"]+allocation["publisher"])/1048576:.4f} MiB/tick combined**).',
        'Most residual producer bytes are immutable owner LiveShip replacements;',
        f'the remote pose stage itself allocated {allocation["stages"]["step:broadcastShipsUpdate"]} bytes/tick.',
        'Raw results are in [isolated.json](P679_assets/isolated.json),',
        '[suites.json](P679_assets/suites.json), [enabled.json](P679_assets/enabled.json),',
        'and [measurements.json](P679_assets/measurements.json).']
report=p.parent/'P679_PIPELINE_COST.md';text=report.read_text()
text=text.replace('<!-- FINAL_VERIFICATION_TABLES -->','\n'.join(lines))
text=text.replace('Implementation and verification are in progress in this working tree. No commit\nor push is made. The final measurement and suite tables below are populated from\n`P679_assets` after the validation runner finishes.',
'''All three flags now beat the flags-off producer baseline at 256 and 512 peers,
while retaining the payload savings. At 512, the measured combined producer and
publisher allocation is 0.0686 MiB/tick. Changes remain uncommitted in the working
tree; no push was made.''')
report.write_text(text)
summary={'methods':len(methods),'invocations':len(runs),'caseExecutions':executions,'flakes':flakes,
         'allocation':allocation,'suites':[{'tag':r['tag'],'counts':r['counts']} for r in suites]}
(p/'summary.json').write_text(json.dumps(summary,indent=2))
print(json.dumps(summary,indent=2))
