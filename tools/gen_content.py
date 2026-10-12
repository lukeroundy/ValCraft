#!/usr/bin/env python3
"""0.10.0: generates ValCraft tin/bronze resources (original procedural pixel art, models, recipes, loot, tags, lang)."""
import json, os, random, struct, zlib
R = os.path.join(os.path.dirname(__file__), '..', 'neoforge-mod', 'src', 'main', 'resources')
A = os.path.join(R, 'assets', 'valcraft'); D = os.path.join(R, 'data')

def png(path, w, h, px):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    raw = b''.join(b'\x00' + b''.join(struct.pack('BBBB', *px[y][x]) for x in range(w)) for y in range(h))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))

def wj(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True); open(path, 'w').write(json.dumps(obj, indent=2) + '\n')

PAL = {'tin': [(70, 78, 92), (120, 130, 145), (170, 180, 192), (210, 218, 228), (245, 248, 252)],
       'bronze': [(78, 42, 18), (130, 76, 30), (186, 118, 52), (222, 160, 84), (250, 210, 140)]}
WOOD = [(60, 40, 20), (100, 70, 38), (137, 101, 58)]
T = (0, 0, 0, 0)

# masks: '.' empty, 1-5 metal shade (dark..light), w/W/x wood shades, o outline
def from_mask(rows, metal):
    px = []
    for r in rows:
        line = []
        for c in r.ljust(16, '.')[:16]:
            if c not in '12345owWx': line.append(T)
            elif c in '12345': line.append(PAL[metal][int(c) - 1] + (255,))
            elif c == 'o': line.append((30, 25, 25, 255))
            elif c in 'wWx': line.append(WOOD['wWx'.index(c)] + (255,))
        px.append(line)
    while len(px) < 16: px.append([T] * 16)
    return px

M = {
 'ingot': ['................','................','................','................','.....oooooooo...','....o44444443o..','...o4555544432o.','..o44444444332o.','..o33333333221o.','..o22222222211o.','...oooooooooooo.','................','................','................','................','................'],
 'nugget': ['................','................','................','................','................','......oo........','.....o54o.......','....o4433o..oo..','....o3322o.o43o.','.....oooo..o32o.','.......oo...oo..','......o43o......','......o32o......','.......oo.......','................','................'],
 'raw': ['................','................','.....ooo........','....o543oo......','...o44433 o.....','...o433332oo....','..o4433322 2o...','..o3332221112o..','..o3322211112o..','...o22211111o...','....o211111o....','.....ooooooo....','................','................','................','................'],
 'sword': ['............ooo.','...........o54o.','..........o54o..','.........o43o...','........o43o....','.......o32o.....','......o32o......','..o..o21o.......','..oo.o1o........','...ooxo.........','....xWo.........','...xWo.o........','..xWo...........','.oWo............','.oo.............','................'],
 'pickaxe': ['................','....oooooo......','...o5444433o....','..o44oooo332o...','..o3o...ox.o2o..','..oo...oxo..oo..','......oWo.......','.....oWo........','....oWo.........','...oWo..........','..oWo...........','.oWo............','.oo.............','................','................','................'],
 'axe': ['................','......ooo.......','.....o544o......','....o54433o.....','....o4433xo.....','.....o33xWo.....','......ooWo......','.....oWo........','....oWo.........','...oWo..........','..oWo...........','.oWo............','.oo.............','................','................','................'],
 'shovel': ['..........ooo...','.........o543o..','........o5432o..','........o4322o..','.........o21o...','........oxoo....','.......oWo......','......oWo.......','.....oWo........','....oWo.........','...oWo..........','..oWo...........','.oxo............','.oo.............','................','................'],
 'hoe': ['................','.......oooooo...','......o544332o..','.....oo3ooooo...','.....oxo........','....oWo.........','...oWo..........','..oWo...........','.oWo............','.oo.............','................','................','................','................','................','................'],
 'helmet': ['................','................','................','....oooooooo....','...o55444433o...','..o5444443332o..','..o4433333222o..','..o33oooooo21o..','..o32o....o21o..','..o21o....o11o..','..oooo....oooo..','................','................','................','................','................'],
 'chestplate': ['................','..ooo......ooo..','.o543o....o432o.','.o4433oooo3322o.','.o4443333332221o','..oo43333332oo..','...o43333322o...','...o33333222o...','...o33322221o...','...o33222211o...','...o22222111o...','...oooooooooo...','................','................','................','................'],
 'leggings': ['................','...oooooooooo...','...o54444333o...','...o43333222o...','...o3332o222o...','...o332o.o21o...','...o322o.o21o...','...o321o.o21o...','...o321o.o11o...','...o221o.o11o...','...oooo..oooo...','................','................','................','................','................'],
 'boots': ['................','................','................','................','................','....ooo...ooo...','....o43o..o43o..','....o32o..o32o..','....o32o..o32o..','..ooo32o.ooo2o..','.o44332o.o4321o.','.o33221o.o3211o.','.oooooo..oooooo.','................','................','................'],
}
random.seed(7)
def noise_tex(base, specks=None, border=False):
    px = []
    for y in range(16):
        line = []
        for x in range(16):
            c = base[min(len(base) - 1, max(0, 2 + random.choice([-1, 0, 0, 1])))]
            if border and (x in (0, 15) or y in (0, 15)): c = base[1]
            elif border and (x in (1, 14) or y in (1, 14)): c = base[3]
            line.append(c + (255,))
        px.append(line)
    if specks:
        for _ in range(7):
            x, y = random.randint(1, 13), random.randint(1, 13)
            for dx, dy, s in ((0, 0, 3), (1, 0, 2), (0, 1, 2), (1, 1, 1)): px[y + dy][x + dx] = specks[s] + (255,)
    return px
STONE = [(80, 80, 80), (100, 100, 100), (122, 122, 122), (140, 140, 140), (160, 160, 160)]

def armor_layer(metal):
    w, h = 64, 32; p = PAL[metal]
    return [[(p[max(0, min(4, 2 + random.choice([-1, 0, 0, 1]) - (1 if (x % 16 in (0, 15) or y % 16 in (0, 15)) else 0)))] + (255,)) for x in range(w)] for y in range(h)]

items = {}
for metal in ('tin', 'bronze'):
    for tool in ('sword', 'pickaxe', 'axe', 'shovel', 'hoe', 'helmet', 'chestplate', 'leggings', 'boots'):
        n = f'{metal}_{tool}'; png(f'{A}/textures/item/{n}.png', 16, 16, from_mask(M[tool], metal)); items[n] = 'handheld' if tool in ('sword', 'pickaxe', 'axe', 'shovel', 'hoe') else 'generated'
    png(f'{A}/textures/models/armor/{metal}_layer_1.png', 64, 32, armor_layer(metal))
    png(f'{A}/textures/models/armor/{metal}_layer_2.png', 64, 32, armor_layer(metal))
for n, mask, metal in (('tin_ingot', 'ingot', 'tin'), ('tin_nugget', 'nugget', 'tin'), ('raw_tin', 'raw', 'tin'), ('bronze_ingot', 'ingot', 'bronze')):
    png(f'{A}/textures/item/{n}.png', 16, 16, from_mask(M[mask], metal)); items[n] = 'generated'
# placeholder: parchment with a rune
rune = ['................','..oooooooooooo..','.o5555555555554o','.o5544444444454o','.o5444o44o44454o','.o54444o4o44444o','.o544444o444444o','.o54444o4o44444o','.o5444o444o4444o','.o54444444444444','.o5544444444454o','.o5555555555554o','..oooooooooooo..','................','................','................']
PAL['parch'] = [(90, 60, 30), (150, 110, 60), (190, 150, 90), (222, 196, 140), (240, 222, 180)]
png(f'{A}/textures/item/valheim_item.png', 16, 16, from_mask(rune, 'parch')); items['valheim_item'] = 'generated'
png(f'{A}/textures/block/tin_ore.png', 16, 16, noise_tex(STONE, PAL['tin']))
png(f'{A}/textures/block/bronze_block.png', 16, 16, noise_tex(PAL['bronze'], border=True))

for n, kind in items.items(): wj(f'{A}/models/item/{n}.json', {'parent': f'minecraft:item/{kind}', 'textures': {'layer0': f'valcraft:item/{n}'}})
for b in ('tin_ore', 'bronze_block'):
    wj(f'{A}/blockstates/{b}.json', {'variants': {'': {'model': f'valcraft:block/{b}'}}})
    wj(f'{A}/models/block/{b}.json', {'parent': 'minecraft:block/cube_all', 'textures': {'all': f'valcraft:block/{b}'}})
    wj(f'{A}/models/item/{b}.json', {'parent': f'valcraft:block/{b}'})

lang = {'item.valcraft.raw_tin': 'Raw Tin', 'item.valcraft.tin_ingot': 'Tin Ingot', 'item.valcraft.tin_nugget': 'Tin Nugget', 'item.valcraft.bronze_ingot': 'Bronze Ingot',
        'item.valcraft.valheim_item': 'Valheim Item', 'block.valcraft.tin_ore': 'Tin Ore', 'block.valcraft.bronze_block': 'Block of Bronze', 'entity.valcraft.valheim_proxy': 'Valheim Creature'}
for metal in ('tin', 'bronze'):
    for tool in ('sword', 'pickaxe', 'axe', 'shovel', 'hoe', 'helmet', 'chestplate', 'leggings', 'boots'):
        lang[f'item.valcraft.{metal}_{tool}'] = f'{metal.title()} {tool.title()}'
wj(f'{A}/lang/en_us.json', lang)

def drop_self(b): return {'type': 'minecraft:block', 'pools': [{'rolls': 1, 'entries': [{'type': 'minecraft:item', 'name': f'valcraft:{b}'}], 'conditions': [{'condition': 'minecraft:survives_explosion'}]}]}
wj(f'{D}/valcraft/loot_table/blocks/bronze_block.json', drop_self('bronze_block'))
wj(f'{D}/valcraft/loot_table/blocks/tin_ore.json', {'type': 'minecraft:block', 'pools': [{'rolls': 1, 'entries': [{'type': 'minecraft:alternatives', 'children': [
    {'type': 'minecraft:item', 'name': 'valcraft:tin_ore', 'conditions': [{'condition': 'minecraft:match_tool', 'predicate': {'predicates': {'minecraft:enchantments': [{'enchantments': 'minecraft:silk_touch', 'levels': {'min': 1}}]}}}]},
    {'type': 'minecraft:item', 'name': 'valcraft:raw_tin', 'functions': [{'function': 'minecraft:apply_bonus', 'enchantment': 'minecraft:fortune', 'formula': 'minecraft:ore_drops'}, {'function': 'minecraft:explosion_decay'}]}]}]}]})
wj(f'{D}/minecraft/tags/block/mineable/pickaxe.json', {'replace': False, 'values': ['valcraft:tin_ore', 'valcraft:bronze_block']})
wj(f'{D}/minecraft/tags/block/needs_stone_tool.json', {'replace': False, 'values': ['valcraft:tin_ore', 'valcraft:bronze_block']})
for tag, vals in {'ingots/tin': ['valcraft:tin_ingot'], 'ingots/bronze': ['valcraft:bronze_ingot'], 'raw_materials/tin': ['valcraft:raw_tin'], 'nuggets/tin': ['valcraft:tin_nugget'], 'ores/tin': ['valcraft:tin_ore'], 'storage_blocks/bronze': ['valcraft:bronze_block']}.items():
    wj(f'{D}/c/tags/item/{tag}.json', {'replace': False, 'values': vals})
for tag, vals in {'swords': 'sword', 'pickaxes': 'pickaxe', 'axes': 'axe', 'shovels': 'shovel', 'hoes': 'hoe', 'head_armor': 'helmet', 'chest_armor': 'chestplate', 'leg_armor': 'leggings', 'foot_armor': 'boots'}.items():
    wj(f'{D}/minecraft/tags/item/{tag}.json', {'replace': False, 'values': [f'valcraft:tin_{vals}', f'valcraft:bronze_{vals}']})
wj(f'{D}/c/tags/block/ores/tin.json', {'replace': False, 'values': ['valcraft:tin_ore']})

RC = f'{D}/valcraft/recipe'
def shaped(name, pattern, key, result, count=1, cat='equipment'):
    wj(f'{RC}/{name}.json', {'type': 'minecraft:crafting_shaped', 'category': cat, 'pattern': pattern, 'key': {k: {'item': v} for k, v in key.items()}, 'result': {'id': result, 'count': count}})
def shapeless(name, ings, result, count=1, cat='misc'):
    wj(f'{RC}/{name}.json', {'type': 'minecraft:crafting_shapeless', 'category': cat, 'ingredients': [{'item': i} for i in ings], 'result': {'id': result, 'count': count}})
for kind, t in (('smelting', 200), ('blasting', 100)):
    for src in ('valcraft:raw_tin', 'valcraft:tin_ore'):
        wj(f'{RC}/tin_ingot_from_{kind}_{src.split(":")[1]}.json', {'type': f'minecraft:{kind}', 'category': 'misc', 'ingredient': {'item': src}, 'result': {'id': 'valcraft:tin_ingot'}, 'experience': 0.7, 'cookingtime': t})
shapeless('bronze_ingot', ['minecraft:copper_ingot', 'minecraft:copper_ingot', 'valcraft:tin_ingot'], 'valcraft:bronze_ingot')
shapeless('tin_nugget', ['valcraft:tin_ingot'], 'valcraft:tin_nugget', 9)
shaped('tin_ingot_from_nuggets', ['NNN', 'NNN', 'NNN'], {'N': 'valcraft:tin_nugget'}, 'valcraft:tin_ingot', cat='misc')
shaped('bronze_block', ['III', 'III', 'III'], {'I': 'valcraft:bronze_ingot'}, 'valcraft:bronze_block', cat='building')
shapeless('bronze_ingot_from_block', ['valcraft:bronze_block'], 'valcraft:bronze_ingot', 9)
PAT = {'sword': [' M ', ' M ', ' S '], 'pickaxe': ['MMM', ' S ', ' S '], 'axe': ['MM ', 'MS ', ' S '], 'shovel': [' M ', ' S ', ' S '], 'hoe': ['MM ', ' S ', ' S '],
       'helmet': ['MMM', 'M M'], 'chestplate': ['M M', 'MMM', 'MMM'], 'leggings': ['MMM', 'M M', 'M M'], 'boots': ['M M', 'M M']}
for metal in ('tin', 'bronze'):
    for tool, pat in PAT.items():
        key = {'M': f'valcraft:{metal}_ingot'}
        if 'S' in ''.join(pat): key['S'] = 'minecraft:stick'
        shaped(f'{metal}_{tool}', pat, key, f'valcraft:{metal}_{tool}')
print('generated')
