from fontTools.ttLib import TTFont
import os
D=os.path.expanduser('~/.wpfx/drive_c/windows/Fonts/')
for f in os.listdir(D): os.remove(D+f)
alias={0x2315:0x2302,0x23e9:0x25b6,0x23ed:0x25b6,0x23ee:0x25c0,0x23ef:0x25b6,0x23f1:0x25ce,0x23f9:0x25a0,0x23fa:0x25cf,0x1f4a1:0x2600,0x1f507:0x266b,0x1f509:0x266b,0x1f50a:0x266b,0x1f512:0x25a0,0x1f517:0x221e,0x1f5a5:0x25a1,0x1f5a9:0x25a6,0x1f5b1:0x25cf,0x1f5d5:0x2500}
def mk(src,fam,st,w,fn):
    f=TTFont(src)
    for t in f['cmap'].tables:
        if t.isUnicode():
            for a,b in alias.items():
                if b in t.cmap and a not in t.cmap and (a<0x10000 or t.format==12): t.cmap[a]=t.cmap[b]
    if not any(t.format==12 for t in f['cmap'].tables):
        from fontTools.ttLib.tables._c_m_a_p import cmap_format_12
        base=f.getBestCmap(); n=cmap_format_12(12); n.platformID=3; n.platEncID=10; n.format=12; n.reserved=0; n.length=0; n.language=0; n.nGroups=0
        n.cmap=dict(base); 
        for a,b in alias.items():
            if b in base: n.cmap[a]=base[b]
        f['cmap'].tables.append(n)
    nm=f['name']; nm.names=[r for r in nm.names if r.nameID not in (1,2,3,4,6,16,17,21,22,25)]
    sub='Bold' if st=='Bold' else 'Regular'
    for pid,eid,lid in [(3,1,0x409),(1,0,0)]:
        nm.setName(fam if st in('Regular','Bold') else fam+' '+st,1,pid,eid,lid); nm.setName(sub,2,pid,eid,lid); nm.setName(fam+' '+st,3,pid,eid,lid); nm.setName(fam+' '+st,4,pid,eid,lid); nm.setName((fam+'-'+st).replace(' ',''),6,pid,eid,lid)
        nm.setName(fam,16,pid,eid,lid); nm.setName(st,17,pid,eid,lid)
    f['OS/2'].usWeightClass=w
    f.save(D+fn)
R='/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf'; B='/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'; M='/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf'
for fam,p in [('Segoe UI','segoeui'),('Segoe UI Variable Text','segvar'),('Arial','arial'),('Tahoma','tahoma'),('Segoe UI Symbol','seguisym'),('Segoe UI Emoji','seguiemj'),('Microsoft Sans Serif','micross'),('Times New Roman','times')]:
    mk(R,fam,'Regular',400,p+'.ttf'); mk(B,fam,'Bold',700,p+'b.ttf')
    if fam.startswith('Segoe UI') and 'Sym' not in fam and 'Emo' not in fam: mk(B,fam,'Semibold',600,p+'sb.ttf')
for fam,p in [('Courier New','cour'),('Consolas','consola'),('Cascadia Mono','cascm')]: mk(M,fam,'Regular',400,p+'.ttf')
print(len(os.listdir(D)))
