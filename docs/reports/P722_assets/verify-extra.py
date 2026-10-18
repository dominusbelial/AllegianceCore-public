#!/usr/bin/env python3
"""P722 corrected fixture triplet and receive-registry triplets (no full sweeps)."""
import importlib.util
import sys
sys.dont_write_bytecode = True
from pathlib import Path
spec = importlib.util.spec_from_file_location('verify', Path(__file__).with_name('verify.py'))
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)
v.LEDGER = Path(__file__).with_name('supplemental-results.json')
methods = [
    ('ClientCore.MonoGame', 'Allegiance.ClientCore.MonoGame.Tests.P722InterfaceModelTests.Selection_treats_allies_as_friendly_and_drops_dead_or_departed_targets'),
    ('ClientCore', 'Allegiance.ClientCore.Tests.FedMsgDispatchCoverageTests.EveryExpectedServerSentMessage_IsConsumed_NotUnknownFmid'),
    ('ClientCore', 'Allegiance.ClientCore.Tests.FedMsgDispatchCoverageTests.UnknownFmidDetector_RecordsUnconsumedServerSentMessage'),
    ('ClientCore', 'Allegiance.ClientCore.Tests.FedMsgDispatchCoverageTests.SupportedServerSentSet_IsFullyAccountedFor'),
]
for index, (project, method) in enumerate(methods, 1):
    for repeat in (1, 2, 3):
        v.run(f'supplemental-{index}-{repeat}', v.projects[project], 'FullyQualifiedName=' + method)
