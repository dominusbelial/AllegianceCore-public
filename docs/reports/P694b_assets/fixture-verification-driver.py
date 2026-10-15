import pathlib,json
source=pathlib.Path(__file__).with_name('verification-driver.py').read_text().split('# Start with the numeric')[0]
source=source.replace("item={'label':label", "item={'fixturePreparation':'fixed 5 seconds and at least 10 fresh worlds','label':label")
exec(source)
ledger=json.load(open(ART/'verification.json'))
method='Allegiance.Core.Tests.P694MassEventTickTests.Launch_point_mass_events'
for count in [150,195]:
 exact=method+f'&DisplayName~count: {count}&DisplayName~parallel: False&DisplayName~stationDeath: True&DisplayName~coincident: False&DisplayName~nativeLaunch: False'
 for attempt in range(1,4):run(f'warm5-case-Debug-{count}-{attempt}','Allegiance.Core.Tests','Debug',exact)
for attempt in range(1,4):run(f'warm5-matrix-Release-{attempt}','Allegiance.Core.Tests','Release',method)
run('suite-Allegiance.Core.Tests-final','Allegiance.Core.Tests','Debug')
failures=sorted({(r['project'],r['configuration'],x['method']) for r in ledger['runs'] if r['label'].startswith('suite-') for x in r['nonpassing'] if x['outcome']=='Failed'})
for index,(project,config,method) in enumerate(failures):
 for attempt in range(1,4):run(f'failure-audit-postwarm-{index:03}-{attempt}',project,config,method)
print('FINAL FIXTURE VERIFICATION DONE',flush=True)
