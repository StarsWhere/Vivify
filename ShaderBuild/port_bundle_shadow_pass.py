import sys,struct,copy,argparse,json,hashlib
from pathlib import Path
parser=argparse.ArgumentParser()
parser.add_argument('--bundle',required=True)
parser.add_argument('--checksum',required=True,type=int)
parser.add_argument('--output',required=True)
parser.add_argument('--modules')
parser.add_argument('--shadow-bundle',required=True)
args=parser.parse_args()
if args.modules:sys.path.insert(0,str(Path(args.modules).resolve()))
import UnityPy,lz4.block
if UnityPy.__version__!='1.25.4':raise RuntimeError('This converter requires UnityPy 1.25.4')

def entries(d):
    offset=d['offsets'][0][0];length=d['compressedLengths'][0][0];size=d['decompressedLengths'][0][0]
    blob=lz4.block.decompress(bytes(d['compressedBlob'])[offset:offset+length],uncompressed_size=size)
    count=struct.unpack_from('<I',blob)[0]
    rows=[struct.unpack_from('<III',blob,4+i*12) for i in range(count)]
    if any(segment!=0 for offset,length,segment in rows):raise ValueError('Segmented shader data is unsupported')
    return [blob[offset:offset+length] for offset,length,_ in rows]

def encode(rows):
    header=bytearray(4+len(rows)*12);struct.pack_into('<I',header,0,len(rows));payload=bytearray()
    for i,data in enumerate(rows):
        while (len(header)+len(payload))%4:payload.append(0)
        struct.pack_into('<III',header,4+i*12,len(header)+len(payload),len(data),0);payload.extend(data)
    return bytes(header+payload)

src=UnityPy.load(str(Path(args.shadow_bundle).resolve()))
shader=next(o.read_typetree() for o in src.objects if o.type.name=='Shader')
shadow=next(p for s in shader['m_ParsedForm']['m_SubShaders'] for p in s['m_Passes'] if p['m_State']['m_Name']=='SHADOWCASTER')
sourceRows=entries(shader)
source=Path(args.bundle).resolve()
env=UnityPy.load(str(source));changed=0;grabs=set();grabShaders=[];shadowCount=0
for obj in env.objects:
    if obj.type.name!='Shader':continue
    d=obj.read_typetree();p=d['m_ParsedForm']
    hasShadow=any(ps.get('m_UseName')=='Legacy Shaders/VertexLit/SHADOWCASTER' for s in p['m_SubShaders'] for ps in s['m_Passes'])
    hasGrab=any(ps['m_Type']==2 for s in p['m_SubShaders'] for ps in s['m_Passes'])
    if not hasShadow and not hasGrab:continue
    if hasGrab:
        if len(p['m_SubShaders'])!=1:raise ValueError('Grab shader with multiple subshaders requires explicit selection: '+p['m_Name'])
        steps=[];passIndex=0
        for ps in p['m_SubShaders'][0]['m_Passes']:
            if ps['m_Type']==2:
                steps.append(dict(passIndex=-1,texture=ps.get('m_TextureName') or '_GrabTexture',perObject=not bool(ps.get('m_TextureName'))))
            else:
                tags={k.lower():v.lower() for k,v in ps['m_State']['m_Tags']['tags']}
                mode=tags.get('lightmode','')
                if mode not in ('shadowcaster','depthonly','depthnormals','meta','motionvectors') and not ps.get('m_UseName','').lower().endswith('/shadowcaster'):
                    steps.append(dict(passIndex=passIndex,texture='',perObject=False))
                passIndex+=1
        grabShaders.append(dict(shader=p['m_Name'],steps=steps))
        for sub in p['m_SubShaders']:
            grabs.update(ps.get('m_TextureName') or '_GrabTexture' for ps in sub['m_Passes'] if ps['m_Type']==2)
            sub['m_Passes']=[ps for ps in sub['m_Passes'] if ps['m_Type']!=2]
    if not hasShadow:
        obj.save_typetree(d);changed+=1;continue
    shadowCount+=1
    rows=entries(d);base=len(rows)
    names=p['m_KeywordNames'];flags=p['m_KeywordFlags'];mapping={}
    for index,name in enumerate(shader['m_ParsedForm']['m_KeywordNames']):
        if name not in names:names.append(name);flags.append(shader['m_ParsedForm']['m_KeywordFlags'][index])
        mapping[index]=names.index(name)
    def remap(value,key=''):
        if isinstance(value,dict):return {k:remap(v,k) for k,v in value.items()}
        if isinstance(value,list):return [remap(v,key) for v in value]
        if isinstance(value,int):
            if key in ['m_BlobIndex','m_ParameterBlobIndices']:return value+base
            if key in ['m_KeywordIndices','m_SerializedKeywordStateMask']:return mapping[value]
        return value
    def merge(template,source):
        result=copy.deepcopy(template)
        for key,value in source.items():
            if isinstance(value,dict) and isinstance(result.get(key),dict):result[key]=merge(result[key],value)
            else:result[key]=copy.deepcopy(value)
        return result
    for sub in p['m_SubShaders']:
        sub['m_Passes']=[merge(ps,remap(copy.deepcopy(shadow))) if ps.get('m_UseName')=='Legacy Shaders/VertexLit/SHADOWCASTER' else ps for ps in sub['m_Passes']]
    raw=encode(rows+sourceRows);packed=lz4.block.compress(raw,store_size=False)
    d['offsets']=[[0]];d['compressedLengths']=[[len(packed)]];d['decompressedLengths']=[[len(raw)]];d['compressedBlob']=packed
    # Remove only the obsolete built-in dependency belonging to this UsePass.
    d['m_Dependencies']=[ref for ref in d['m_Dependencies'] if not (ref['m_PathID']==6 and ref['m_FileID']>0 and obj.assets_file.externals[ref['m_FileID']-1].path.lower().endswith('unity_builtin_extra'))]
    obj.save_typetree(d);changed+=1
if changed:
    directory=Path(args.output).resolve();directory.mkdir(parents=True,exist_ok=True)
    target=directory/(str(args.checksum)+'.vivify')
    binary=next(iter(env.files.values())).save(packer='lz4')
    target.write_bytes(binary)
    metadata=dict(gameBuild='1.45.2_28848',originalChecksum=args.checksum,sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),outputSha256=hashlib.sha256(binary).hexdigest(),shadowShaderSha256=hashlib.sha256((Path(args.shadow_bundle).resolve()).read_bytes()).hexdigest(),shaders=changed,shadowShaders=shadowCount,grabTextures=sorted(grabs),grabShaders=grabShaders)
    target.with_suffix('.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
    print('Ported',shadowCount,'shadow shaders and',len(grabs),'URP grab textures:',target)
else:print('No legacy shadow pass conversion needed:',source)
