"""Collect NuGet metadata and license files for the distributable."""
import json
import pathlib
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
assets = json.loads((root / 'src/GameLearn/obj/project.assets.json').read_text(encoding='utf-8'))
cache = pathlib.Path(next(iter(assets['packageFolders'])))
destination = root / 'src/GameLearn/Assets/licenses'
destination.mkdir(parents=True, exist_ok=True)
rows = ["# Third-party notices", "", "GameLearn distributes the following components. Their licenses and copyrights remain with their respective authors.", "", "## Data and models", "", "- ECDICT, copyright (c) 2025 Linwei, MIT. Source revision bc015ed2e24a. See ../ECDICT-LICENSE.txt and ../dictionary-source.json.", "- PP-OCRv5 model weights, PaddlePaddle/PaddleOCR authors and Baidu, Apache-2.0. Latin recognition, mobile detection and text direction models distributed via RapidOcrNet. https://github.com/PaddlePaddle/PaddleOCR", "- PaddleOCR official HTTP protocol reference: https://github.com/PaddlePaddle/PaddleOCR/tree/main/api_sdk/typescript. Client implementation in GameLearn is independent C# code.", "", "## NuGet dependencies", "", "| Package | License | Project |", "| --- | --- | --- |"]
copyrights = []
packages = dict(assets['libraries'])
for framework in assets['project']['frameworks'].values():
    for package in framework.get('downloadDependencies', []):
        name = package['name']
        version = package['version'].strip('[]').split(',')[0].strip()
        packages[name + '/' + version] = {'path': name.lower() + '/' + version}
for library, info in sorted(packages.items()):
    package = cache / info['path']
    specs = list(package.glob('*.nuspec'))
    if not specs:
        continue
    metadata = ET.parse(specs[0]).getroot()
    def get(name):
        return next((el.text or '' for el in metadata.iter() if el.tag.split('}')[-1] == name), '')
    rows.append(f"| {library} | {get('license') or get('licenseUrl')} | {get('projectUrl')} |")
    copied = set()
    for file in package.rglob('*'):
        if file.is_file() and file.name.lower().startswith(('license', 'notice', 'thirdpartynotice', 'third-party-notice')) and file.suffix.lower() not in ('.dll', '.so'):
            out = library.replace('/', '-') + '-' + file.name
            if out not in copied:
                (destination / out).write_bytes(file.read_bytes())
                copied.add(out)
    if get('copyright'):
        copyrights.append(f"- {library}: {get('copyright')}")
rows.extend(['', '## Copyright notices', '', *copyrights])
(destination / 'THIRD_PARTY_NOTICES.md').write_text('\n'.join(rows) + '\n', encoding='utf-8')
print('Generated', destination / 'THIRD_PARTY_NOTICES.md')
