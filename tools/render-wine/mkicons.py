from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
from fontTools import subset
cp={}
for l in open('ms.codepoints'):
    n,c=l.split(); cp.setdefault(n,int(c,16))
M={'E700':'menu','E706':'light_mode','E708':'dark_mode','E70D':'expand_more','E70E':'expand_less','E70F':'edit','E710':'add','E711':'close','E712':'more_horiz','E713':'settings','E715':'mail','E71D':'donut_large','E721':'search','E722':'screenshot_region','E72A':'arrow_forward','E72C':'refresh','E72E':'lock','E734':'star','E73E':'check','E74A':'arrow_upward','E74B':'arrow_downward','E74D':'delete','E74E':'save','E74F':'volume_off','E765':'keyboard','E767':'volume_up','E768':'play_arrow','E76B':'chevron_left','E76C':'chevron_right','E771':'brush','E774':'language','E77F':'content_paste','E781':'lightbulb','E7A7':'undo','E7AD':'open_with','E7BA':'warning','E7C1':'flag','E7C2':'near_me','E7C3':'description','E7C4':'web_asset','E7C8':'fiber_manual_record','E7E8':'power_settings_new','E7F4':'desktop_windows','E7F8':'laptop','E7FC':'sports_esports','E80F':'home','E821':'work','E838':'folder_open','E892':'skip_previous','E893':'skip_next','E895':'sync','E896':'download','E897':'help','E898':'upload','E8A7':'open_in_new','E8AB':'swap_horiz','E8B0':'mouse','E8B5':'input','E8B7':'folder','E8B9':'image','E8C6':'content_cut','E8C8':'content_copy','E8D2':'text_fields','E8D6':'music_note','E8E5':'file_open','E8F1':'library_books','E916':'timer','E946':'info','E950':'memory','E962':'mouse','E993':'brightness_low','E995':'brightness_high','E9D9':'monitoring','EDE1':'ios_share','E8EE':'repeat','E7C9':'touch_app','E738':'remove'}
f=TTFont('ms.ttf')
f=instancer.instantiateVariableFont(f,{'FILL':0,'GRAD':0,'opsz':24,'wght':400})
cmap=f.getBestCmap()
names={}
miss=[]
for k,n in M.items():
    c=cp.get(n)
    if c is None or c not in cmap: miss.append(n); continue
    names[int(k,16)]=cmap[c]
print('missing',miss)
for t in f['cmap'].tables:
    if t.isUnicode(): t.cmap={u:g for u,g in names.items()}
f['cmap'].tables=[t for t in f['cmap'].tables if t.isUnicode()]
for drop in ('GSUB','GPOS','GDEF'):
    if drop in f: del f[drop]
opts=subset.Options(); opts.glyph_names=True; opts.layout_features=[]; opts.notdef_outline=True
s=subset.Subsetter(opts); s.populate(unicodes=list(names)); s.subset(f)
nm=f['name']
for r in list(nm.names):
    if r.nameID in (1,4,16,21): nm.setName('Segoe Fluent Icons',r.nameID,r.platformID,r.platEncID,r.langID)
    if r.nameID==6: nm.setName('SegoeFluentIcons',6,r.platformID,r.platEncID,r.langID)
    if r.nameID in (2,17): nm.setName('Regular',r.nameID,r.platformID,r.platEncID,r.langID)
nm.names=[r for r in nm.names if r.nameID not in (16,17,21,22,25)]
f['OS/2'].ulCodePageRange1|=1
f.save('SegoeFluentIcons-standin.ttf'); print('ok')
