#!/usr/bin/env python3
"""Qualify every retained gate failure in three fresh serial processes."""
import json
import re
from verify import OUT, records, test

failed = {}
for row in records:
    if row['label'].startswith('post-isolate-'):
        continue
    for failure in row.get('failures', []):
        selector = 'FullyQualifiedName=' + failure['method']
        # These generated research tests have many slow civ-specific cases.
        if 'P703ResearchCommanderLiveTests.Both_sides_complete_' in failure['method']:
            selector += '&DisplayName~civA: ' + re.search(r'civA: (\d+),', failure['name'])[1] + ','
        failed[(row['project'], selector)] = failure
for index, ((project, selector), failure) in enumerate(sorted(failed.items())):
    for repeat in range(1, 4):
        test(f'post-isolate-{index:03}-{repeat}', project, selector)
print(json.dumps({'failed_selectors': len(failed)}, indent=2), flush=True)
