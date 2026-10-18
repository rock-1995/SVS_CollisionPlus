using Character;
using MagicaCloth;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using V = System.Numerics.Vector3;
using M = System.Numerics.Matrix4x4;

namespace SVSCollisionPlus;

internal static class Convert
{
    internal static V Vec(Vector3 v) => new(v.x,v.y,v.z);
    internal static Vector3 Vec(V v) => new(v.X,v.Y,v.Z);
    internal static M Matrix(Matrix4x4 m) => new(m.m00,m.m10,m.m20,m.m30,m.m01,m.m11,m.m21,m.m31,m.m02,m.m12,m.m22,m.m32,m.m03,m.m13,m.m23,m.m33);
    internal static int Depth(Transform t) { int d=0;for(;t!=null && d<128;t=t.parent)d++;return d; }
    internal static ReadOnlySpan<Vector3> Span(Il2CppSystem.Collections.Generic.List<Vector3> list)
    {
        var array=list._items as Il2CppStructArray<Vector3>;
        if(array==null || list._size<0 || list._size>array.Length)throw new InvalidOperationException("Invalid native mesh buffer");
        return array.AsSpan().Slice(0,list._size);
    }
}

internal sealed class BoneNode
{
    internal int Id,Depth;
    internal Transform Bone;
    internal MagicaBoneSpring Cloth;
    internal Region Region=Region.Breast;
    internal readonly List<BodySample> Samples=new();
    internal readonly List<ContactProbe> Probes=new();
    internal readonly List<ContactTarget> Targets=new();
    internal readonly ProjectionWorkspace Projection=new();
    internal V OffsetLocal,SavedLocal,AppliedLocal,BaseWorld;
    internal int MotionKinds;
    internal bool Applied;
    internal bool Active=>Bone!=null && ClothActive(Cloth);
    internal static bool ClothActive(MagicaBoneSpring cloth)=>cloth!=null && cloth.isActiveAndEnabled && cloth.IsActive() && cloth.IsCalculate && cloth.BlendWeight>.001f;
    internal RestoreResult Restore()
    {
        if(!Applied)return RestoreResult.None;
        Applied=false;
        if(Bone==null)return RestoreResult.Missing;
        if(V.DistanceSquared(Convert.Vec(Bone.localPosition),AppliedLocal)<1e-10f)
        {Bone.localPosition=Convert.Vec(SavedLocal);return RestoreResult.Restored;}
        return RestoreResult.Replaced;
    }
}
internal enum RestoreResult {None,Restored,Replaced,Missing}
internal readonly record struct BodySample(BodySurface Surface,int Vertex,float Weight);

internal sealed class FrameWork
{
    internal readonly List<(ActorData Actor,BoneNode Node)> Nodes=new();
    private readonly List<ActorData> activeActors=new();
    private readonly Dictionary<IntPtr,bool> clothStates=new();
    internal int ClothChecks {get;private set;}
    internal int MeshUpdates {get;private set;}
    internal int NativeSurfaces=>0;
    internal void Prepare(IEnumerable<ActorData> actors,Switches switches)
    {
        Nodes.Clear();activeActors.Clear();clothStates.Clear();ClothChecks=0;
        foreach(var actor in actors)
        {
            foreach(var s in actor.Surfaces){s.Needed=false;s.Valid=false;s.Tree.Enabled=false;}
            if(!actor.Active)continue;
            activeActors.Add(actor);
            foreach(var node in actor.Nodes)
            {
                if(!switches.Any || node.Bone==null || node.Cloth==null){node.OffsetLocal=V.Zero;node.MotionKinds=0;continue;}
                if(!clothStates.TryGetValue(node.Cloth.Pointer,out bool active))
                {active=BoneNode.ClothActive(node.Cloth);clothStates.Add(node.Cloth.Pointer,active);ClothChecks++;}
                if(!active){node.OffsetLocal=V.Zero;node.MotionKinds=0;continue;}
                Nodes.Add((actor,node));node.BaseWorld=Convert.Vec(node.Bone.position);
            }
        }
        if(Nodes.Count>0)foreach(var actor in activeActors)foreach(var s in actor.Surfaces)s.Needed=true;
    }
    internal void UpdateSurfaces()
    {
        MeshUpdates=0;
        foreach(var actor in activeActors)
        {
            foreach(var surface in actor.Surfaces)if(surface.Needed){surface.Update();MeshUpdates++;}
            // Pregnancy can replace weights in-place without replacing the mesh.
            if(actor.Surfaces.Any(s=>s.BindingChanged))actor.RebuildSamples();
        }
    }
    internal void Clear(){Nodes.Clear();activeActors.Clear();clothStates.Clear();}
}

internal sealed class BodySurface:IDisposable
{
    internal readonly SkinnedMeshRenderer Renderer;
    internal readonly Mesh Source;
    internal Mesh Baked;
    internal Transform[] Bones;
    internal readonly BoneWeight[] Weights;
    internal readonly int[] Triangles;
    internal readonly int?[] Owners;
    internal readonly float[] OwnerWeights;
    internal readonly V[] World,WorldNormals;
    internal readonly Region[] VertexRegions;
    internal readonly SurfaceTree Tree;
    internal readonly Dictionary<int,bool[]> Exclusions=new();
    internal bool Valid,Needed,BindingChanged;
    internal bool UsesNativeCache=>false;
    private readonly Dictionary<int,BoneNode> nodes;
    private readonly Region[] triangleRegions;
    private readonly Il2CppSystem.Collections.Generic.List<Vector3> vertices=new(),normals=new();
    private bool disposed;

    internal BodySurface(SkinnedMeshRenderer renderer,Dictionary<int,BoneNode> nodes)
    {
        Renderer=renderer;Source=renderer.sharedMesh;this.nodes=nodes;
        try
        {
            EnsureScratchMesh();
            Bones=renderer.bones;Weights=new BoneWeight[Source.vertexCount];Triangles=Source.triangles;
            World=new V[Weights.Length];WorldNormals=new V[Weights.Length];Owners=new int?[Weights.Length];OwnerWeights=new float[Weights.Length];VertexRegions=new Region[Weights.Length];
            triangleRegions=new Region[Triangles.Length/3];
            if(Triangles.Length%3!=0 || Triangles.Any(i=>(uint)i>=World.Length))throw new InvalidOperationException("Invalid body topology");
            foreach(var node in nodes.Values)Exclusions.Add(node.Id,new bool[triangleRegions.Length]);
            RefreshBinding(true);BakeVertices();
            Tree=new SurfaceTree(World,Triangles,triangleRegions);Tree.Refit(WorldNormals);Tree.Enabled=Valid;
        }
        catch{UnityEngine.Object.Destroy(Baked);throw;}
    }
    private void RefreshBinding(bool force=false)
    {
        // SVS's GetBoneWeights(List) is an unstripping-failure stub. The
        // array property has a real native implementation in this game.
        var weights=Source.boneWeights;
        try
        {
            ReadOnlySpan<BoneWeight> live=weights.AsSpan();
            if(live.Length!=Weights.Length)throw new InvalidOperationException("Body weights count changed");
            if(!force && System.Runtime.InteropServices.MemoryMarshal.AsBytes(live).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Weights.AsSpan())))return;
            live.CopyTo(Weights);
        }
        finally { GC.KeepAlive(weights); }
        Bones=Renderer.bones;
        // Unity runtime instance IDs can be negative. Keep "not owned" as
        // null instead of reserving a valid integer or rejecting its sign.
        var boneOwners=new int?[Bones.Length];var boneRegions=new Region[Bones.Length];
        for(int i=0;i<Bones.Length;i++)
        {
            var bone=Bones[i];
            // Checking ancestors for abdomen would also classify arms and legs as belly.
            string name=bone?.name?.ToLowerInvariant()??"";
            boneRegions[i]=IsAbdomenBone(name)?Region.Abdomen:Region.Other;
            for(var t=bone;t!=null;t=t.parent)if(nodes.TryGetValue(t.GetInstanceID(),out var n))
            {boneOwners[i]=n.Id;boneRegions[i]=Region.Breast;break;}
        }
        Array.Clear(Owners,0,Owners.Length);Array.Clear(OwnerWeights,0,OwnerWeights.Length);
        var influences=nodes.Keys.ToDictionary(id=>id,_=>new bool[Weights.Length]);
        var sums=new Dictionary<int,float>();
        for(int v=0;v<Weights.Length;v++)
        {
            sums.Clear();float abdomen=0;var w=Weights[v];
            void Add(int bone,float amount)
            {
                if(amount<=.001f)return;
                if((uint)bone>=Bones.Length || !float.IsFinite(amount))throw new InvalidOperationException("Invalid skin influence");
                if(boneOwners[bone] is int owner){sums[owner]=sums.GetValueOrDefault(owner)+amount;influences[owner][v]=true;}
                else if(boneRegions[bone]==Region.Abdomen)abdomen+=amount;
            }
            Add(w.boneIndex0,w.weight0);Add(w.boneIndex1,w.weight1);Add(w.boneIndex2,w.weight2);Add(w.boneIndex3,w.weight3);
            VertexRegions[v]=abdomen>=.25f?Region.Abdomen:Region.Other;
            if(sums.Count>0)
            {
                var best=sums.MaxBy(p=>p.Value);
                if(best.Value>=.25f){Owners[v]=best.Key;OwnerWeights[v]=best.Value;VertexRegions[v]=Region.Breast;}
            }
        }
        for(int t=0;t<triangleRegions.Length;t++)
        {
            int a=Triangles[t*3],b=Triangles[t*3+1],c=Triangles[t*3+2];
            var x=VertexRegions[a];var y=VertexRegions[b];var z=VertexRegions[c];
            triangleRegions[t]=x==y || x==z?x:y==z?y:Region.Other;
            foreach(var item in influences)Exclusions[item.Key][t]=item.Value[a] || item.Value[b] || item.Value[c];
        }
        BindingChanged=true;
    }
    internal static bool IsAbdomenBone(string name)=>name.Contains("hara") || name.Contains("belly") || name.Contains("waist") || name.EndsWith("_hips") || name.Contains("hips01") || name.Contains("spine01") || name.Contains("spine02");
    private void EnsureScratchMesh()
    {
        if(Baked!=null)return;
        // This temporary mesh is held only by the managed adapter, not a
        // renderer. Keep Unity's unused-asset cleanup from destroying it.
        Baked=new Mesh{name="SVSCollisionPlus_Scratch",hideFlags=HideFlags.DontUnloadUnusedAsset};
        Baked.MarkDynamic();
    }
    private void BakeVertices()
    {
        // SVS Pregnancy edits vertices, weights AND bindposes, including virtual
        // pelvis palette entries. The liquid collision mesh is not deformed.
        EnsureScratchMesh();
        Renderer.BakeMesh(Baked,false);Baked.GetVertices(vertices);Baked.GetNormals(normals);
        var positions=Convert.Span(vertices);var directions=Convert.Span(normals);
        if(positions.Length!=World.Length){Valid=false;return;}
        var world=Convert.Matrix(Renderer.transform.localToWorldMatrix);
        if(!M.Invert(world,out var inverse)){Valid=false;return;}
        var normal=M.Transpose(inverse);Valid=true;
        for(int i=0;i<World.Length;i++)
        {
            World[i]=V.Transform(Convert.Vec(positions[i]),world);
            WorldNormals[i]=i<directions.Length?V.TransformNormal(Convert.Vec(directions[i]),normal):V.Zero;
            if(!Geometry.Finite(World[i]) || !Geometry.Finite(WorldNormals[i]))Valid=false;
        }
        GC.KeepAlive(vertices);GC.KeepAlive(normals);
    }
    internal bool Update()
    {
        Valid=false;Tree.Enabled=false;
        if(disposed || Source==null || Renderer==null || !Renderer.gameObject.activeInHierarchy || !Renderer.enabled)return false;
        var mesh=Renderer.sharedMesh;
        if(mesh==null || mesh.GetInstanceID()!=Source.GetInstanceID() || mesh.vertexCount!=World.Length)return false;
        RefreshBinding();BakeVertices();if(Valid)Tree.Refit(WorldNormals);Tree.Enabled=Valid;return Valid;
    }
    public void Dispose(){if(disposed)return;disposed=true;if(Baked!=null)UnityEngine.Object.Destroy(Baked);Baked=null;}
}

internal sealed class ActorData:IDisposable
{
    internal readonly Human Human;
    internal readonly long Fingerprint;
    internal readonly List<BoneNode> Nodes=new();
    internal readonly List<BodySurface> Surfaces=new();
    internal float Height=1.6f;
    internal bool Ready;
    internal ActorData(Human human,long fingerprint,Plugin plugin)
    {
        Human=human;Fingerprint=fingerprint;
        var nodes=new Dictionary<int,BoneNode>();
        try
        {
            // SVS defines Bust=0 and Hip=1 only; AL's eight types do not exist.
            var entries=human.body?.magicaSpringSetting?.Get(HumanMagicaSpringSetting.Type.Bust,HumanMagicaSpringSetting.Side.All);
            if(entries!=null)foreach(var e in entries)
            {
                var cloth=e?.spring;var roots=cloth?.ClothTarget?.rootList;
                if(roots==null)continue;
                foreach(var root in roots)if(root!=null && root.IsChildOf(human.transform))
                {int id=root.GetInstanceID();nodes.TryAdd(id,new(){Id=id,Bone=root,Cloth=cloth,Depth=Convert.Depth(root)});}
            }
            // Only the outermost physical root gets the whole-breast correction.
            foreach(var n in nodes.Values.OrderBy(n=>n.Depth))if(!Nodes.Any(parent=>n.Bone.IsChildOf(parent.Bone)))Nodes.Add(n);
            // Characters without breast springs need no mesh baking. Their
            // fingerprint changes when a body/spring is subsequently loaded.
            if(Nodes.Count==0){Ready=true;return;}
            nodes=Nodes.ToDictionary(n=>n.Id);
            foreach(var renderer in BodyRenderers(human))Surfaces.Add(new BodySurface(renderer,nodes));
            RebuildSamples();UpdateHeight();
            Ready=Nodes.Count>0 && Surfaces.Count>0 && Nodes.Any(n=>n.Samples.Count>0);
            if(plugin.Diagnostics.Value)plugin.Log.LogInfo($"Actor bound: ready={Ready}, breastRoots={Nodes.Count}, probes={Nodes.Sum(n=>n.Samples.Count)}, bodySurfaces={Surfaces.Count}, height={Height:F3}; roots=[{string.Join(", ",Nodes.Select(n=>$"{n.Bone.name}#{n.Id}:{n.Samples.Count}"))}]; abdomenVertices={Surfaces.Sum(s=>s.VertexRegions.Count(r=>r==Region.Abdomen))}");
        }
        catch{Dispose();throw;}
    }
    internal static IEnumerable<SkinnedMeshRenderer> BodyRenderers(Human human)
    {
        if(human.gameObject==null)yield break;
        var main=human.body?.rendBody;
        foreach(var s in human.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if(s==null || s.sharedMesh==null || !s.sharedMesh.isReadable)continue;
            string name=s.name.ToLowerInvariant(),mesh=s.sharedMesh.name.ToLowerInvariant();
            bool owned=(main!=null && main.GetInstanceID()==s.GetInstanceID()) || name.StartsWith("o_body") || mesh.StartsWith("o_body");
            if(owned && !name.Contains("shadow") && !mesh.Contains("shadow") && !name.Contains("simple"))yield return s;
        }
    }
    internal void RebuildSamples()
    {
        foreach(var n in Nodes)n.Samples.Clear();
        var byId=Nodes.ToDictionary(n=>n.Id);
        foreach(var s in Surfaces)
        {
            for(int i=0;i<s.Owners.Length;i++)if(s.Owners[i] is int owner && byId.TryGetValue(owner,out var n))n.Samples.Add(new(s,i,s.OwnerWeights[i]));
            s.BindingChanged=false;
        }
    }
    private void UpdateHeight()
    {
        // Bind-pose head/foot origins remain stable when crouching or lying.
        float lo=float.PositiveInfinity,hi=float.NegativeInfinity;
        var rootInverse=Convert.Matrix(Human.transform.worldToLocalMatrix);
        foreach(var s in Surfaces)
        {
            Matrix4x4[] binds=s.Source.bindposes;
            var toRoot=Convert.Matrix(s.Renderer.transform.localToWorldMatrix)*rootInverse;
            for(int i=0;i<Math.Min(binds.Length,s.Bones.Length);i++)
            {
                string name=s.Bones[i]?.name??"";
                if(!name.Contains("head") && !name.Contains("foot") && !name.Contains("toes"))continue;
                if(M.Invert(Convert.Matrix(binds[i]),out var bind))
                {float y=V.Transform(V.Zero,bind*toRoot).Y;lo=Math.Min(lo,y);hi=Math.Max(hi,y);}
            }
        }
        float span=(hi-lo)*1.08f*Math.Abs(Human.transform.lossyScale.y);
        if(float.IsFinite(span) && span>.25f && span<30)Height=span;
    }
    internal bool Active=>!Human.disposed && !Human.isReloading && Human.isActiveAndEnabled && Human.hiPoly && Human.transform!=null;
    public void Dispose(){foreach(var n in Nodes)n.Restore();foreach(var s in Surfaces)s.Dispose();}
}

