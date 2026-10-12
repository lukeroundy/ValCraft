import sys
from PIL import Image
d=open(sys.argv[1],'rb').read(); i=d.index(b'ENDHDR\n')+7; h=dict(l.split(b' ',1) for l in d[:i].split(b'\n')[1:] if b' ' in l)
w,hh=int(h[b'WIDTH']),int(h[b'HEIGHT']); im=Image.frombytes('RGBA',(w,hh),d[i:i+w*hh*4])
print(sys.argv[1], im.size, 'alpha', im.getchannel('A').getextrema())
bg=Image.new('RGBA',im.size,(255,0,255,255)); bg.alpha_composite(im); bg.convert('RGB').save(sys.argv[2])
