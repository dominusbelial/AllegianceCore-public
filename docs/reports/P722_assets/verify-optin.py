#!/usr/bin/env python3
"""Run the local P660 opt-in proof three times; never enable rig/221 opt-ins."""
import importlib.util
import os
import sys
from pathlib import Path
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('verify', Path(__file__).with_name('verify.py'))
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)
v.LEDGER = Path(__file__).with_name('optin-results.json')
os.environ['ALLEGIANCE_LIVE_P660_SHOWCASE'] = '1'
method = 'Allegiance.ClientCore.Tests.P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms'
for repeat in (1, 2, 3):
    v.run(f'optin-p660-{repeat}', v.projects['ClientCore'], 'FullyQualifiedName=' + method)
