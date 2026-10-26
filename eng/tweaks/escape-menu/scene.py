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
        '111': ['serverChangeButton'],   # placed again below, above Switch Characters, as the game shows both
        '147': ['loadingBgMc'],
    },
    'first_frame': [{'sprite': '37', 'character': '26'}],   # class art (Bard) on the class image's first frame
    'variants': {
        '9001': {'base': '586', 'hide': ['over_mc', 'newIcon']},                          # menu row
        '9002': {'base': '644', 'hide': ['spacialBgMc', 'newIconMc']},                    # bottom button
        '9003': {'base': '644', 'hide': ['newIconMc']},                                   # bottom button, special (Store)
    },
    'buttons': [],
}

# --- title on the frame's centre plate ---
text('Game Menu', 907, 261, 350, 30, 20, '#fff7e2', TITLE, 'center')

# --- the five menu columns as the English client fills them: title, then rows of icon + name ---
columns = [
    (557, 'Roster', [(76, 'Skin Codex'), (69, 'Trophies'), (62, 'Memory Chamber'), (46, 'Achievements'),
                     (26, 'Titles'), (25, 'Engraving Effect'), (16, 'Card Catalog'), (53, 'Roster Info')]),
    (725, 'Character', [(78, 'Ark Grid'), (73, 'Ark Passive'), (65, 'Mass Presets'), (4, 'Trade Skills'),
                        (2, 'Combat Skills'), (0, 'Inventory'), (1, 'Char. Profile'), (66, 'Faction Rank Info')]),
    (893, 'Adventure', [(74, 'Solo/Matching Mode'), (67, 'Content List'), (55, 'Bifrost'), (18, 'Sheet Music'),
                        (36, 'Collectibles'), (12, 'Rapport'), (7, "Adventurer's Tome"), (19, "Una's Tasks"),
                        (3, 'Quest Journal')]),
    (1058, 'Guide', [(79, 'Recommended Settings'), (77, 'Combat Ana.'), (20, 'Pets'), (29, 'Mounts'), (17, 'Codex'),
                     (11, 'World Map')]),
    (1226, 'Community', [(71, 'Style Book'), (72, 'Global Chat'), (61, 'Market'), (59, 'Stronghold Community'),
                         (42, 'Party and Raid'), (40, 'Find Party'), (39, 'Voice Chat'), (5, 'Interact'),
                         (10, 'Guild'), (6, 'Friends')]),
]
for x, title, rows in columns:
    text(title, x - 2 + 2, 356, 150, 24, 18, '#a4afb7', TITLE, 'center')
    for i, (icon, name) in enumerate(rows):
        y = 378 + i * 33
        put('9001', x, y)
        put(f'ExitMenuCategory_ListItemIcon_{icon}', x + 6, y + 2)
        text(name, x + 40, y + 6, 100, 21, 14, '#ffffff', omit=True)

# --- character info (bottom left): level and name centred over the buttons, item level below ---
ix, iy = 255, 606
servers_y = iy + 121 - 49   # with both buttons showing, the name and item level move up by one button
text('Lv. 70', ix + 52, iy + 68 - 49, 60, 24, 18, '#ffd200', TITLE)
text('Character Name', ix + 52 + 58, iy + 68 - 49, 220, 24, 18, '#ffffff', TITLE)
text('1700.00', ix + 39, iy + 93 - 49, 200, 22, 16, '#ffd200', TITLE, 'center')
put('Exitmenu_serverChangeButton', ix, servers_y)
text('Switch Servers', ix + 32, servers_y + 13, 228, 21, 16, '#ffffff', BODY, 'center')
text('Switch Characters', ix + 32, iy + 134, 228, 21, 16, '#ffffff', BODY, 'center')

# --- content status (right), as it shows before a task is set to track ---
cx, cy = 1357, 252
text('Character Name', cx + 49, cy + 50, 260, 20, 14, '#f2d694', BODY, 'center')
text('View All Status', cx + 143 + 45, cy + 73 + 5, 120, 20, 14, '#ffffff')
text('Set a task to track.', cx + 26 + 2 - 2, cy + 108 + 169.6 + 40.5, 386, 23, 14, '#a4afb7', BODY, 'center')
layout['buttons'].append({'x': cx + 167, 'y': cy + 352, 'w': 100, 'h': 34, 'label': 'Settings'})

# --- bottom bar: the game's buttons, centred, and Arkive Tweaks one button left of Store ---
bar_x, bar_y, bar_w = 549, 711, 128 * 9.626
pitch = 80 + 11
buttons = [(45, 'Store', True), (63, 'Terms of Service', False), (9, 'Settings', False), (24, 'Escape', False),
           (31, 'Char. Select', False), (68, 'Assign Gold Acquirer', False), (32, 'Return', False), (33, 'Quit', False)]
start = bar_x + (bar_w - pitch * len(buttons)) / 2
for i, (icon, label, special) in enumerate(buttons):
    x = start + i * pitch
    put('9003' if special else '9002', x, bar_y)
    put(f'ExitMenuBottomButton_icon_{icon}', x + (23 if special else 19), bar_y + (12 if special else 6))
    text(label, x + 2, bar_y + 40, 80, 38, 12, '#f2d694' if special else '#d1d2d0', BODY, 'center', 'center')
arkive_x = start - pitch
put('9002', arkive_x, bar_y)
layout['arkive_button'] = {'x': arkive_x, 'y': bar_y, 'w': 80, 'h': 80, 'icon_x': arkive_x + 19, 'icon_y': bar_y + 6}
text('Arkive Tweaks', arkive_x + 2, bar_y + 40, 80, 38, 12, '#d1d2d0', BODY, 'center', 'center')

layout['places'] = places
layout['crop'] = [150, 195, 1830, 800]   # from the frame's top ornament to the bottom bar
json.dump(layout, open(os.path.join(here, 'layout.json'), 'w'), indent=1)
json.dump(texts, open(os.path.join(here, 'texts.json'), 'w'), indent=1)
print(len(places), 'places', len(texts), 'texts')

