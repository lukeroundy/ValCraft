"""0.15.1: contact sheets: before/after item icons and armour sets on a player-skin layout."""
import os, sys
from PIL import Image, ImageDraw
sys.path.insert(0, os.path.dirname(__file__)); import pixelart
TX = os.path.join(os.path.dirname(__file__), '..', 'neoforge-mod', 'src', 'main', 'resources', 'assets', 'valcraft', 'textures')
def sheet(before, out, cols=16, sc=4):
    names = sorted(f for f in os.listdir(f'{TX}/item') if f.endswith('.png'))
    cw, ch = 16 * sc * 2 + 12, 16 * sc + 14
    im = Image.new('RGBA', (cols * cw, ((len(names) + cols - 1) // cols) * ch), (40, 40, 46, 255)); d = ImageDraw.Draw(im)
    for i, n in enumerate(names):
        x, y = (i % cols) * cw, (i // cols) * ch
        for k, src in enumerate((f'{before}/item/{n}', f'{TX}/item/{n}')):
            if os.path.exists(src): im.alpha_composite(Image.open(src).convert('RGBA').resize((16 * sc, 16 * sc), Image.NEAREST), (x + 4 + k * (16 * sc + 2), y + 2))
        d.text((x + 4, y + 16 * sc + 2), n[:-4][:20], fill=(200, 200, 200, 255))
    im.save(out)
def skin():
    s = Image.new('RGBA', (64, 64), (0, 0, 0, 0)); p = s.load()
    for (u, v, w, h, dd), col in (((0, 0, 8, 8, 8), (200, 150, 120)), ((16, 16, 8, 12, 4), (60, 120, 160)), ((40, 16, 4, 12, 4), (200, 150, 120)), ((0, 16, 4, 12, 4), (60, 60, 120))):
        for f, (x0, y0, ww, hh) in pixelart.faces(u, v, w, h, dd).items():
            for y in range(hh):
                for x in range(ww): p[x0 + x, y0 + y] = col + (255,)
    fx, fy, *_ = pixelart.faces(0, 0, 8, 8, 8)['front']
    for x in (1, 2, 5, 6): p[fx + x, fy + 4] = (255, 255, 255, 255)
    for x in (2, 5): p[fx + x, fy + 4] = (60, 60, 160, 255)
    for x in range(8): p[fx + x, fy + 1] = (80, 50, 30, 255); p[fx + x, fy] = (80, 50, 30, 255)
    return s
def figure(layers, side):
    """2D orthographic front/back view: skin + layer_2 + layer_1 (+hat)."""
    W = Image.new('RGBA', (16, 32), (0, 0, 0, 0))
    def blit(src, cube, face, dx, dy, mirror=False):
        x0, y0, w, h = pixelart.faces(*cube)[face]; part = src.crop((x0, y0, x0 + w, y0 + h))
        if mirror: part = part.transpose(Image.FLIP_LEFT_RIGHT)
        W.alpha_composite(part, (dx, dy))
    f = 'front' if side == 'front' else 'back'
    for src in layers:
        blit(src, (0, 0, 8, 8, 8), f, 4, 0); blit(src, (16, 16, 8, 12, 4), f, 4, 8)
        blit(src, (40, 16, 4, 12, 4), f, 0, 8); blit(src, (40, 16, 4, 12, 4), f, 12, 8, True)
        blit(src, (0, 16, 4, 12, 4), f, 4, 20); blit(src, (0, 16, 4, 12, 4), f, 8, 20, True)
    for src in layers[1:]: blit(src, (32, 0, 8, 8, 8), f, 4, 0)
    return W
def armor_sheet(out, sc=6):
    sets = sorted({f.rsplit('_layer', 1)[0] for f in os.listdir(f'{TX}/models/armor')})
    cw = 16 * sc * 2 + 64 * 3 + 30; ch = 32 * sc + 20
    im = Image.new('RGBA', (cw * 2, ((len(sets) + 1) // 2) * ch), (40, 40, 46, 255)); d = ImageDraw.Draw(im); sk = skin()
    for i, s in enumerate(sets):
        x, y = (i % 2) * cw, (i // 2) * ch
        l1 = Image.open(f'{TX}/models/armor/{s}_layer_1.png').convert('RGBA'); l2 = Image.open(f'{TX}/models/armor/{s}_layer_2.png').convert('RGBA')
        l1s = Image.new('RGBA', (64, 64)); l1s.paste(l1, (0, 0)); l2s = Image.new('RGBA', (64, 64)); l2s.paste(l2, (0, 0))
        for k, side in enumerate(('front', 'back')): im.alpha_composite(figure([sk, l2s, l1s], side).resize((16 * sc, 32 * sc), Image.NEAREST), (x + 6 + k * (16 * sc + 6), y + 4))
        im.alpha_composite(l1.resize((128, 64), Image.NEAREST), (x + 32 * sc + 20, y + 10)); im.alpha_composite(l2.resize((128, 64), Image.NEAREST), (x + 32 * sc + 20, y + 80))
        d.text((x + 32 * sc + 20, y + 150), s, fill=(240, 220, 160, 255))
    im.save(out)
if __name__ == '__main__':
    sheet(sys.argv[1], sys.argv[2]); armor_sheet(sys.argv[3])
