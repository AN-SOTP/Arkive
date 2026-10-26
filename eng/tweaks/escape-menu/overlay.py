"""
Writes texts.json onto the rendered menu in the game's English fonts, draws the Arkive Tweaks button's "A" the way the
tweak draws it, marks the button, and crops the menu.

usage: overlay.py <work folder: fonts, layout.json, texts.json> <frame.png> <out.jpg>
"""
import json
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

here = sys.argv[1]
FONTS = {'body': os.path.join(here, 'ExuberanceF-Regular.ttf'), 'title': os.path.join(here, 'MonarchaF-Regular.ttf')}
cache = {}


def font(kind, size):
    key = (kind, size)
    if key not in cache:
        cache[key] = ImageFont.truetype(FONTS[kind], size)
    return cache[key]


def rgb(hex_color):
    hex_color = hex_color.lstrip('#')
    return tuple(int(hex_color[i:i + 2], 16) for i in (0, 2, 4)) + (255,)


def wrap(text, f, width):
    words, lines, line = text.split(), [], ''
    for word in words:
        candidate = (line + ' ' + word).strip()
        if f.getlength(candidate) <= width or not line:
            line = candidate
        else:
            lines.append(line)
            line = word
    lines.append(line)
    return lines


def draw_text(layer, item):
    f = font(item['font'], item['size'])
    d = ImageDraw.Draw(layer)
    inner_w = item['w'] - 4
    lines = wrap(item['text'], f, inner_w) if item['valign'] == 'center' else [item['text']]
    if item.get('omit') and f.getlength(lines[0]) > inner_w:
        # the game's omitTextField: cut to fit and end with an ellipsis (the full name shows in a tooltip)
        line = lines[0]
        while line and f.getlength(line + '...') > inner_w:
            line = line[:-1]
        lines = [line.rstrip() + '...']
    ascent, descent = f.getmetrics()
    line_h = ascent + descent
    block = line_h * len(lines)
    top = item['y'] + 2 + ((item['h'] - 4 - block) / 2 if item['valign'] == 'center' else 0)
    for i, line in enumerate(lines):
        width = f.getlength(line)
        if item['align'] == 'center':
            x = item['x'] + 2 + (inner_w - width) / 2
        elif item['align'] == 'right':
            x = item['x'] + 2 + inner_w - width
        else:
            x = item['x'] + 2
        y = top + i * line_h
        # the game's labels carry a soft dark outline (glow filter) for legibility on the art
        shadow = Image.new('RGBA', layer.size, (0, 0, 0, 0))
        ImageDraw.Draw(shadow).text((x, y), line, font=f, fill=(0, 0, 0, 200))
        shadow = shadow.filter(ImageFilter.GaussianBlur(1.2))
        layer.alpha_composite(shadow)
        d.text((x, y), line, font=f, fill=rgb(item['color']))


frame = Image.open(sys.argv[2]).convert('RGBA')
layout = json.load(open(os.path.join(here, 'layout.json')))
# Buttons made from shared component symbols the movie can't draw on its own (the task tracker's Settings).
for b in layout.get('buttons', []):
    box = Image.new('RGBA', (b['w'], b['h']))
    for row in range(b['h']):
        shade = 74 - int(22 * row / b['h'])
        ImageDraw.Draw(box).line((0, row, b['w'], row), fill=(shade - 18, shade - 6, shade + 12, 255))
    ImageDraw.Draw(box).rectangle((0, 0, b['w'] - 1, b['h'] - 1), outline=(96, 112, 134, 255))
    frame.alpha_composite(box, (int(b['x']), int(b['y'])))
    draw_text(frame, dict(text=b['label'], x=b['x'], y=b['y'], w=b['w'], h=b['h'], size=14, color='#ffffff',
                          font='body', align='center', valign='center'))
for item in json.load(open(os.path.join(here, 'texts.json'))):
    draw_text(frame, item)

# The tweak's icon: an "A" in the label font, 0xD1D2D0, 0.8 of Store's icon size, centred in that square.
button = layout['arkive_button']
size = layout.get('store_icon_size', 40)
f = font('body', int(size * 0.8))
letter_w = f.getlength('A')
ascent, descent = f.getmetrics()
ImageDraw.Draw(frame).text((button['icon_x'] + (size - letter_w) / 2, button['icon_y'] + (size - ascent - descent) / 2 - 2 + 2),
                           'A', font=f, fill=rgb('#d1d2d0'))

# The mark: a glowing gold frame around the button.
x0, y0 = button['x'] - 4, button['y'] - 4
x1, y1 = button['x'] + button['w'] + 4, button['y'] + button['h'] + 2
glow = Image.new('RGBA', frame.size, (0, 0, 0, 0))
ImageDraw.Draw(glow).rounded_rectangle((x0, y0, x1, y1), radius=8, outline=(255, 201, 77, 255), width=6)
glow = glow.filter(ImageFilter.GaussianBlur(6))
frame.alpha_composite(glow)
ImageDraw.Draw(frame).rounded_rectangle((x0, y0, x1, y1), radius=8, outline=(255, 214, 110, 255), width=3)

crop = layout.get('crop', [150, 130, 1830, 810])
frame.crop(crop).convert('RGB').save(sys.argv[3], quality=92, optimize=True)
print('saved', sys.argv[3], frame.crop(crop).size)

