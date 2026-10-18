from pathlib import Path
import UnityPy,json,hashlib,sys
from UnityPy.helpers.MeshHelper import MeshHandler
asset=Path(sys.argv[1])
env=UnityPy.load(str(asset));out=[]
def ancestry(pointer):
    chain=[]
    while pointer.path_id:
        t=pointer.read()
        chain.append(t.m_GameObject.read().m_Name)
        pointer=t.m_Father
    return chain
for obj in env.objects:
    if obj.type.name!='SkinnedMeshRenderer':continue
    smr=obj.read()
    if not smr.m_Mesh.path_id:continue
    mesh=smr.m_Mesh.read()
    if mesh.m_Name!='o_body':continue
    bones=[p.read().m_GameObject.read().m_Name if p.path_id else '' for p in smr.m_Bones]
    poses=[[getattr(m,f'e{r}{c}') for r in range(4) for c in range(4)] for m in mesh.m_BindPose]
    handler=MeshHandler(mesh);handler.process()
    out.append(dict(renderer=smr.m_GameObject.read().m_Name,mesh=mesh.m_Name,bones=bones,bindposes=poses,
        vertices=handler.m_Vertices,weights=handler.m_BoneWeights,boneIndices=handler.m_BoneIndices,
        triangles=[i for submesh in handler.get_triangles() for triangle in submesh for i in triangle],
        boneAncestors=[ancestry(p) for p in smr.m_Bones]))
    print('BODY',len(handler.m_Vertices),'bones',len(bones))
    print('\n'.join(f'{i}: {n}' for i,n in enumerate(bones) if any(x in n for x in ['waist','spine','kokan','hips','siri','thigh'])))
path=Path(sys.argv[2]);path.write_text(json.dumps(dict(asset=asset.name,sha256=hashlib.sha256(asset.read_bytes()).hexdigest(),meshes=out)),encoding='utf-8')
