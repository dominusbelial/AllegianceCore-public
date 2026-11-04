#!/usr/bin/env python3
"""Exact two-sided fair-coin test power for a predeclared 70/30 alternative."""
from math import comb


def power(n, alternative=.7, alpha=.05):
    threshold = next(k for k in range(n // 2 + 1, n + 1)
                     if 2 * sum(comb(n, j) * .5 ** n for j in range(k, n + 1)) <= alpha)
    probability = lambda j: comb(n, j) * alternative ** j * (1-alternative) ** (n-j)
    return dict(matches=n, rejectAtLeast=threshold, rejectAtMost=n-threshold,
                power=sum(probability(j) for j in range(threshold, n+1))
                + sum(probability(j) for j in range(n-threshold+1)))


if __name__ == '__main__':
    import json
    print(json.dumps([power(n) for n in [50, 52, 60, 80, 100, 104]], indent=2))
