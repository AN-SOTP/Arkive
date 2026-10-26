"""Converts UModel's PNG exports of a UI package into the TGA files its movies name (exitMenu_I1.nopack.tga)."""
import glob
import os
import re
import sys

from PIL import Image

source, movie_dump, target = sys.argv[1], sys.argv[2], sys.argv[3]
os.makedirs(target, exist_ok=True)
names = set()
for line in open(movie_dump, encoding='utf-8', errors='replace'):
    if 'DefineExternalImage2' in line:
        data = bytes.fromhex(''.join(re.findall(r'\b[0-9a-f]{2}\b', line.split('len=')[1].split(None, 1)[1])))
        for match in re.finditer(rb'[A-Za-z0-9_.]+\.tga', data):
            text = match.group().decode()
            # the length byte before the name may be a printable character
            for start in range(len(text)):
                names.add(text[start:])
pngs = {os.path.splitext(os.path.basename(p))[0].lower(): p for p in glob.glob(os.path.join(source, '**', '*.png'), recursive=True)}
done = 0
for name in sorted(names):
    key = name[:-4].lower().replace('.nopack', '_nopack')
    if key in pngs:
        Image.open(pngs[key]).convert('RGBA').save(os.path.join(target, name))
        done += 1
print(f'{done} tga written to {target}')
