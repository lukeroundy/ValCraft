"""0.11.0: generates every Valheim item missing in Minecraft from valheim_items.py: original procedural 16x16 textures,
armor layers, models, lang, recipes, tags, the Java registration table (valcraft_items.json) and the ItemMap defaults."""
import json, math, os, random, re
from valheim_items import ARMOR_SETS, ARMOR_NAMES, ITEMS, SLOT_SHARE

BASE = {'rag': (150, 130, 100), 'leather': (140, 90, 50), 'troll': (70, 100, 140), 'iron': (150, 155, 165), 'root': (90, 110, 60),
 'wolf': (200, 200, 210), 'fenris': (90, 80, 90), 'padded': (170, 150, 110), 'carapace': (60, 120, 130), 'eitr': (90, 170, 220),
 'flametal': (220, 120, 50), 'embla': (150, 40, 50), 'ask': (110, 90, 70), 'gold': (230, 190, 60), 'red': (190, 40, 40), 'flower': (230, 210, 90),
 'linen': (225, 220, 200), 'lox': (130, 100, 70), 'feather': (210, 220, 230), 'ash': (80, 75, 75), 'wood': (140, 100, 55), 'bronze': (186, 118, 52),
 'silver': (200, 210, 225), 'blackmetal': (70, 70, 80), 'flint': (120, 120, 115), 'copper': (200, 110, 70), 'chitin': (120, 150, 140),
 'stone': (128, 128, 128), 'crystal': (180, 220, 240), 'antler': (210, 190, 160), 'birch': (225, 215, 185), 'fire': (240, 130, 30),
 'poison': (120, 170, 60), 'obsidian': (60, 40, 80), 'frost': (150, 210, 245), 'bone': (230, 225, 205), 'serpent': (60, 110, 90),
 'pine': (110, 80, 50), 'ygg': (110, 170, 200), 'resin': (215, 140, 40), 'eye': (140, 60, 160), 'guck': (100, 160, 50), 'flesh': (200, 90, 90),
 'blood': (150, 20, 30), 'jelly': (230, 180, 210), 'marble': (55, 55, 65), 'jotun': (230, 210, 80), 'grausten': (95, 95, 100), 'sulfur': (230, 220, 70),
 'barley': (220, 190, 110), 'flax': (110, 150, 200), 'tar': (35, 30, 30), 'carrot': (240, 130, 30), 'turnip': (220, 200, 210), 'onion': (220, 170, 120),
 'cloud': (240, 190, 60), 'blue': (60, 80, 200), 'cooked': (150, 85, 45), 'fish': (140, 160, 180), 'pearl': (240, 235, 220)}
def ramp(c): return [tuple(max(0, min(255, int(v * f + (30 if f > 1.15 else 0)))) for v in c) for f in (0.45, 0.68, 1.0, 1.18, 1.35)]
HANDLE = [(55, 38, 20), (95, 66, 36), (133, 98, 56), (160, 120, 70), (180, 140, 90)]

class Canvas:
    def __init__(s, w=16, h=16): s.w, s.h = w, h; s.p = [[None] * w for _ in range(h)]
    def put(s, x, y, pal, sh):
        if 0 <= x < s.w and 0 <= y < s.h: s.p[y][x] = (pal, max(0, min(4, sh)))
    def line(s, x0, y0, x1, y1, pal, sh=2, wd=1, grad=True):
        n = max(abs(x1 - x0), abs(y1 - y0), 1)
        for i in range(n + 1):
            x = round(x0 + (x1 - x0) * i / n); y = round(y0 + (y1 - y0) * i / n)
            for dx in range(wd):
                for dy in range(wd): s.put(x + dx, y + dy, pal, sh + (1 if grad and dx == 0 and dy == 0 and wd > 1 else 0))
    def disc(s, cx, cy, r, pal, sh=2):
        for y in range(s.h):
            for x in range(s.w):
                d = math.hypot(x - cx, y - cy)
                if d <= r: s.put(x, y, pal, sh + (1 if (x - cx) + (y - cy) < -r * 0.4 else 0) - (1 if (x - cx) + (y - cy) > r * 0.6 else 0))
    def rect(s, x0, y0, x1, y1, pal, sh=2):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1): s.put(x, y, pal, sh + (1 if x == x0 or y == y0 else 0) - (1 if x == x1 or y == y1 else 0))
    def pixels(s, pals, rnd):
        out = [[(0, 0, 0, 0)] * s.w for _ in range(s.h)]
        for y in range(s.h):
            for x in range(s.w):
                c = s.p[y][x]
                if c: sh = max(0, min(4, c[1] + (rnd.choice((-1, 0, 0, 0, 1)) if c[0] != 'h' else 0))); out[y][x] = (HANDLE if c[0] == 'h' else pals)[sh] + (255,)
                elif any(0 <= x + dx < s.w and 0 <= y + dy < s.h and s.p[y + dy][x + dx] for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))): out[y][x] = (28, 22, 22, 255)
        return out

def draw(kind, rnd):
    c = Canvas(); j = rnd.randint(-1, 1)
    def handle(x0=2, y0=13, x1=7, y1=8): c.line(x0, y0, x1, y1, 'h', 2)
    if kind in ('sword', 'greatsword'):
        handle(2, 13, 4, 11); c.line(3, 10, 6, 13, 'h', 1, 3); c.line(5, 10, 14 if kind == 'greatsword' else 13, 1, 'm', 2 if kind == 'greatsword' else 1, 3); c.line(6, 10, 13 + j, 2, 'm', 1, 4)
    elif kind == 'knife': c.line(3, 12, 6, 9, 'h', 2); c.line(7, 9, 12, 4 + j, 'm', 2, 3); c.line(5, 8, 8, 11, 'm', 1, 1)
    elif kind in ('mace', 'club'):
        c.line(2, 13, 9, 6, 'h', 2)
        if kind == 'mace': c.disc(11, 4, 3 + (j > 0), 'm'); [c.put(11 + dx, 4 + dy, 'm', 4) for dx, dy in ((0, -4), (4, 0), (-4, 0), (0, 4), (3, -3))]
        else: c.disc(11, 4, 3, 'h', 2)
    elif kind == 'sledge': c.line(2, 14, 9, 7, 'h', 2); c.rect(7, 1, 14, 6, 'm'); c.rect(8, 2, 13, 5, 'm', 3)
    elif kind in ('axe', 'battleaxe'):
        c.line(2, 14, 10, 4, 'h', 2); c.disc(11, 5, 3, 'm');
        if kind == 'battleaxe': c.disc(6, 2 + 1, 2.5, 'm', 1); c.disc(13, 9, 2.5, 'm', 1)
    elif kind == 'pickaxe': c.line(2, 14, 9, 6, 'h', 2); c.line(3, 4, 8, 2, 'm', 1, 2); c.line(8, 2, 13, 7 + j, 'm', 1, 2); c.line(4, 4, 12, 6, 'm', 2, 1)
    elif kind == 'hoe': c.line(2, 14, 10, 4, 'h', 2); c.line(9, 3, 13, 3, 'm', 3, 2); c.line(12, 3, 12, 6, 'm', 2, 2)
    elif kind in ('spear', 'atgeir'):
        c.line(1, 14, 10, 5, 'h', 1); c.line(10, 5, 14, 1, 'm', 3, 2)
        if kind == 'atgeir': c.line(10, 2, 13, 5, 'm', 2, 2)
    elif kind == 'fist': c.rect(3, 6, 12, 12, 'h', 2); [c.line(4 + 3 * i, 6, 3 + 3 * i, 1, 'm', 3, 1) for i in range(3)]
    elif kind == 'staff': c.line(2, 14, 11, 5, 'h', 1); c.disc(12, 3, 2.5, 'm', 3); c.put(12, 3, 'm', 4)
    elif kind == 'bow':
        for t in range(13): a = -1.1 + 2.2 * t / 12; c.put(round(4 + 8 * math.cos(a) - 4), round(8 + 7 * math.sin(a)), 'm', 2 + (t % 3 == 0)); c.put(round(4 + 8 * math.cos(a) - 3), round(8 + 7 * math.sin(a)), 'h', 2)
        c.line(4, 2, 4, 14, 'h', 4, 1, False)
    elif kind == 'crossbow': c.line(3, 13, 11, 5, 'h', 2); c.line(4, 4, 13, 12, 'm', 2, 1); c.line(4, 4, 8, 9, 'h', 4, 1, False); c.line(13, 12, 8, 9, 'h', 4, 1, False)
    elif kind == 'arrow': c.line(3, 12, 11, 4, 'h', 1); c.line(11, 4, 13, 2, 'm', 3, 2); c.line(2, 11, 3, 14, 'm', 4, 1); c.line(1, 12, 4, 13, 'm', 4, 1)
    elif kind == 'shield':
        if rnd.random() < 0.5: c.disc(7.5, 7.5, 6.5, 'm'); c.disc(7.5, 7.5, 1.5, 'h', 4)
        else: c.rect(3, 1, 12, 13, 'm'); c.line(3, 7, 12, 7, 'h', 3); c.line(7, 1, 7, 13, 'h', 3)
    elif kind in ('helmet', 'helmet_misc'): c.rect(3, 3, 12, 6, 'm', 3); c.rect(3, 6, 5, 10, 'm'); c.rect(10, 6, 12, 10, 'm'); c.rect(5, 2, 10, 3, 'm', 4)
    elif kind == 'chestplate': c.rect(3, 2, 12, 12, 'm'); c.rect(1, 2, 3, 6, 'm', 3); c.rect(12, 2, 14, 6, 'm', 3); [c.put(x, 2, None, 0) for x in ()]; c.p[2][6] = c.p[2][7] = c.p[2][8] = c.p[2][9] = None
    elif kind == 'leggings': c.rect(4, 2, 11, 5, 'm', 3); c.rect(4, 5, 7, 13, 'm'); c.rect(8, 5, 11, 13, 'm', 1)
    elif kind == 'boots': c.rect(3, 6, 6, 12, 'm'); c.rect(9, 6, 12, 12, 'm'); c.rect(1, 11, 6, 13, 'm', 1); c.rect(9, 11, 14, 13, 'm', 1)
    elif kind == 'cape': [c.line(5 - i // 3, 2 + i, 10 + i // 3, 2 + i, 'm', 2 + (i % 4 == 0), 1, False) for i in range(12)]; c.rect(5, 1, 10, 2, 'h', 3)
    elif kind == 'log': c.rect(2, 4, 13, 11, 'm'); c.disc(13, 7.5, 3.5, 'm', 3); c.disc(13, 7.5, 1.5, 'm', 1)
    elif kind in ('ingot',): c.rect(3, 5, 13, 10, 'm'); c.rect(4, 5, 12, 6, 'm', 4)
    elif kind == 'raw': [c.disc(rnd.uniform(5, 10), rnd.uniform(5, 10), rnd.uniform(2, 3.5), 'm', rnd.randint(1, 3)) for _ in range(4)]
    elif kind in ('orb', 'egg', 'berry', 'coin', 'blob', 'powder', 'gem', 'onion'):
        if kind == 'berry': [c.disc(rnd.uniform(4, 11), rnd.uniform(5, 11), 2.2, 'm') for _ in range(5)]; c.line(8, 1, 8, 4, 'h', 2)
        elif kind == 'egg': c.disc(8, 8.5, 4.5, 'm'); c.disc(8, 5.5, 3, 'm', 3)
        elif kind == 'coin': [c.disc(5 + 3 * i, 11 - i * 2, 3, 'm', 2 + i % 2) for i in range(3)]
        elif kind == 'blob': c.disc(8, 9, 5, 'm'); c.disc(8, 5, 2.5, 'm', 3)
        elif kind == 'powder': c.rect(3, 10, 12, 13, 'm', 1); c.disc(8, 10, 4, 'm', 3)
        elif kind == 'gem': c.line(8, 2, 3, 8, 'm', 4, 2); c.line(8, 2, 13, 8, 'm', 3, 2); c.line(3, 8, 8, 14, 'm', 2, 2); c.line(13, 8, 8, 14, 'm', 1, 2); c.disc(8, 8, 3.5, 'm', 2)
        else: c.disc(8, 8, 5.5, 'm'); c.put(6, 6, 'm', 4)
    elif kind == 'seed': [c.disc(rnd.uniform(4, 12), rnd.uniform(4, 12), 1.6, 'm', rnd.randint(1, 3)) for _ in range(5)]
    elif kind in ('hide', 'thread'):
        if kind == 'hide': c.rect(3, 3, 12, 12, 'm'); [c.put(x, y, None, 0) for x, y in ((3, 3), (12, 3), (3, 12), (12, 12))]; c.p[3][3] = c.p[3][12] = c.p[12][3] = c.p[12][12] = None
        else: c.disc(8, 8, 5, 'm', 1); [c.line(3, 4 + 2 * i, 12, 5 + 2 * i, 'm', 3, 1, False) for i in range(4)]
    elif kind in ('bone', 'shard', 'feather', 'nail', 'chain'):
        if kind == 'bone': c.line(3, 12, 12, 3, 'm', 3, 2); c.disc(3, 12, 1.6, 'm', 3); c.disc(12, 3, 1.6, 'm', 3)
        elif kind == 'shard': c.line(4, 13, 10, 2, 'm', 3, 3); c.line(9, 12, 13, 5, 'm', 2, 2)
        elif kind == 'feather': c.line(3, 13, 12, 2, 'h', 4, 1); [c.line(4 + i, 11 - i, 8 + i, 12 - i, 'm', 2 + i % 2, 1) for i in range(8)]
        elif kind == 'nail': [c.line(3 + 4 * i, 3, 3 + 4 * i, 12, 'm', 2, 1) or c.rect(2 + 4 * i, 2, 4 + 4 * i, 3, 'm', 3) for i in range(3)]
        else: [c.disc(4 + 3 * i, 4 + 3 * i, 2, 'm', 2) or c.put(4 + 3 * i, 4 + 3 * i, None, 0) for i in range(4)]; [setattr(c, 'w', 16)]
    elif kind in ('plant',): [c.line(4 + 3 * i, 14, 5 + 3 * i + rnd.randint(-1, 1), 2 + rnd.randint(0, 3), 'm', 2 + i % 2, 1) for i in range(3)]
    elif kind == 'mushroom': c.rect(7, 8, 9, 13, 'h', 4); c.disc(8, 7, 5, 'm'); [c.put(x, 7, 'm', 0) for x in range(3, 14)]
    elif kind in ('meat', 'cooked'): c.disc(9, 7, 5, 'm'); c.line(2, 13, 6, 9, 'h', 4, 2)
    elif kind == 'fish': c.disc(7, 8, 4, 'm'); c.line(11, 8, 14, 5, 'm', 1, 2); c.line(11, 8, 14, 11, 'm', 1, 2); c.put(5, 7, 'h', 0)
    elif kind == 'carrot': c.line(4, 12, 11, 5, 'm', 2, 3); c.line(11, 4, 13, 1, 'h', 1, 1); c.line(12, 5, 14, 3, 'h', 1, 1)
    elif kind == 'bowl': c.rect(2, 8, 13, 12, 'h', 2); c.rect(3, 6, 12, 8, 'm', 3)
    elif kind == 'bread': c.disc(8, 9, 5.5, 'm'); c.line(5, 7, 11, 7, 'm', 0, 1)
    elif kind == 'mead': c.rect(5, 5, 10, 13, 'm'); c.rect(6, 2, 9, 4, 'h', 3); c.rect(6, 1, 9, 1, 'h', 1)
    elif kind == 'trophy': c.disc(8, 8, 5, 'm'); c.put(6, 7, 'h', 0); c.put(10, 7, 'h', 0); c.line(3, 4, 1, 1, 'h', 4, 1); c.line(12, 4, 14, 1, 'h', 4, 1)
    elif kind == 'block_item': c.rect(2, 2, 13, 13, 'm')
    else: c.rect(3, 3, 12, 12, 'm')
    return c

def armor_layer(pal, rnd, legs=False):
    w, h = 64, 32
    out = []
    for y in range(h):
        line = []
        for x in range(w):
            sh = 2 + rnd.choice((-1, 0, 0, 1)) - (1 if (x % 8 in (0, 7) or y % 8 in (0, 7)) else 0) + (1 if (x + y) % 11 == 0 else 0)
            line.append(pal[max(0, min(4, sh))] + (255,))
        out.append(line)
    return out

KIND_MODEL = {'sword': 'handheld', 'greatsword': 'handheld', 'knife': 'handheld', 'mace': 'handheld', 'club': 'handheld', 'sledge': 'handheld', 'axe': 'handheld',
              'battleaxe': 'handheld', 'pickaxe': 'handheld', 'hoe': 'handheld', 'spear': 'handheld', 'atgeir': 'handheld', 'staff': 'handheld', 'fist': 'generated'}
MAT = {'rag': 'valcraft:leather_scraps', 'leather': 'valcraft:deer_hide', 'troll': 'valcraft:troll_hide', 'iron': 'minecraft:iron_ingot', 'root': 'valcraft:ancient_bark',
       'wolf': 'valcraft:wolf_pelt', 'fenris': 'valcraft:fenris_hair', 'padded': 'valcraft:linen_thread', 'carapace': 'valcraft:carapace', 'eitr_weave': 'valcraft:refined_eitr',
       'eitr': 'valcraft:refined_eitr', 'flametal': 'valcraft:flametal', 'embla': 'valcraft:refined_eitr', 'ask': 'valcraft:asksvin_hide', 'bronze': 'valcraft:bronze_ingot',
       'silver': 'valcraft:silver_ingot', 'blackmetal': 'valcraft:black_metal', 'flint': 'minecraft:flint', 'stone': 'minecraft:cobblestone', 'wood': 'minecraft:oak_planks',
       'antler': 'valcraft:hard_antler', 'chitin': 'valcraft:chitin', 'copper': 'minecraft:copper_ingot', 'birch': 'valcraft:fine_wood', 'ash': 'valcraft:ashwood',
       'bone': 'valcraft:bone_fragments', 'frost': 'valcraft:freeze_gland', 'fire': 'valcraft:surtling_core', 'serpent': 'valcraft:scale_hide', 'crystal': 'valcraft:crystal',
       'poison': 'valcraft:ooze', 'obsidian': 'minecraft:obsidian', 'lox': 'valcraft:lox_pelt', 'linen': 'valcraft:linen_thread', 'feather': 'minecraft:feather',
       'gold': 'minecraft:gold_ingot', 'red': 'minecraft:red_wool', 'flower': 'minecraft:dandelion'}
PAT = {'sword': [' M ', ' M ', ' L '], 'greatsword': [' MM', 'MM ', 'L  '], 'knife': ['M', 'L'], 'mace': [' MM', ' LM', 'S  '], 'club': ['  M', ' M ', 'L  '],
       'sledge': ['MMM', 'MLM', ' S '], 'axe': ['MM', 'ML', ' S'], 'battleaxe': ['MMM', 'MLM', ' S '], 'pickaxe': ['MMM', ' L ', ' S '], 'hoe': ['MM ', ' L ', ' S '],
       'spear': ['  M', ' L ', 'S  '], 'atgeir': [' MM', ' LM', 'S  '], 'fist': ['M M', 'LML'], 'staff': ['  M', ' L ', 'S  '], 'bow': [' MW', 'L W', ' MW'],
       'crossbow': ['MLM', 'WMW', ' L '], 'shield': ['MLM', 'MMM', ' M '], 'helmet': ['MMM', 'MLM'], 'chestplate': ['MLM', 'MMM', 'MMM'], 'leggings': ['MMM', 'MLM', 'M M'],
       'boots': ['MLM', 'M M'], 'cape': ['MLM', 'MMM', 'M M'], 'helmet_misc': ['MMM', 'MLM']}
SMELT = {'silver_ore': 'valcraft:silver_ingot', 'black_metal_scrap': 'valcraft:black_metal', 'flametal_ore': 'valcraft:flametal', 'iron_ore': 'minecraft:iron_ingot',
         'scrap_iron': 'minecraft:iron_ingot', 'copper_scrap': 'minecraft:copper_ingot'}
COOK = {'boar_meat': 'cooked_boar_meat', 'deer_meat': 'cooked_deer_meat', 'neck_tail': 'grilled_neck_tail', 'raw_fish': 'cooked_fish', 'serpent_meat': 'cooked_serpent_meat',
        'wolf_meat': 'cooked_wolf_meat', 'valheim_egg': 'fried_egg', 'lox_meat': 'cooked_lox_meat', 'hare_meat': 'cooked_hare_meat', 'seeker_meat': 'cooked_seeker_meat',
        'asksvin_meat': 'cooked_asksvin_meat', 'volture_meat': 'cooked_volture_meat'}
TAG = {'sword': 'swords', 'greatsword': 'swords', 'knife': 'swords', 'mace': 'swords', 'club': 'swords', 'sledge': 'swords', 'spear': 'swords', 'atgeir': 'swords', 'fist': 'swords',
       'axe': 'axes', 'battleaxe': 'axes', 'pickaxe': 'pickaxes', 'hoe': 'hoes', 'helmet': 'head_armor', 'helmet_misc': 'head_armor', 'chestplate': 'chest_armor',
       'leggings': 'leg_armor', 'boots': 'foot_armor', 'arrow': 'arrows'}

def run(png, wj, A, D, R, lang, base_cs):
    entries, sets, tags, covered = [], [], {}, []
    def tex(n, kind, palname):
        rnd = random.Random(n); png(f'{A}/textures/item/{n}.png', 16, 16, draw(kind, rnd).pixels(ramp(BASE[palname]), rnd))
        wj(f'{A}/models/item/{n}.json', {'parent': f'minecraft:item/{KIND_MODEL.get(kind, "generated")}', 'textures': {'layer0': f'valcraft:item/{n}'}})
    def recipe(n, kind, palname):
        pat = PAT.get(kind)
        if not pat or palname not in MAT: return
        key = {'M': MAT[palname]}; flat = ''.join(pat)
        if 'L' in flat: key['L'] = 'minecraft:leather'
        if 'S' in flat: key['S'] = 'minecraft:stick'
        if 'W' in flat: key['W'] = 'minecraft:string'
        wj(f'{D}/valcraft/recipe/{n}.json', {'type': 'minecraft:crafting_shaped', 'category': 'equipment', 'pattern': pat, 'key': {k: {'item': v} for k, v in key.items()}, 'result': {'id': f'valcraft:{n}', 'count': 1}})
    vmap = []  # (valcraft id, [prefabs])
    for sname, (tier, pal, total, tough, ench, slots) in ARMOR_SETS.items():
        d = {s: max(1, round(total * SLOT_SHARE[s])) for s in SLOT_SHARE}
        sets.append({'name': sname, 'def': [d['boots'], d['leggings'], d['chestplate'], d['helmet']], 'tough': tough, 'ench': ench, 'dur': 8 + tier * 5})
        rnd = random.Random(sname); r = ramp(BASE[pal])
        png(f'{A}/textures/models/armor/{sname}_layer_1.png', 64, 32, armor_layer(r, rnd)); png(f'{A}/textures/models/armor/{sname}_layer_2.png', 64, 32, armor_layer(r, rnd, True))
        for slot in ('helmet', 'chestplate', 'leggings', 'boots'):
            if slot == 'helmet' and slot not in slots: continue
            n = f'{sname}_{slot}'; tex(n, slot, pal); recipe(n, slot, sname)
            entries.append({'id': n, 'kind': slot, 'tier': tier, 'set': sname}); tags.setdefault(TAG[slot], []).append(n)
            lang[f'item.valcraft.{n}'] = f'{ARMOR_NAMES[sname]} {"Chest" if slot == "chestplate" else slot.title()}'
            if slots.get(slot): vmap.append((n, [slots[slot]]))
            covered.append(n)
    sets.append({'name': 'cloth', 'def': [1, 1, 1, 1], 'tough': 0.0, 'ench': 20, 'dur': 6})
    r = ramp(BASE['linen']); rnd = random.Random('cloth')
    png(f'{A}/textures/models/armor/cloth_layer_1.png', 64, 32, armor_layer(r, rnd)); png(f'{A}/textures/models/armor/cloth_layer_2.png', 64, 32, armor_layer(r, rnd))
    for it in ITEMS:
        n, kind, tier, pal, prefabs, name = it[:6]; extra = it[6] if len(it) > 6 else {}
        tex(n, kind, pal); lang[f'item.valcraft.{n}'] = name
        e = {'id': n, 'kind': kind, 'tier': tier}
        if 'food' in extra: e['food'] = list(extra['food'])
        if kind == 'helmet_misc': e['set'] = 'cloth'
        entries.append(e)
        if kind in TAG: tags.setdefault(TAG[kind], []).append(n)
        if kind not in ('meat', 'cooked', 'fish', 'egg', 'bowl', 'bread', 'mead', 'berry', 'carrot', 'mushroom') or 'food' not in extra: recipe(n, kind, pal)
        if kind == 'arrow' and pal in MAT:
            wj(f'{D}/valcraft/recipe/{n}.json', {'type': 'minecraft:crafting_shapeless', 'category': 'equipment', 'ingredients': [{'item': i} for i in (MAT[pal], 'minecraft:stick', 'minecraft:feather', 'minecraft:string')], 'result': {'id': f'valcraft:{n}', 'count': 8}})
        if prefabs: vmap.append((n, prefabs))
        covered.append(n)
    for src, dst in SMELT.items():
        for k, t in (('smelting', 200), ('blasting', 100)): wj(f'{D}/valcraft/recipe/{dst.split(":")[1]}_from_{k}_{src}.json', {'type': f'minecraft:{k}', 'category': 'misc', 'ingredient': {'item': f'valcraft:{src}'}, 'result': {'id': dst}, 'experience': 0.7, 'cookingtime': t})
    for src, dst in COOK.items():
        for k, t in (('smelting', 200), ('smoking', 100), ('campfire_cooking', 600)): wj(f'{D}/valcraft/recipe/{dst}_from_{k}.json', {'type': f'minecraft:{k}', 'category': 'food', 'ingredient': {'item': f'valcraft:{src}'}, 'result': {'id': f'valcraft:{dst}'}, 'experience': 0.35, 'cookingtime': t})
    for tag, vals in tags.items():
        p = f'{D}/minecraft/tags/item/{tag}.json'
        old = json.load(open(p))['values'] if os.path.exists(p) else []
        wj(p, {'replace': False, 'values': sorted(set(old) | {f'valcraft:{v}' for v in vals})})
    for tag, ids in {'ingots/silver': ['silver_ingot'], 'ingots/black_metal': ['black_metal'], 'ingots/flametal': ['flametal'], 'ores/silver': [], 'raw_materials/silver': ['silver_ore']}.items():
        if ids: wj(f'{D}/c/tags/item/{tag}.json', {'replace': False, 'values': [f'valcraft:{i}' for i in ids]})
    wj(f'{R}/valcraft_items.json', {'sets': sets, 'items': entries})
    # ---- ItemMap defaults: append after vanilla stand-ins; new default only where there is no stand-in ----
    basej = json.load(open(os.path.join(os.path.dirname(__file__), 'itemmap_base.json')))
    basej.setdefault('FishingRod', ['minecraft:fishing_rod'])
    out = {k: list(v) for k, v in basej.items()}; appends = []
    for n, prefabs in vmap:
        for pf in prefabs:
            vid = f'valcraft:{n}'
            if pf in out:
                if vid not in out[pf]:
                    if pf in basej and len(basej[pf]) == 1: appends.append((pf, basej[pf][0], vid))
                    out[pf].append(vid)
            else: out[pf] = [vid]
    body = '{\n' + ',\n'.join(f'  ""{k}"": [{", ".join(chr(34) * 2 + x + chr(34) * 2 for x in v)}]' for k, v in out.items()) + '\n}'
    gen = ('        // <gen:defaults> generated by tools/gen_content.py from tools/valheim_items.py; do not edit by hand\n'
           f'        public const string DefaultJson = @"{body}";\n\n'
           '        /// <summary>0.11.0: entries still exactly at their old single default gain the new ValCraft item as an accepted alternative.</summary>\n'
           '        static readonly (string key, string oldId, string addId)[] Appends =\n        {\n' +
           ',\n'.join(f'            ("{k}", "{o}", "{a}")' for k, o, a in appends) + '\n        };\n        // </gen:defaults>\n')
    s = open(base_cs).read()
    if '// <gen:defaults>' in s: s = re.sub(r'        // <gen:defaults>.*?// </gen:defaults>\n', lambda m: gen, s, flags=re.S)
    else:
        i = s.index('        public const string DefaultJson'); k = s.index('}";', i) + 4
        s = s[:i] + gen + s[k:]
    open(base_cs, 'w').write(s)
    return covered, len(out), len(appends)
