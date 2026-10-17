#!/usr/bin/env python3
"""Qualify the generated case exposed by the third research isolation."""
import importlib.util,json
from pathlib import Path
OUT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('p738_p732_verify',OUT/'verify.py')
verify=importlib.util.module_from_spec(spec);spec.loader.exec_module(verify)
verify.records=json.loads((OUT/'verification.json').read_text())
method='Allegiance.ClientCore.Tests.P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates'
for repeat in range(1,4):
    verify.test(f'post-isolate-P703-generated-{repeat}','ClientCore',
                'FullyQualifiedName='+method+'&DisplayName~civA: 1,')
