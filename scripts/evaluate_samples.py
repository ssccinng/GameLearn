"""Evaluate the manually annotated OBS dialogue used in the September 24 test.

This annotation applies to the captured 1440-pixel-wide OBS window only.
It deliberately does not treat ten frames of one dialogue as ten diverse scenes.
"""
import argparse
import json
import pathlib
import re
import statistics

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("directory", type=pathlib.Path)
args = parser.parse_args()
data = json.loads((args.directory / 'samples.json').read_text(encoding='utf-8'))
expected = 'A little elbow grease and I could just get them back on their feet.'

def tokens(text):
    return re.findall(r"[a-z]+(?:'[a-z]+)*", text.lower())

def distance(left, right):
    row = list(range(len(right) + 1))
    for i, first in enumerate(left, 1):
        current = [i]
        for j, second in enumerate(right, 1):
            current.append(min(current[-1] + 1, row[j] + 1, row[j - 1] + (first != second)))
        row = current
    return row[-1]

rows = []
for sample in data:
    if sample['Width'] != 1440:
        raise ValueError('The annotation region is for the original 1440-pixel-wide OBS capture only.')
    lines = [line for line in sample['lines'] if line['Bounds']['X'] > 800 and 185 < line['Bounds']['Y'] < 245]
    actual = ' '.join(line['Text'] for line in lines)
    reference = tokens(expected)
    errors = distance(reference, tokens(actual))
    rows.append(dict(index=sample['index'], expected=expected, actual=actual, referenceWords=len(reference),
                     editErrors=errors, accuracy=max(0, 1 - errors / len(reference)), elapsedMs=sample['elapsedMs']))
report = dict(sampleCount=len(rows), distinctDialogues=1,
              note='Ten actual captures of one unchanged dialogue. Not a diverse-game benchmark. OBS UI outside the manually annotated dialogue region is excluded.',
              meanMs=statistics.mean(row['elapsedMs'] for row in rows), maxMs=max(row['elapsedMs'] for row in rows),
              wordAccuracy=1 - sum(row['editErrors'] for row in rows) / sum(row['referenceWords'] for row in rows), samples=rows)
(args.directory / 'quality.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({key: value for key, value in report.items() if key != 'samples'}, ensure_ascii=False, indent=2))
