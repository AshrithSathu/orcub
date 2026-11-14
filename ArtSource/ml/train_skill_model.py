#!/usr/bin/env python3
"""Train schema-1 logistic regression from orcub-attempts.json or sim_attempts.json.
No dependencies. Fixed normalisation matches EarlyFeatures.Vector. Every fifth row
is held out before training; simulation uses independent random players per row.
Usage: python3 ArtSource/ml/train_skill_model.py INPUT --output MODEL
"""
import argparse
import json
import math
import hashlib
from pathlib import Path


def vector(record):
    f = record['Features']
    n = max(1, f['Count'])
    return [1, f['Useful']/n, f['Think']/n/30, f['Locked']/n/10, f['Undo']/n]


def sigmoid(z):
    return 1 / (1 + math.exp(-max(-40, min(40, z))))


def train(rows):
    weights = [0.] * 5
    for epoch in range(1200):
        gradient = [0.] * 5
        for x, y in rows:
            error = sigmoid(sum(a*b for a, b in zip(weights, x))) - y
            for j in range(5):
                gradient[j] += error * x[j]
        for j in range(5):
            weights[j] -= .8 * (gradient[j]/len(rows) + (.001*weights[j] if j else 0))
    return weights


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('--output', type=Path, default=Path('Assets/Orcub/Resources/Orcub/skill_model.json'))
    args = parser.parse_args()
    data = json.loads(args.input.read_text())['Attempts']
    rows = [(vector(r), int(r['Result']['Outcome'] == 0)) for r in data
            if r.get('Version', 1) >= 2 and r.get('Features', {}).get('Count', 0) >= 5
            and r['Result']['Outcome'] != 2 and r['Result'].get('Continues', 0) == 0
            and r['Result'].get('BoostersUsed', 0) == 0]
    test = rows[::5]
    training = [r for i, r in enumerate(rows) if i % 5]
    if len(test) < 20 or len({y for _, y in training}) != 2:
        raise ValueError('Need more complete attempts from both outcome classes')
    weights = train(training)
    predictions = [(sigmoid(sum(a*b for a,b in zip(weights,x))), y) for x,y in test]
    metrics = {'train': len(training), 'held_out': len(test),
               'accuracy': sum((p >= .5) == y for p,y in predictions)/len(test),
               'brier': sum((p-y)**2 for p,y in predictions)/len(test),
               'log_loss': -sum(y*math.log(max(1e-9,p))+(1-y)*math.log(max(1e-9,1-p)) for p,y in predictions)/len(test)}
    observed = sum(y for _, y in predictions)/len(test)
    metrics['observed_win_rate'] = observed
    metrics['mean_prediction'] = sum(p for p,_ in predictions)/len(test)
    metrics['baseline_brier'] = observed*(1-observed)
    metrics['source_sha256'] = hashlib.sha256(args.input.read_bytes()).hexdigest()
    args.output.write_text(json.dumps({'Schema': 1, 'Version': 'sim-logistic-v1', 'Bias': weights[0], 'Weights': weights[1:]}, indent=2)+'\n')
    Path('ArtSource/ml/model_metrics.json').write_text(json.dumps(metrics, indent=2)+'\n')
    print(json.dumps(metrics, indent=2))


if __name__ == '__main__':
    main()
