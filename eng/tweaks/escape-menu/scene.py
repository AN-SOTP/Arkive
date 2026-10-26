"""
The escape menu as the game fills it, with placeholder character data: writes layout.json (what build.py places) and
texts.json (what overlay.py writes in the game's fonts). Positions are the movie's own (form_Exitmsg = stage).

usage: scene.py <work folder>
"""
import json
import os
import sys

here = sys.argv[1]
BODY, TITLE = 'body', 'title'   # $YG760 -> ExuberanceF, $YoonGasiIIM -> MonarchaF

places, texts = [], []


def text(s, x, y, w, h, size, color, font=BODY, align='left', valign='top', omit=False):
    texts.append(dict(text=s, x=x, y=y, w=w, h=h, size=size, color=color, font=font, align=align, valign=valign, omit=omit))


def put(symbol, x, y, sx=1.0, sy=1.0, mult=None, add=None):
    item = dict(symbol=str(symbol), x=x, y=y, sx=sx, sy=sy)
    if mult is not None:
        item['mult'], item['add'] = mult, add
    places.append(item)


layout = {
    'hidden_names': ['classImgOtherCharacter', 'gatePanel', 'isEventMissionGroup', 'childModalMc', 'childWindow',
                     'containerMc'],
    'hidden_ids': [],
    'hide_in': {
        '111': ['serverChangeButton'],
        '147': ['loadingBgMc', 'blockLabel'],
    },
    'first_frame': [{'sprite': '37', 'character': '26'}],   # class art (Bard) on the class image's first frame
    'variants': {
        '9001': {'base': '586', 'hide': ['over_mc', 'newIcon']},                          # menu row
        '9002': {'base': '644', 'hide': ['spacialBgMc', 'newIconMc']},                    # bottom button
        '9003': {'base': '644', 'hide': ['newIconMc']},                                   # bottom button, special (Store)
        '9004': {'base': '309', 'hide': ['childBgMc1', 'childBgMc2', 'childBgMc3', 'childBgMc4', 'endContentsBgMc',
                                         'bookMarkBgMc', 'over_mc', 'lineMc', 'bonusGauge', 'noGoldIconMc',
                                         'emoticonTooltipMc', 'noRewardIconMc', 'arrow_mc', 'blockLabel',
                                         'thirdPointMc']},                                # content heading
        '9005': {'base': '309', 'hide': ['childBgMc0', 'childBgMc2', 'childBgMc3', 'childBgMc4', 'endContentsBgMc',
                                         'bookMarkBgMc', 'over_mc', 'lineMc', 'noGoldIconMc', 'emoticonTooltipMc',
                                         'noRewardIconMc', 'arrow_mc', 'blockLabel', 'thirdPointMc']},  # content row
    },
}

# --- title on the frame's centre plate ---
text('Game Menu', 907, 261, 350, 30, 20, '#fff7e2', TITLE, 'center')

# --- the five menu columns: title, then rows of icon + name ---
columns = [
    (557, 'Roster', [(53, 'Roster Info'), (59, 'Stronghold'), (38, 'Universal Storage'), (36, 'Collectibles'),
                     (46, 'Achievements'), (16, 'Card Catalog'), (17, 'Codex')]),
    (725, 'Character', [(0, 'Inventory'), (1, 'Char. Profile'), (2, 'Combat Skills'), (64, 'Ark Passive'),
                        (20, 'Pets'), (29, 'Mounts'), (26, 'Titles'), (4, 'Trade Skills')]),
    (893, 'Adventure', [(3, 'Quest Journal'), (11, 'World Map'), (7, "Adventurer's Tome"), (19, "Una's Tasks"),
                        (27, 'Calendar'), (12, 'Rapport'), (60, 'Bifrost')]),
    (1058, 'Guide', [(30, 'Guidebook'), (21, 'Waymark'), (34, 'Event Information'), (28, 'Login Events')]),
    (1226, 'Community', [(6, 'Friends'), (10, 'Guild'), (40, 'Find Party'), (39, 'Voice Chat'), (72, 'Global Chat'),
                         (61, 'Market')]),
]
for x, title, rows in columns:
    text(title, x - 2 + 2, 356, 150, 24, 18, '#a4afb7', TITLE, 'center')
    for i, (icon, name) in enumerate(rows):
        y = 378 + i * 33
        put('9001', x, y)
        put(f'ExitMenuCategory_ListItemIcon_{icon}', x + 6, y + 2)
        text(name, x + 40, y + 6, 100, 21, 14, '#ffffff', omit=True)

# --- character info (bottom left) ---
ix, iy = 255, 606
level, name = 'Lv. 70', 'Character Name'
text(level, ix + 52, iy + 68, 60, 24, 18, '#ffd200', TITLE)
text(name, ix + 52 + 58, iy + 68, 220, 24, 18, '#ffffff', TITLE)
text('1,700.00', ix + 39, iy + 93, 200, 22, 16, '#ffd200', TITLE, 'center')
text('Switch Characters', ix + 32, iy + 134, 228, 21, 16, '#ffffff', BODY, 'center')

# --- content status (right) ---
cx, cy = 1357, 252
text('Character Name', cx + 49, cy + 50, 260, 20, 14, '#f2d694', BODY, 'center')
text('View All Status', cx + 143 + 45, cy + 73 + 5, 120, 20, 14, '#ffffff')
tx, ty = cx + 32, cy + 116
content = [
    ('head', 'Daily'),
    ('row', 'Kurzan Front', 0, 1, -1),
    ('row', 'Guardian Raid', 1, 1, -1),
    ('row', "[Daily] Una's Task", 2, 3, -1),
    ('head', 'Weekly Tasks'),
    ('row', 'Act 4: Armoche', 1, 2, -1),
    ('row', 'Denouement: Kazeros', 0, 2, -1),
    ('row', "[Weekly] Una's Task", 3, 3, -1),
]
for i, item in enumerate(content):
    y = ty + i * 43
    if item[0] == 'head':
        put('9004', tx, y)
        text(item[1], tx + 9, y + 10, 300, 22, 16, '#ffffff', TITLE)
    else:
        _, label, cur, most, bonus = item
        put('9005', tx, y)
        name_y = 6 if bonus > -1 else 9
        text(label, tx + 36, y + name_y, 240, 21, 14, '#ffffff')
        done = cur >= most
        tint = '#999999' if done else '#ffffff'
        text(str(cur), tx + 259, y + 11, 35, 21, 14, tint, BODY, 'center')
        text('/', tx + 274, y + 11, 42, 21, 14, tint, BODY, 'center')
        text(str(most), tx + 296, y + 11, 35, 21, 14, tint, BODY, 'center')

# --- bottom bar: the game's buttons, centred, and Arkive Tweaks one button left of Store ---
bar_x, bar_y, bar_w = 549, 711, 128 * 9.626
pitch = 80 + 11
buttons = [(45, 'Store', True), (9, 'Settings', False), (14, 'Customer Service', False), (15, 'Coupons', False),
           (24, 'Escape', False), (31, 'Char. Select', False), (33, 'Quit', False)]
start = bar_x + (bar_w - pitch * len(buttons)) / 2
for i, (icon, label, special) in enumerate(buttons):
    x = start + i * pitch
    put('9003' if special else '9002', x, bar_y)
    put(f'ExitMenuBottomButton_icon_{icon}', x + (23 if special else 19), bar_y + (12 if special else 6))
    text(label, x + 2, bar_y + 40, 80, 38, 12, '#f2d694' if special else '#d1d2d0', BODY, 'center', 'center')
arkive_x = start - pitch
put('9002', arkive_x, bar_y)
layout['arkive_button'] = {'x': arkive_x, 'y': bar_y, 'w': 80, 'h': 80, 'icon_x': arkive_x + 19, 'icon_y': bar_y + 6}
# Our label copies Store's look (dress() takes the format and colour of the button it sits beside).
text('Arkive Tweaks', arkive_x + 2, bar_y + 40, 80, 38, 12, '#f2d694', BODY, 'center', 'center')

layout['places'] = places
layout['crop'] = [150, 195, 1830, 800]   # from the frame's top ornament to the bottom bar
json.dump(layout, open(os.path.join(here, 'layout.json'), 'w'), indent=1)
json.dump(texts, open(os.path.join(here, 'texts.json'), 'w'), indent=1)
print(len(places), 'places', len(texts), 'texts')

