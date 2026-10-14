"""0.15.1: crisp 16x16 Minecraft-style item art for the Valheim items.
Templates are ASCII masks of regions; each region gets a 5-tone hue-shifted ramp (tone 0 = outline).
lowercase region = auto-shaded (light from top-left), UPPERCASE = highlight, region+'_' via DARK map = shadow tone.
A coloured dark outline is added around every shape. No blur, no anti-aliasing."""
import random
import colorsys

def clamp(v): return max(0, min(255, int(round(v))))
def ramp(c):
    r, g, b = [v / 255 for v in c]; h, l, s = colorsys.rgb_to_hls(r, g, b)
    out = []
    for i, (dl, dh, ds) in enumerate(((-0.62, 0.035, 0.1), (-0.30, 0.02, 0.05), (-0.13, 0.01, 0.0), (0.0, 0, 0), (0.17, -0.02, -0.08))):
        ll = max(0.03, min(0.97, l + dl * (l if dl < 0 else (1 - l)) * 1.8 if i != 0 else l * 0.32))
        hh = (h + (dh if h > 0.5 or h < 0.15 else -dh)) % 1.0
        rr, gg, bb = colorsys.hls_to_rgb(hh, ll, max(0, min(1, s + ds)))
        out.append((clamp(rr * 255), clamp(gg * 255), clamp(bb * 255)))
    return out

DARK = {'k': 'm', 'x': 'h', 'z': 'a', 'q': 'b'}   # explicit shadow pixels of a region
# Region keys: m material, h wood handle, g leather grip, a accent, b second accent, s string/white, e eye black, o forced outline
def render(rows, pals):
    H, W = 16, 16
    if 'm' not in pals: pals = dict(pals, m=next(iter(pals.values())))
    grid = [list(r.ljust(16, '.')[:16]) for r in rows] + [['.'] * 16 for _ in range(16 - len(rows))]
    def filled(x, y): return 0 <= x < W and 0 <= y < H and grid[y][x] != '.'
    px = [[(0, 0, 0, 0)] * W for _ in range(H)]
    for y in range(H):
        for x in range(W):
            c = grid[y][x]
            if c == '.': continue
            if c == 'e': px[y][x] = (22, 16, 18, 255); continue
            if c == 'o': px[y][x] = pals['m'][0] + (255,); continue
            if c in DARK: px[y][x] = pals[DARK[c]][1] + (255,); continue
            reg = c.lower(); p = pals.get(reg, pals['m'])
            if c.isupper(): t = 4
            else:
                t = 2
                lit = (not filled(x, y - 1)) or (not filled(x - 1, y)); shade = (not filled(x, y + 1)) or (not filled(x + 1, y))
                if lit and not shade: t = 3
                elif shade and not lit: t = 1
            px[y][x] = p[t] + (255,)
    # outline
    out = [row[:] for row in px]
    for y in range(H):
        for x in range(W):
            if grid[y][x] != '.': continue
            for dx, dy in ((0, 1), (1, 0), (0, -1), (-1, 0)):
                nx, ny = x + dx, y + dy
                if filled(nx, ny):
                    c = grid[ny][nx].lower(); c = DARK.get(c, c)
                    p = pals.get(c, pals['m']) if c not in 'eo' else pals['m']
                    out[y][x] = p[0] + (255,); break
    return out

T = {}
def t(name, s):
    rows = s.split('\n')[1:]
    while rows and rows[-1] == '': rows.pop()
    T[name] = rows
# ---------------- weapons ----------------
t('sword', '''
..............
.............mW
............mWm
...........mWm
..........mWm
.........mWm
........mWm
.......mWm
..a...mWm
..aa.mWm
...aamm
....gaa
...gg.aa
..gg
.ag
..''')
t('greatsword', '''
..............mW
.............mWm
............mWmm
...........mWmm
..........mWmm
.........mWmm
........mWmm
.......mWmm
..a...mWmm
..aa.mWmm
...aammm
....gaaa
...gg..aa
..gg
.ag
''')
t('knife', '''



...........mW
..........mWm
.........mWm
........mWm
.......mWm
......mmm
....aamm
.....gg
....gg
...gg
..ag
''')
t('axe', '''

.........mm
........mWmm
.......mWmmmm
.......mWmmmmm
........mmmkmm
.........hkmm
........hh.mm
.......hh...m
......hh
.....hh
....hh
...gg
..gg
.hg
''')
t('battleaxe', '''
...mm.....
..mWmm.....mm
..mWmmm...mmm
...mmmmmhmmmm
....mmmhhmmm
.....mmhmmm
......hhkm
.....hh..m
....hh
...hh
..gg
.gg
.h
''')
t('mace', '''
...........a
.........mmmm
........mWmmmm
.......amWmmmma
........mmmkmm
........mmkkmm
.........hmmm
........hh..a
.......hh
......hh
.....hh
....hh
...gg
..gg
.hg
''')
t('club', '''

..........hh
.........hHhh
........hHhhh
........hhhxh
.........hxhh
........hh.h
.......hh
......hh
.....hh
....hh
...hh
..gg
.gg
.h
''')
t('sledge', '''
.......mmmmmm
......mWWmmmmm
......mWmmmmkm
......mmmmmkkm
.......mmhmmmm
........hh
.......hh
......hh
.....hh
....hh
...hh
..gg
.gg
.h
''')
t('pickaxe', '''

.....mmmmm
....mWWmmmmm
...mm..hhh.mmm
..mm...hh....mm
..m...hhh.....m
.....hh..
....hh
...hh
..hh
.hh
.g
''')
t('hoe', '''

........mmmm
.......mWmmmm
..........hmm
.........hh.m
........hh
.......hh
......hh
.....hh
....hh
...hh
..gg
.gg
.h
''')
t('spear', '''
..............m
............mWm
...........mWm
..........mWmm
...........mm
..........h
.........h
........h
.......h
......h
.....h
....g
...g
..g
.h
''')
t('atgeir', '''
...........mm.m
..........mWmmm
.........mWmmm
........mWmmm
.......mmmm
........hmm
.......h..m
......h
.....h
....h
...g
..g
.g
h
''')
t('fist', '''

...m..m..m
...mm.mm.mm
...mm.mm.mm
...mW.mW.mW
..ggggggggg
..gGgggggggg
..ggggggggggg
..gggggggggg
...gggggggg
...aaaaaaaa
....gggggg
''')
t('staff', '''
..........aaa
.........aAAaa
.........aAaaa
.........aaaa
..........hh
.........hh
........hh
.......hh
......hh
.....hh
....hh
...hh
..hh
.hh
.h
''')
t('bow', '''
....hhh
...h...hh
.......s.h
.......s..h
.......s...h
.......s...h
.......s....h
.......g....g
.......g....g
.......s....h
.......s...h
.......s...h
.......s..h
.......s.h
...h...hh
....hhh
''')
t('crossbow', '''
.mm..........mm
..mm........mm
...mm.hh...mm
....mmhhhmmm
.....s.hhh.s
......s.hh.s
.......shh.s
........hhs
.........hh
..........hh
...........hh
............hg
.............g
''')
t('arrow', '''
............mW
...........mWm
...........mm
..........h
.........h
........h
.......h
......h
.....h
...a.h
..a.h
..aha
..ha
.h
''')
t('bolt', '''

...........mW
..........mWm
..........mm
.........h
........h
.......h
......h
.....h
....ah
....aa
''')
t('shield_round', '''
.....mmmmmm
...mmhhhhhhmm
..mhhhHhhhhhm
.mhhHhhxhhhhhm
.mhHhhxhhhhhhm
mhhhhxhhaahhhhm
mhhhxhhaAaahhhm
mhhhhhhaaahhhhm
mhhhhhhhaahhxhm
.mhhhhhhhhhxhm
.mhhhhhhhhxhhm
..mhhhhhhhhhm
...mmhhhhhmm
.....mmmmmm
''')
t('shield_metal', '''
.....mmmmmm
...mmmmmmmmmm
..mmWmmmmmmmmm
.mmWmmmmmmmmmmm
.mWmmmmmmmmmmmm
mmmmmmmaammmmmm
mmmmmmaAaammmmm
mmmmmmaaaammmmm
mmmmmmmaammmmkm
.mmmmmmmmmmmkm
.mmmmmmmmmmkmm
..mmmmmmmmmmm
...mmmmmmmmm
.....mmmmmm
''')
t('shield_tower', '''
..mmmmmmmmmmm
..mhhhhhhhhhm
..mhHhhhhhhhm
..mhhhhhhhhhm
..mhhhhhhhhhm
..mmmmmmmmmmm
..mhhhhhhhhhm
..mhhhhhhhhhm
..mhhhhhhhhhm
..mmmmmmmmmmm
..mhhhhhhhhhm
...mhhhhhhhm
....mhhhhhm
.....mmmmm
''')
t('shield_kite', '''
..mmmmmmmmmmm
..mmWmmmmmmmm
..mWmmmaammmm
..mmmmaAaammm
..mmmmmaammmm
..mmmmmmmmmkm
...mmmmmmmmkm
...mmmmmmmkm
....mmmmmmkm
....mmmmmmm
.....mmmmm
......mmm
.......m
''')
t('buckler', '''


......mmm
....mmmmmmm
...mmWmmmmmm
...mWmmmmmmm
..mmmmaaammmm
..mmmmaAammmm
..mmmmaaammmm
...mmmmmmmkm
...mmmmmmmkm
....mmmmmmm
......mmm
''')
# ---------------- armour icons ----------------
t('helmet', '''


....mmmmmm
...mWWmmmmm
..mWmmmmmmmm
..mmmmmmmmmm
..mmmmmmmmkm
..mmm.mm.mmm
..mm..mm..mm
..mm..mm..mm
..mm......mm
''')
t('helmet_horn', '''
.s............s
.ss..........ss
..ss........ss
...smmmmmmmmss
...mWWmmmmmmm
..mWmmmmmmmmmm
..mmmmmmmmmmkm
..mmm.mm.mmmkm
..mm..mm..mmm
..mm..mm..mm
..mm......mm
''')
t('hood', '''

.....aaaaa
....aAaaaaa
...aAaaaaaaa
...aaammmaaa
..aaam...maaa
..aam.....maa
..aam.....maa
..aaam...maaa
..aaaaaaaaaaa
...aaaaaaaaa
''')
t('crown', '''




..a..a..a..a
..aa.aa.aa.aa
..aAaaAaaAaaa
..mmmmmmmmmmm
..mbmmbmmbmmm
..mmmmmmmmmmm
''')
t('hat', '''


.....mmmmm
....mWmmmmm
....mmmmmmm
....mmmmmmk
...aaaaaaaaa
.mmmmmmmmmmmmm
.mmmmmmmmmmmmm
''')
t('chestplate', '''

..mmm......mmm
.mWWmm....mmmmm
.mWmmmaaaammmkm
.mmmmmmmmmmmmkm
..mmmmmmmmmmmm
...mmmmmmmmmm
...mmmmmmmmmm
...gggggggggg
...mmmmmmmmmm
...mmmmmmmmmm
...mmmmmmmmmm
''')
t('leggings', '''

...gggggggggg
...mmmmmmmmmm
...mWmmmmmmkm
...mmmm..mmmm
...mmmm..mmmm
...mmmk..mmmk
...mmmk..mmmk
...mmmk..mmmk
...mmmk..mmmk
...aaaa..aaaa
''')
t('boots', '''




...mmm....mmm
...mWm....mWm
...mmm....mmm
...mmk....mmk
...aaa....aaa
.mmmmk..mmmmk
.mmmmk..mmmmk
''')
t('cape', '''
....aaaaaaa
...aAAaaaaaa
...mmmmmmmmm
...mWmmmmmmm
...mmmmmmmmm
...mmmmmmmmm
..mmmmmmmmmmm
..mmmmmmmmmkm
..mmmmmmmmmkm
..mmmmmmmmmmm
..mmmmmmmmmmm
.mmmmmmmmmmmmm
.mm.mmm.mmm.mm
''')
# ---------------- materials ----------------
t('log', '''


..mmmmmmmmmaaa
.mWmmmmmmmaAaaa
.mmmmkmmmmaabaa
.mmmmmmmmmaabaa
.mmkmmmmmmaabaa
.mmmmmmkmmaaaaa
.mmmmmmmmmmaaa
..mmmmmmmmm
''')
t('ingot', '''




....mmmmmmmmm
...mWWWWWWWmm
..mWmmmmmmmmkm
.mmmmmmmmmmkmm
.mmmmmmmmmkkm
.mmmmmmmmkkm
''')
t('raw', '''


.....mmm
....mWmmm.mm
...mWmmmmmmmm
...mmmammmmkm
..mmmmmmamkm
..mmamkmmmmm
..mmmmmmkmm
...mmmmkmm
....mmmm
''')
t('orb', '''


......mmmm
.....mWWmmm
....mWmmmmmm
....mWmmmmmm
....mmmmmmmm
....mmmmmmkm
.....mmmmkm
......mmmm
''')
t('egg', '''

......mmm
.....mWmmm
....mWmmmmm
....mWmmmmm
...mmmmmmmmm
...mmmmmmmkm
...mmmmmmmkm
....mmmmmkm
.....mmmmm
''')
t('berry', '''

.......hh
......h.a
.....aaaaa
....mmaaamm
...mWmmmmWmm
...mmmmmmmmm
..mmWmmmmWmm
..mmmmmmmmkm
...mmmmmmkm
....mmmmmm
''')
t('coin', '''



.........mmm
........mWmmm
....mmm.mmmkm
...mWmmm.mmm
...mmmmkm
..mmmmmmmmm
..mmWmmmmmmm
.mmmmmmmmmkkm
.mmmmmmmmkkm
''')
t('blob', '''



.......mm
......mWmm
.....mWmmmm
....mWmmmmmm
...mmmmmmmmmm
...mmmmmmmmkm
...mmmmmmmkkm
....mmmmmkkm
''')
t('powder', '''

......aaaa
.....aAaaaa
....mmmmmmmm
...hhhhhhhhhh
...hmmmmmmmh
...hmWmmmmmh
...hmmmmmmmh
....hmmmmmh
.....hhhhh
''')
t('gem', '''


......mmmm
.....mWWmmm
....mWWmmmmm
....mmmmmmkm
.....mmmmkm
......mmkm
.......mm
''')
t('seed', '''



......mm
.....mWm..mm
.....mmk.mWm
.........mmk
...mm
..mWm..mm
..mmk.mWm
......mmk
''')
t('hide', '''


..mm......mm
..mmmmmmmmmm
...mWmmmmmm
...mWmmmmkm
...mmmmmmkm
...mmmmmmkm
..mmmmmmmmmm
..mm......mm
''')
t('thread', '''


.....mmmmm
....mWmmmmm
...hhhhhhhhh
....mmmmmmm
...hhhhhhhhh
....mmmmmmm
...hhhhhhhhh
....mmmmmkm
.....mmmmm
''')
t('bone', '''

...........mm
..........mWmm
...........mmm
..........mm
.........mm
........mm
.......mm
......mm
.....mm
....mm
..mmm
.mmWm
..mm
''')
t('shard', '''

..........m
.........mW
........mWm
.......mWmm
......mWmmk
.....mWmmk
....mmmmk
...mmmkk
...mmk
''')
t('feather', '''
............mm
..........mmWm
.........mWmmm
........mWmmmm
.......mWmmmm
......mWmmmm
.....mWmmmm
....mmmmmm
....smmmm
...s.mmm
..s
.s
''')
t('nail', '''


..mmmm.mmmm
...mW...mW
...mm...mm
...mm...mm
...mk...mk
...mk...mk
....k....k
''')
t('chain', '''

..........mmm
.........m..m
.........m..m
.......mmmmm
......m..m
......m..m
....mmmmm
...m..m
...m..m
.mmmmm
.m..m
.m..m
.mmmm
''')
t('plant', '''

......m..m
.....mm.mm
..m..mm.mm..m
..mm.mmmm..mm
...mmmmmmmmm
....mmmmmmm
......mm
......hh
......hh
''')
t('mushroom', '''


....mmmmmm
...mWmamamm
..mWmmmmmamm
..mmammmmmmm
..mmmmmmmmkm
.....hhh
.....hhh
.....hhh
....hhhh
''')
t('meat', '''


......mmmm
.....mWmmmm
....mWmmmmmm
....mmmammmmm
....mmmmmmmmm
.....mmmmmmkm
......mmmmkm
....ss.mmmm
..sss
..ss
''')
t('fish', '''


...........m
.....mmmm.mm
...mmWmmmmmm
..mWmmmmmmmm
..memmmmmmmm
...mmmmmmkmm
.....mmmm.mm
...........m
''')
t('carrot', '''
...........a.a
..........aa.a
...........aa
.........mmm
........mWmm
.......mWmm
......mmmm
.....mmmk
....mmmk
...mmk
..mk
''')
t('bowl', '''




..aaaaaaaaaa
.aAaaAaaaaaaa
.hhhhhhhhhhhh
.hHhhhhhhhhxh
..hhhhhhhhhxh
...hhhhhhhh
.....hhhh
''')
t('bread', '''



.....mmmmm
...mmWmmmmmm
..mWmmammammm
..mmmmmmmmmkm
..mmmmmmmmkkm
...mmmmmkkm
''')
t('mead', '''

......hhh
......xxx
......sss
.....s...s
....s.....s
...saaaaaaas
...saAaaaaas
...saaaaaaas
...saaaaazas
...saaaaazas
....saaazas
.....sssss
''')
t('block_item', '''

..mmmmmmmmmmmm
..mWWWWWWWWWmm
..mWmmmmmmmmkm
..mWmmmkmmmmkm
..mWmmmmmmmmkm
..mWmmmmmmkmkm
..mWmkmmmmmmkm
..mWmmmmmmmmkm
..mmmmmmmmmmkm
..mkkkkkkkkkkm
''')
# ---------------- trophies (hand-tuned heads) ----------------
t('trophy', '''


.....mmmmm
....mWmmmmm
...mWmmmmmmm
...mmemmmemm
...mmmmmmmmm
....mmmmmmm
.....maaam
......mmm
''')
t('trophy_boar', '''

...m........m
...mm......mm
....mmmmmmmm
...mWmmmmmmmm
...mmemmmmemm
...mmmmmmmmmm
....mmaaaamm
...s.aaeeaa.s
...ss.aaaa.ss
.......mm
''')
t('trophy_deer', '''
.s.s.......s.s
.sss.......sss
..ss.......ss
...s..mmm..s
....smmmmms
.....mWmmm
.....memem
.....mmmmm
......mmm
......mam
.......a
''')
t('trophy_eikthyr', '''
s.s.s.....s.s.s
sssss.....sssss
.ssss.....ssss
...ss.mmm.ss
....smmmmms
.....mWmmm
.....mamam
.....mmmmm
......mmm
......mmm
.......m
''')
t('trophy_skeleton', '''


.....mmmmm
....mWmmmmm
...mWmmmmmmm
...mmeemeemm
...mmeemeemm
....mmmemmm
.....mmmmm
.....memem
......mmm
''')
t('trophy_elder', '''
..h.h...h..h
..hhh.h.hhhh
...hhhhhhhh
...mmmmmmmm
..mWmmmmmmmm
..mmaammaamm
..mmmmmmmmmm
..mmmmkkmmmm
...mekeekem
...mmmmmmmm
....mmmmmm
''')
t('trophy_bonemass', '''

....aaaaaaa
...aAaaaaaaa
..aammmmmmmaa
..aaWmmmmmmaa
..ammeemmeema
..ammeemmeema
..aammmemmmaa
...ammmmmmma
...aamememaa
....aammmaa
.....aaaaa
''')
t('trophy_moder', '''

..s.........s
..ss.......ss
...smmmmmmms
..mmWmmmmmmmm
..mmaemmmeamm
...mmmmmmmmm
....mmmmmmm
.....mmmmm
......sss
......s.s
''')
t('trophy_yagluth', '''
...a.a.a.a.a
...aaaaaaaaa
....mmmmmmm
...mWmmmmmmm
...mmeemmeemm
...maammmaamm
...mmmmemmmmm
....mmmmmmmm
....memememm
.....mmmmmm
''')
t('trophy_queen', '''
..a.........a
...a.......a
....ammmmma
...mmWmmmmmm
..mmaammmaamm
..mmaammmaamm
...mmmmmmmmm
....mmmmmmm
...ss.mmm.ss
..ss.......ss
''')
t('trophy_fader', '''
.s...........s
.ss.........ss
..ss.......ss
...smmmmmmms
..mmWmmmmmmmm
..mmaammmaamm
..mmmmmmmmmmm
...mmmmmmmmm
....meeeeem
....msssssm
.....mmmmm
''')
t('rune', '''

...aaaaaaaaa
..aAaaaaaaaaa
..aammmmmmmaa
..aamaaaaamaa
..aaamaaamaaa
..aaaamamaaaa
..aaaaamaaaaa
..aaaaamaaaaa
..aaaaamaaaaa
..aaaaaaaaaaa
...aaaaaaaaa
''')

# ---------------- item -> template + palettes ----------------
WOOD = (150, 105, 60); GRIP = (95, 60, 38); STRING = (225, 222, 208); IRON = (165, 170, 180); GLASS = (205, 225, 235); GREEN = (95, 150, 60)
TROPHY = {'trophy_boar': ((135, 90, 65), (215, 150, 140), (235, 230, 210)), 'trophy_deer': ((160, 110, 70), (90, 60, 40), (225, 205, 170)),
          'trophy_eikthyr': ((70, 80, 105), (140, 225, 255), (230, 220, 190)), 'trophy_skeleton': ((225, 220, 200), (90, 80, 70), (0, 0, 0)),
          'trophy_elder': ((120, 85, 50), (150, 235, 90), (0, 0, 0), (90, 140, 60)), 'trophy_bonemass': ((215, 215, 190), (110, 165, 50), (0, 0, 0)),
          'trophy_moder': ((205, 225, 245), (60, 140, 230), (240, 240, 245)), 'trophy_yagluth': ((175, 165, 150), (250, 140, 40), (0, 0, 0)),
          'trophy_queen': ((70, 95, 120), (190, 90, 230), (210, 200, 170)), 'trophy_fader': ((80, 65, 60), (255, 130, 30), (235, 225, 205))}
SETS = {  # armour icon + layer style: m main, a trim, g belt/strap, helm template, pattern, cape colour
 'rag': dict(m=(150, 130, 100), a=(115, 98, 72), g=(175, 145, 95), helm='hood', pat='rag'),
 'leather': dict(m=(140, 92, 52), a=(185, 150, 105), g=(80, 50, 30), helm='helmet', pat='leather', cape=(170, 130, 90)),
 'troll': dict(m=(72, 105, 145), a=(50, 75, 110), g=(70, 50, 35), helm='hood', pat='hide', cape=(60, 92, 130)),
 'tin': dict(m=(170, 180, 192), a=(110, 80, 50), g=(80, 50, 30), helm='helmet', pat='plate'),
 'bronze': dict(m=(190, 122, 55), a=(110, 72, 42), g=(80, 50, 30), helm='helmet', pat='plate'),
 'iron': dict(m=(160, 165, 175), a=(85, 60, 40), g=(70, 48, 30), helm='helmet', pat='scale'),
 'root': dict(m=(100, 112, 62), a=(70, 85, 40), g=(85, 60, 35), helm='helmet', pat='bark', eyes=(170, 240, 90)),
 'wolf': dict(m=(200, 210, 225), a=(238, 238, 242), g=(90, 90, 100), helm='helmet_horn', pat='chain', cape=(232, 232, 238), horn=(235, 230, 215)),
 'fenris': dict(m=(95, 85, 95), a=(55, 50, 58), g=(120, 90, 60), helm='hood', pat='fur', cape=(70, 62, 72)),
 'padded': dict(m=(175, 155, 115), a=(150, 155, 165), g=(90, 60, 40), helm='helmet', pat='quilt'),
 'carapace': dict(m=(60, 125, 140), a=(35, 70, 85), g=(40, 40, 50), helm='helmet', pat='chitin'),
 'eitr_weave': dict(m=(60, 80, 150), a=(100, 190, 235), g=(45, 45, 70), helm='hood', pat='robe'),
 'flametal': dict(m=(75, 65, 68), a=(240, 130, 45), g=(50, 40, 40), helm='helmet', pat='flame'),
 'embla': dict(m=(150, 40, 52), a=(235, 195, 80), g=(70, 30, 35), helm='hood', pat='robe'),
 'cloth': dict(m=(225, 218, 195), a=(200, 190, 165), g=(150, 120, 80), helm='hood', pat='robe'),
 'ask': dict(m=(115, 92, 70), a=(160, 55, 42), g=(60, 45, 35), helm='hood', pat='hide', cape=(150, 60, 45)),
}
def P(**kw): return {k: ramp(v) for k, v in kw.items()}

def icon(name, kind, base, BASE):
    """Returns 16x16 RGBA rows for item `name` of `kind` whose material colour is `base`."""
    m = base; lighter = tuple(min(255, v + 45) for v in base)
    if name in TROPHY:
        c = TROPHY[name]; pals = P(m=c[0], a=c[1], s=c[2] if c[2] != (0, 0, 0) else c[0], h=c[3] if len(c) > 3 else c[0])
        return render(T[name], pals)
    if kind == 'trophy': return render(T['trophy'], P(m=m, a=lighter))
    for sn in sorted(SETS, key=len, reverse=True):
        if kind in ('helmet', 'chestplate', 'leggings', 'boots') and name.startswith(sn + '_'):
            st = SETS[sn]; tmpl = st['helm'] if kind == 'helmet' else kind
            pals = P(m=st['m'], a=st['a'] if tmpl != 'hood' else st['m'], g=st['g'], s=st.get('horn', st['a']))
            if tmpl == 'hood': pals['m'] = ramp(st['a'])
            return render(T[tmpl], pals)
    if kind == 'helmet_misc':
        tm = {'dverger_circlet': 'crown', 'midsummer_crown': 'crown', 'hood_of_odin': 'hood', 'yule_hat': 'hat', 'fishing_hat': 'hat', 'pointy_hat': 'pointy'}.get(name, 'helmet')
        acc = {'midsummer_crown': (110, 170, 70), 'yule_hat': (240, 240, 240), 'dverger_circlet': (230, 190, 60), 'fishing_hat': (90, 60, 40), 'pointy_hat': (230, 200, 90)}.get(name, lighter)
        if tm == 'crown': return render(T[tm], P(m=(200, 160, 50) if name == 'dverger_circlet' else (90, 140, 55), a=acc if name != 'midsummer_crown' else (240, 220, 90), b=(120, 210, 240) if name == 'dverger_circlet' else (230, 90, 120)))
        if tm == 'pointy': return pointy_icon(m, acc)
        return render(T[tm], P(m=m, a=acc if tm == 'hat' else m))
    if kind == 'cape':
        return render(T['cape'], P(m=m, a=tuple(min(255, v + 30) for v in m) if name not in ('wolf_fur_cape', 'lox_cape') else (235, 230, 220)))
    metal_like = kind in ('sword', 'greatsword', 'knife', 'axe', 'battleaxe', 'mace', 'sledge', 'pickaxe', 'hoe', 'spear', 'atgeir')
    if metal_like:
        guard = {'flametal': (250, 160, 60), 'eitr': (120, 220, 255), 'blackmetal': (120, 100, 70), 'silver': (120, 180, 230), 'ash': (240, 110, 40)}.get(next((k for k, v in BASE.items() if v == base), ''), (110, 80, 52))
        tmpl = {'valheim_hammer': 'sledge'}.get(name, kind)
        return render(T[tmpl], P(m=m, h=WOOD if base not in (BASE['wood'],) else (110, 75, 40), g=GRIP, a=guard))
    if kind == 'club': return render(T['club'], P(h=m, g=GRIP))
    if kind == 'fist': return render(T['fist'], P(m=(235, 230, 215), g=m, a=GRIP))
    if kind == 'staff':
        return render(T['staff'], P(h=(95, 70, 50) if name != 'staff_of_the_wild' else (90, 110, 60), a=m))
    if kind == 'bow': return render(T['bow'], P(h=m, s=STRING, g=GRIP))
    if kind == 'crossbow': return render(T['crossbow'], P(m=m, h=WOOD, s=STRING, g=GRIP))
    if kind == 'arrow':
        head = {'fire_arrow': (250, 140, 40), 'poison_arrow': (130, 190, 60), 'frost_arrow': (170, 220, 255), 'wood_arrow': (175, 135, 85)}.get(name, m)
        fl = {'fire_arrow': (230, 70, 40), 'poison_arrow': (90, 140, 50), 'frost_arrow': (120, 170, 230), 'charred_arrow': (70, 60, 60)}.get(name, (235, 232, 222))
        return render(T['bolt' if 'bolt' in name else 'arrow'], P(m=head, h=WOOD if 'charred' not in name else (75, 60, 55), a=fl))
    if kind == 'shield':
        if 'tower' in name: return render(T['shield_tower'], P(m=IRON if 'wood' in name else m, h=WOOD if 'wood' in name or name == 'iron_tower_shield' else m, a=IRON))
        if 'buckler' in name: return render(T['buckler'], P(m=m, a=lighter))
        if name in ('wood_shield', 'banded_shield'): return render(T['shield_round'], P(m=IRON, h=WOOD, a=IRON))
        if name == 'blackmetal_shield': return render(T['shield_metal'], P(m=m, a=(150, 120, 80)))
        return render(T['shield_kite'], P(m=m, a=lighter if name != 'flametal_shield' else (255, 200, 90)))
    if kind == 'log': return render(T['log'], P(m=m, a=tuple(min(255, int(v * 1.3) + 25) for v in m), b=tuple(int(v * 0.8) for v in m)))
    if kind == 'raw':
        scrap = 'scrap' in name or name == 'sulfur_stone'
        return render(T['raw'], P(m=m if scrap else (105, 100, 98), a=lighter if scrap else m))
    if kind == 'bowl': return render(T['bowl'], P(a=m, h=WOOD))
    if kind == 'mead': return render(T['mead'], P(a=m, s=GLASS, h=(130, 90, 55)))
    if kind == 'mushroom': return render(T['mushroom'], P(m=m, a=(240, 235, 225), h=(225, 215, 190)))
    if kind == 'berry': return render(T['berry'], P(m=m, a=GREEN, h=(90, 70, 40)))
    if kind == 'carrot': return render(T['carrot'], P(m=m, a=GREEN))
    if kind == 'plant': return render(T['plant'], P(m=m, h=GREEN))
    if kind in ('meat', 'cooked'): return render(T['meat'], P(m=m, a=tuple(min(255, v + 40) for v in m), s=(235, 230, 215)))
    if kind == 'powder': return render(T['powder'], P(m=m, a=m, h=WOOD))
    if kind == 'thread': return render(T['thread'], P(m=m, h=(120, 85, 50)))
    if kind == 'bread': return render(T['bread'], P(m=m, a=tuple(int(v * 0.7) for v in m)))
    if kind == 'feather': return render(T['feather'], P(m=m, s=(240, 235, 220)))
    if kind == 'fish': return render(T['fish'], P(m=m))
    if kind in T: return render(T[kind], P(m=m, a=lighter, h=WOOD))
    return render(T['block_item'], P(m=m))

# ---------------- worn armour (64x32 humanoid layers) ----------------
def faces(u, v, w, h, d):
    return {'top': (u + d, v, w, d), 'bottom': (u + d + w, v, w, d), 'right': (u, v + d, d, h), 'front': (u + d, v + d, w, h), 'left': (u + d + w, v + d, d, h), 'back': (u + 2 * d + w, v + d, w, h)}
HEAD, HAT, BODY, ARM, LEG = (0, 0, 8, 8, 8), (32, 0, 8, 8, 8), (16, 16, 8, 12, 4), (40, 16, 4, 12, 4), (0, 16, 4, 12, 4)

def armor_layers(sname, seed=0):
    import random
    st = SETS[sname]; rnd = random.Random(sname + str(seed))
    M, A, G = ramp(st['m']), ramp(st['a']), ramp(st['g']); pat = st['pat']
    L1 = [[(0, 0, 0, 0)] * 64 for _ in range(32)]; L2 = [[(0, 0, 0, 0)] * 64 for _ in range(32)]
    def put(img, x, y, c): img[y][x] = c + (255,) if len(c) == 3 else c
    def base(img, fx, fy, fw, fh, pal, lx, ly, part, face):
        for y in range(fh):
            for x in range(fw):
                gx, gy = lx + x, ly + y; t = 2
                if pat in ('scale',): t = 3 if (gy % 3 == 0) else (1 if (gx + (gy // 3) * 2) % 4 == 0 else 2)
                elif pat in ('plate', 'flame'): t = 3 if gy % 4 == 0 else (1 if gy % 4 == 3 else 2)
                elif pat == 'chain': t = 3 if (gx + gy) % 2 == 0 else 1
                elif pat == 'chitin': t = 1 if gy % 5 == 4 or gx % 6 == 5 else (3 if gy % 5 == 0 else 2)
                elif pat == 'bark': t = 1 if (gx * 7 + gy // 3) % 5 == 0 else (3 if gx % 5 == 1 else 2)
                elif pat == 'quilt': t = 1 if (gx % 3 == 0 or gy % 3 == 0) else 3 if (gx + gy) % 3 == 1 else 2
                elif pat == 'robe': t = 1 if gx % 3 == 2 else 2 + (gx % 3 == 0)
                elif pat in ('fur', 'hide', 'rag', 'leather'): t = 2 + rnd.choice((-1, 0, 0, 1))
                if y == 0 and face != 'bottom': t = min(4, t + 1)
                if y == fh - 1: t = max(1, t - 1)
                put(img, fx + x, fy + y, pal[t])
    def paint(img, cube, part, rows=None, pal=M, skip=()):
        for face, (fx, fy, fw, fh) in faces(*cube).items():
            if face in skip: continue
            r0, r1 = (0, fh) if rows is None or face in ('top', 'bottom') else rows
            if face == 'top' and rows and rows[0] > 0: continue
            if face == 'bottom' and rows and rows[1] < fh: continue
            for y in range(r0, r1):
                base(img, fx, fy + y, fw, 1, pal, fx, y, part, face) if False else None
            sub = (fx, fy + r0, fw, r1 - r0)
            base(img, sub[0], sub[1], sub[2], sub[3], pal, fx, r0, part, face)
    # ---- helmet (layer 1 head) ----
    helm = st['helm']
    hp = (M if pat == 'robe' else A) if helm == 'hood' else M
    paint(L1, HEAD, 'head', pal=hp)
    fx, fy, fw, fh = faces(*HEAD)['front']
    for y in range(fh):
        for x in range(fw):
            keep = y < 3 if helm != 'hood' else (y < 2 or x in (0, 7))
            if helm in ('helmet', 'helmet_horn') and (x in (0, 7) and y < 7): keep = True   # cheek guards
            if helm in ('helmet', 'helmet_horn') and x in (3, 4) and y < 5 and sname not in ('padded', 'root'): keep = True  # nasal
            if not keep: L1[fy + y][fx + x] = (0, 0, 0, 0)
    if helm != 'hood':
        for x in range(fw): put(L1, fx + x, fy + 2, M[1])  # brow band shadow
        for (x0, y0, w0, h0) in [faces(*HEAD)[f] for f in ('right', 'left', 'back')]:
            for x in range(w0): put(L1, x0 + x, y0 + 2, M[1])
    if 'eyes' in st:  # root mask: glowing eye holes
        for x in (2, 5): put(L1, fx + x, fy + 3, ramp(st['eyes'])[4]); put(L1, fx + x, fy + 4, ramp(st['eyes'])[3])
        for y in range(3, 7):
            for x in (0, 1, 3, 4, 6, 7):
                put(L1, fx + x, fy + y, M[2 if y < 5 else 1])
    if 'horn' in st:  # drake horns painted on the hat layer sides
        H = ramp(st['horn'])
        for f in ('right', 'left'):
            x0, y0, w0, h0 = faces(*HAT)[f]
            for (x, y, t) in ((3, 0, 4), (4, 0, 3), (2, 1, 3), (3, 1, 2), (2, 2, 2), (1, 2, 1), (1, 3, 1)):
                put(L1, x0 + (x if f == 'right' else 7 - x), y0 + y, H[t])
    if helm == 'hood':  # a cloth band on the hat layer to make the hood bulkier at the back
        x0, y0, w0, h0 = faces(*HAT)['back']
        for y in range(h0):
            for x in range(w0): put(L1, x0 + x, y0 + y, hp[2 if (x + y) % 3 else 1])
    # ---- chest: body + arms (layer 1) ----
    paint(L1, BODY, 'body'); paint(L1, ARM, 'arm')
    bx, by, bw, bh = faces(*BODY)['front']
    for x in range(bw): put(L1, bx + x, by + 9, G[2]); put(L1, bx + x, by + 10, G[1])  # belt
    put(L1, bx + 3, by + 9, ramp((220, 190, 80))[3]); put(L1, bx + 4, by + 9, ramp((220, 190, 80))[2])  # buckle
    if pat in ('leather', 'hide', 'rag', 'fur'):  # diagonal strap
        for i in range(8): put(L1, bx + i, by + i, G[2 if i % 2 else 3])
    if pat == 'flame':  # glowing seams
        F = ramp(st['a'])
        for i in range(0, 8, 2): put(L1, bx + i, by + 4, F[4]); put(L1, bx + i + 1, by + 8, F[3])
    if pat == 'robe':  # trim down the front + hem
        T_ = ramp(st['a'])
        for y in range(bh): put(L1, bx + 3, by + y, T_[3]); put(L1, bx + 4, by + y, T_[2])
    # shoulders / fur trims
    trim = A if pat not in ('robe',) else ramp(st['a'])
    if pat in ('chain', 'fur', 'hide', 'leather'):
        F = ramp(st.get('cape', st['a']))
        ax, ay, aw, ah = faces(*ARM)['top']
        for x in range(aw):
            for y in range(ah): put(L1, ax + x, ay + y, F[3 if (x + y) % 2 else 2])
        for f in ('right', 'front', 'left', 'back'):
            x0, y0, w0, h0 = faces(*ARM)[f]
            for x in range(w0):
                for y in range(2 + (x % 2)): put(L1, x0 + x, y0 + y, F[3 if y == 0 else 2])
        tx, ty, tw, th = faces(*BODY)['top']
        for x in range(tw):
            for y in range(th): put(L1, tx + x, ty + y, F[3 if (x + y) % 2 else 2])
        for f in ('front', 'back', 'left', 'right'):
            x0, y0, w0, h0 = faces(*BODY)[f]
            for x in range(w0):
                for y in range(1 + (x % 3 == 0)): put(L1, x0 + x, y0 + y, F[3 if y == 0 else 2])
    elif pat in ('plate', 'scale', 'chitin', 'quilt', 'flame', 'bark'):
        for f in ('right', 'front', 'left', 'back'):
            x0, y0, w0, h0 = faces(*ARM)[f]
            for x in range(w0): put(L1, x0 + x, y0, M[4]); put(L1, x0 + x, y0 + 3, M[1])
        if pat in ('plate', 'scale'):  # rivets
            for x in (1, 6): put(L1, bx + x, by + 1, ramp(st['m'])[4])
    # cape on the back
    if 'cape' in st:
        C = ramp(st['cape']); x0, y0, w0, h0 = faces(*BODY)['back']
        for y in range(h0):
            for x in range(w0): put(L1, x0 + x, y0 + y, C[1 if x in (0, w0 - 1) else (3 if (x + y // 2) % 3 == 0 else 2)])
    # forearm wraps
    for f in ('right', 'front', 'left', 'back'):
        x0, y0, w0, h0 = faces(*ARM)[f]
        for x in range(w0): put(L1, x0 + x, y0 + 9, G[2]); put(L1, x0 + x, y0 + 10, G[1])
    # ---- boots (layer 1 legs, lower part) ----
    paint(L1, LEG, 'leg', rows=(8, 12), pal=G if pat in ('robe', 'rag', 'hide', 'fur', 'leather') else M)
    for f in ('right', 'front', 'left', 'back'):
        x0, y0, w0, h0 = faces(*LEG)[f]
        for x in range(w0): put(L1, x0 + x, y0 + 8, (A if pat in ('fur', 'chain', 'hide') else M)[3])
    # ---- leggings (layer 2: legs + waist) ----
    paint(L2, LEG, 'leg', rows=(0, 10), pal=M if pat not in ('robe',) else M)
    paint(L2, BODY, 'body', rows=(8, 12), skip=('top',))
    for f in ('right', 'front', 'left', 'back'):
        x0, y0, w0, h0 = faces(*LEG)[f]
        for x in range(w0): put(L2, x0 + x, y0 + 4, G[2]); put(L2, x0 + x, y0 + 7, G[1])   # leg wraps
    for f in ('front', 'back', 'left', 'right'):
        x0, y0, w0, h0 = faces(*BODY)[f]
        for x in range(w0): put(L2, x0 + x, y0 + 9, G[2]); put(L2, x0 + x, y0 + 10, G[1])
    return L1, L2

# ---------------- worn capes (vanilla cape layout, 64x32) ----------------
CAPE_COL = {'deer_hide_cape': ((150, 105, 65), (195, 160, 115)), 'troll_hide_cape': ((70, 100, 140), (50, 75, 110)), 'wolf_fur_cape': ((215, 218, 225), (245, 245, 248)),
            'lox_cape': ((120, 90, 60), (225, 210, 180)), 'linen_cape': ((225, 218, 195), (180, 60, 50)), 'feather_cape': ((200, 210, 225), (120, 140, 170)),
            'asksvin_cloak': ((150, 60, 45), (110, 90, 70)), 'ash_cape': ((70, 66, 66), (230, 120, 40)), 'cape_of_odin': ((60, 60, 90), (40, 40, 60))}
def cape_texture(name, base):
    m, a = CAPE_COL.get(name, (base, tuple(min(255, v + 40) for v in base)))
    M, A = ramp(m), ramp(a)
    img = [[(0, 0, 0, 0)] * 64 for _ in range(32)]
    fur = name in ('wolf_fur_cape', 'lox_cape', 'deer_hide_cape', 'troll_hide_cape')
    for face, (x0, y0, w, h) in faces(0, 0, 10, 16, 1).items():
        for y in range(h):
            for x in range(w):
                gx, gy = x0 + x, y0 + y
                if name == 'feather_cape': t = 3 if (gy + (gx % 2)) % 3 == 0 else (1 if (gy + gx) % 5 == 0 else 2)
                elif fur: t = 2 + ((gx * 5 + gy * 3) % 7 == 0) - ((gx * 3 + gy * 7) % 9 == 0)
                else: t = 1 if gx % 4 == 3 else 2 + (gx % 4 == 0)
                c = M[max(1, min(4, t))]
                if face in ('front', 'back') and y < 3: c = A[3 if (x + y) % 2 else 2]           # collar / mantle
                if face in ('front', 'back') and y == h - 1: c = (A if not fur else M)[1]          # hem
                if face in ('front', 'back') and name in ('linen_cape', 'ash_cape', 'asksvin_cloak') and x in (0, w - 1): c = A[2]  # border
                if name == 'cape_of_odin' and face == 'front' and y > 6 and x in (4, 5) and y < 12: c = (200, 200, 210)
                img[gy][gx] = c + (255,)
    return img

def empty_cape_slot():
    rows = T['cape']; px = [[(0, 0, 0, 0)] * 16 for _ in range(16)]
    for y, r in enumerate(rows):
        for x, c in enumerate(r[:16]):
            if c != '.': px[y][x] = (139, 139, 139, 255) if (x + y) % 7 else (150, 150, 150, 255)
    out = [row[:] for row in px]
    for y in range(16):
        for x in range(16):
            if px[y][x][3] and any(not (0 <= x + dx < 16 and 0 <= y + dy < 16) or not px[y + dy][x + dx][3] for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))): out[y][x] = (200, 200, 200, 120)
            elif px[y][x][3]: out[y][x] = (0, 0, 0, 0)
    return out


# ---- 0.15.7: pointy (wizard) hat ----
def pointy_icon(m, band):
    """16x16 icon: a tall bent cone on a wide brim with a gold band and a star."""
    img = [[(0, 0, 0, 0)] * 16 for _ in range(16)]
    dark = tuple(max(0, v - 30) for v in m); light = tuple(min(255, v + 35) for v in m)
    def px(x, y, c):
        if 0 <= x < 16 and 0 <= y < 16: img[y][x] = c + (255,)
    for x in range(1, 15): px(x, 13, dark); px(x, 12, m)
    px(0, 13, dark); px(15, 13, dark)
    rows = [(4, 11), (4, 11), (5, 10), (5, 10), (6, 9), (6, 9), (7, 9), (7, 8), (8, 9), (9, 10), (10, 11)]
    for i, (a, b) in enumerate(rows):
        y = 11 - i
        for x in range(a, b + 1): px(x, y, light if x == a else (dark if x == b else m))
    for x in range(4, 12): px(x, 10, band)
    px(7, 6, (250, 240, 150)); px(11, 1, dark)
    return img

def pointy_hat_layer():
    """64x32 texture for the client PointyHatModel boxes (brim 12x1x12 @0,0; cone 8x3x8 @0,13; 6x3x6 @32,13;
    4x3x4 @0,24; 2x3x2 tip @16,24). Dark violet felt with a gold band on the lowest cone tier."""
    rnd = random.Random('pointy')
    base = (62, 52, 105)
    img = [[(0, 0, 0, 0)] * 64 for _ in range(32)]
    def felt():
        d = rnd.randint(-10, 10); return tuple(max(0, min(255, v + d)) for v in base) + (255,)
    def box(u, v, w, h, d, band=False):
        for y in range(v, v + d + h):
            for x in range(u, u + 2 * d + 2 * w):
                if y < v + d and not (d <= x - u < d + 2 * w): continue
                c = felt()
                if band and y >= v + d and (y - v - d) == h - 1: c = (225, 190, 80, 255)
                img[y][x] = c
    box(0, 0, 12, 1, 12); box(0, 13, 8, 3, 8, band=True); box(32, 13, 6, 3, 6); box(0, 24, 4, 3, 4); box(16, 24, 2, 3, 2)
    return img
