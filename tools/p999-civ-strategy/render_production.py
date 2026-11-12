#!/usr/bin/env python3
"""Render P1000's reproducible production-choice / generated-plan comparison."""
import argparse
import csv
import json
from pathlib import Path


def milestones(civ, plan, target):
    nodes = {(n["kind"], n["id"]): n for n in civ["nodes"]}
    funding = seconds = 0
    for index, step in enumerate(plan["steps"], 1):
        key = (step["action"], step["id"])
        node = nodes[key]
        funding += node["price"]
        seconds += node["seconds"]
        if key == (target["kind"], target["id"]):
            return f"{index} / {seconds:g} / {funding}"
    return "not in completion order"


def render(rows, output):
    output.mkdir(parents=True, exist_ok=True)
    text = ["# P1000 production portfolios versus today's generated commander", "",
        "Seed 1000; each civilization faces the other nine public PCore001 profiles.",
        "Default research settings and portfolio assumptions. These are observation-only choices.",
        "The comparison is a legal ordering witness: primary, compatible counter, then a coverage tail.",
        "It is never installed in the commander. Live research ordering is unchanged.", "",
        "Milestone cells are **step / serial bucket seconds / cumulative bucket funding**,",
        "at researchTimeScale=1, unlimited funding and suitable rocks. Travel, construction shells,",
        "combat and income waits are excluded from those serial columns. Choice critical times",
        "separately include the public 1000m constructor travel assumption and 30s shells,",
        "with independent prerequisite buckets overlapping. They are estimates, not observed sim times.", "",
        "| Civ | Primary | Counter | Primary funding | Critical s | Routes |", "|---|---|---|---:|---:|---:|"]
    ledger = []
    for civ in rows:
        nodes = {(n["kind"], n["id"]): n for n in civ["nodes"]}
        choice = civ["choice"]
        def name(candidate):
            if candidate is None:
                return "none"
            target = candidate["route"]["target"]
            return nodes[target["kind"], target["id"]]["name"]
        p, c = choice["primary"], choice["counter"]
        text.append(f"| {civ['name']} | {name(p)} | {name(c)} | {p['route']['funding'] if p else 0} | "
                    f"{p['route']['criticalSeconds'] if p else 0:.1f} | {civ['routeCount']} |")
        ledger.append(dict(civ=civ["civ"], name=civ["name"], primary=name(p), counter=name(c),
            funding=p["route"]["funding"] if p else 0, critical_seconds=p["route"]["criticalSeconds"] if p else 0,
            routes=civ["routeCount"], input_hash=choice["inputHash"]))
    for civ in rows:
        nodes = {(n["kind"], n["id"]): n for n in civ["nodes"]}
        text += ["", f"## {civ['name']} ({civ['civ']})", "",
                 "| Selected spike | Today: step / s / funding | Comparison: step / s / funding |", "|---|---|---|"]
        for role in ("primary", "counter"):
            candidate = civ["choice"][role]
            if candidate is None:
                continue
            target = candidate["route"]["target"]
            label = nodes[target["kind"], target["id"]]["name"]
            text.append(f"| {role}: {label} | {milestones(civ, civ['baseline'], target)} | "
                        f"{milestones(civ, civ['comparison'], target)} |")
        text += ["", "| # | Today's generated plan | Observation-only comparison witness |", "|---:|---|---|"]
        for index in range(40):
            def step(plan):
                if index >= len(plan["steps"]):
                    return "—"
                s = plan["steps"][index]
                return f"{s['action']} {s['id']}: {s['name']}"
            text.append(f"| {index + 1} | {step(civ['baseline'])} | {step(civ['comparison'])} |")
        text += ["", "Primary's concrete fielding witness (hull/part IDs are loaded-core identities):", "",
                 "| Duty | Hull | Ships × crew | Hull + parts per ship | Delivery s | Mounted part:mount:amount |", "|---|---:|---:|---:|---:|---|"]
        for spike in civ["choice"]["primary"]["spikes"] if civ["choice"]["primary"] else []:
            parts = ", ".join(f"{p['id']}:{p['mount']}:{p['amount']}" for p in spike["parts"])
            text.append(f"| {spike['duty']} | {spike['hull']} | {spike['ships']} × {spike['crew']} | "
                        f"{spike['price']:g} | {spike['travelSeconds']:.1f} | {parts or 'none (NPC miner)'} |")
        text += ["", f"Choice input SHA-256: `{civ['choice']['inputHash']}`.", ""]
    (output / "plan-comparison.md").write_text("\n".join(text) + "\n")
    with (output / "portfolios.csv").open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(ledger[0]))
        writer.writeheader()
        writer.writerows(ledger)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("production", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    render(json.loads(args.production.read_text()), args.output)


if __name__ == "__main__":
    main()
