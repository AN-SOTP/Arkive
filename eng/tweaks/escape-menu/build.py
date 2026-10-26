"""
Builds a movie FFDec can draw the escape menu from: atlas sub-images become external images of their own (FFDec
draws external images but not DefineSubImage), the root shows the menu's content form, hidden parts are left out,
and the menu's lists get rows. Text is left empty here and drawn afterwards with the game's own font.

usage: build.py <exitmenu.xml> <movie dir with the TGAs> <out.xml> <layout.json>
"""
import copy
import json
import os
import sys
import xml.etree.ElementTree as ET

from PIL import Image

source, folder, out_xml, layout_json = sys.argv[1:5]
tree = ET.parse(source)
root = tree.getroot()
tags = root.find('tags')

# --- atlases: DefineSubImage -> DefineExternalImage2 with a cropped TGA ---
atlases = {}
for t in tags:
    if t.get('type') == 'DefineExternalImage2' and t.get('idType') == '9':
        atlases[t.get('imageID')] = t.get('fileName')
opened = {}
for i, t in enumerate(list(tags)):
    if t.get('type') != 'DefineSubImage':
        continue
    cid = t.get('characterID')
    atlas = atlases[t.get('imageId')]
    if atlas not in opened:
        opened[atlas] = Image.open(os.path.join(folder, atlas)).convert('RGBA')
    x1, x2, y1, y2 = (int(t.get(k)) for k in ('x1', 'x2', 'y1', 'y2'))
    name = f'sub_{cid}.tga'
    opened[atlas].crop((x1, y1, x2, y2)).save(os.path.join(folder, name))
    t.attrib.clear()
    t.attrib.update({'type': 'DefineExternalImage2', 'bitmapFormat': '13', 'characterID': cid, 'exportName': '',
                     'fileName': name, 'forceWriteAsLong': 'true', 'idType': '0', 'imageID': cid,
                     'targetHeight': str(y2 - y1), 'targetWidth': str(x2 - x1)})

sprites = {t.get('spriteId'): t for t in tags if t.get('type') == 'DefineSpriteTag'}
classes = {}
for t in tags:
    if t.get('type') == 'SymbolClassTag':
        for i, n in zip(t.find('tags'), t.find('names')):
            classes[n.text] = i.text


def remove_places(sprite_id, predicate):
    sub = sprites[sprite_id].find('subTags')
    for p in list(sub):
        if p.get('type', '').startswith('PlaceObject') and predicate(p):
            sub.remove(p)


def place(sprite_id, character, depth, x, y, sx=1.0, sy=1.0, name=None, color=None):
    """Adds a PlaceObject2 to the first frame of a sprite (before its ShowFrame)."""
    sub = sprites[sprite_id].find('subTags')
    show = next(i for i, p in enumerate(sub) if p.get('type') == 'ShowFrameTag')
    p = ET.Element('item', {'type': 'PlaceObject2Tag', 'characterId': str(character), 'depth': str(depth),
                            'forceWriteAsLong': 'false', 'placeFlagHasCharacter': 'true', 'placeFlagHasMatrix': 'true',
                            'placeFlagMove': 'false', 'placeFlagHasColorTransform': 'false', 'placeFlagHasRatio': 'false',
                            'placeFlagHasName': 'false', 'placeFlagHasClipDepth': 'false', 'placeFlagHasClipActions': 'false'})
    scaled = sx != 1.0 or sy != 1.0
    m = ET.SubElement(p, 'matrix', {'type': 'MATRIX', 'hasRotate': 'false', 'hasScale': 'true' if scaled else 'false',
                                    'nRotateBits': '0', 'nScaleBits': '22' if scaled else '0', 'nTranslateBits': '17',
                                    'rotateSkew0': '0', 'rotateSkew1': '0', 'scaleX': str(sx), 'scaleY': str(sy),
                                    'translateX': str(round(x * 20)), 'translateY': str(round(y * 20))})
    if color is not None:
        p.set('placeFlagHasColorTransform', 'true')
        mult, add = color
        ET.SubElement(p, 'colorTransform', {
            'type': 'CXFORMWITHALPHA', 'hasAddTerms': 'true', 'hasMultTerms': 'true', 'nbits': '10',
            'redMultTerm': str(round(mult[0] * 256)), 'greenMultTerm': str(round(mult[1] * 256)),
            'blueMultTerm': str(round(mult[2] * 256)), 'alphaMultTerm': '256',
            'redAddTerm': str(add[0]), 'greenAddTerm': str(add[1]), 'blueAddTerm': str(add[2]), 'alphaAddTerm': '0'})
    sub.insert(show, p)


layout = json.load(open(layout_json, encoding='utf-8'))
form = classes['form_Exitmsg']

# --- the stage behind the menu is black ---
for t in tags:
    if t.get('type') == 'SetBackgroundColorTag':
        c = t.find('backgroundColor')
        for k in ('red', 'green', 'blue'):
            c.set(k, '0')

# --- copies of renderers with some parts left out (one per look the game gives them at runtime) ---
for new_id, spec in layout.get('variants', {}).items():
    base_id = classes.get(spec['base'], spec['base'])
    base = sprites[base_id]
    copy_tag = copy.deepcopy(base)
    copy_tag.set('spriteId', new_id)
    sub = copy_tag.find('subTags')
    for p in list(sub):
        if p.get('type', '').startswith('PlaceObject') and (p.get('name') in spec['hide'] or p.get('characterId') in spec['hide']):
            sub.remove(p)
    tags.insert(list(tags).index(base) + 1, copy_tag)
    sprites[new_id] = copy_tag

# --- a later frame's picture shown on the first frame (the class art) ---
for spec in layout.get('first_frame', []):
    sub = sprites[spec['sprite']].find('subTags')
    item = next(p for p in sub if p.get('type', '').startswith('PlaceObject') and p.get('characterId') == spec['character'])
    show = next(i for i, p in enumerate(sub) if p.get('type') == 'ShowFrameTag')
    sub.insert(show, copy.deepcopy(item))

# --- root: the menu's content form instead of the window frame ---
for p in list(tags):
    if p.get('type', '').startswith('PlaceObject'):
        p.set('characterId', form)
        m = p.find('matrix')
        m.set('translateX', '0')
        m.set('translateY', '0')

# --- leave out what the menu hides while it shows its main page ---
hidden = set(layout['hidden_names'])
hidden_ids = set(str(i) for i in layout['hidden_ids'])
remove_places(form, lambda p: (p.get('name') in hidden) or (p.get('characterId') in hidden_ids))
for sprite_id, names in layout.get('hide_in', {}).items():
    remove_places(classes.get(sprite_id, sprite_id), lambda p, names=names: p.get('name') in names or p.get('characterId') in names)

# --- every text is drawn later with the game's font ---
for t in tags:
    if t.get('type') == 'DefineEditTextTag':
        t.set('initialText', '')
        t.set('hasText', 'false')

depth = 300
for item in layout['places']:
    character = classes.get(item['symbol'], item['symbol'])
    place(form, character, depth, item['x'], item['y'], item.get('sx', 1.0), item.get('sy', 1.0),
          color=(item['mult'], item['add']) if 'mult' in item else None)
    depth += 1

tree.write(out_xml, encoding='utf-8', xml_declaration=True)
print('written', out_xml, 'places', len(layout['places']))
