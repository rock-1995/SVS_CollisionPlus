using Character;
using UnityEngine;
using System.Collections.Concurrent;
using V=System.Numerics.Vector3;
using M=System.Numerics.Matrix4x4;

namespace SVSCollisionPlus;

internal sealed class DiagnosticRecorder:IDisposable
{
    private readonly Plugin plugin;
    private readonly Queue<(int Frame,string Stage)> timeline=new();
    private readonly Dictionary<string,long> counts=new();
    private readonly ConcurrentQueue<string> messages=new();
    private readonly Queue<string> files=new();
    private readonly Dictionary<BoneNode,NodeCapture> nodeMap=new();
    private readonly Dictionary<BodySurface,SurfaceCapture> surfaceMap=new();
    private Task writer=Task.CompletedTask;
    private CaptureFrame pending;
    private float nextCapture=2,nextIdle=5,nextFailure,lastSolveAt=-1;
    private int serial;
    private float largestDepth=-1;
    private bool firstBoundSaved;
    private bool stopped;
    private string stage="Load";
    internal string DirectoryPath {get;}
    internal bool IsCapturing=>pending!=null;
    internal bool Enabled=>!stopped && plugin.CaptureEvidence.Value;
    internal DiagnosticRecorder(Plugin plugin,string directory=null)
    {
        this.plugin=plugin;
        DirectoryPath=directory??Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location),"diagnostics",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Environment.ProcessId+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
    }
    internal void Mark(string name)
    {
        if(!Enabled)return;
        stage=name;counts[name]=counts.GetValueOrDefault(name)+1;if(name=="Solve")lastSolveAt=Time.unscaledTime;
        timeline.Enqueue((Time.frameCount,name));if(timeline.Count>256)timeline.Dequeue();
    }
    internal void Pulse(bool hasActors)
    {
        while(messages.TryDequeue(out var text))plugin.Log.LogInfo(text);
        if(!Enabled)return;
        // Even absent/broken physics hooks leave a heartbeat and raw actor data.
        if(Time.unscaledTime<nextIdle || pending!=null || !writer.IsCompleted)return;
        nextIdle=Time.unscaledTime+20;
        if(hasActors && Time.unscaledTime-lastSolveAt<3)return;
        Safe(()=>{pending=Header("no solver activity");ReadInventory(pending,true);Submit();});
    }
    internal void Begin(IEnumerable<ActorData> actors,FrameWork work)
    {
        if(!Enabled || Time.unscaledTime<nextCapture || pending!=null || !writer.IsCompleted)return;
        nextCapture=Time.unscaledTime+15;
        Safe(()=>
        {
            pending=Header("automatic scene snapshot");
            ReadInventory(pending,false);
            nodeMap.Clear();surfaceMap.Clear();
            var surfaces=new Dictionary<BodySurface,int>();int actorIndex=0;
            foreach(var actor in actors)
            {
                foreach(var surface in actor.Surfaces)
                {
                    var capture=ReadSurface(surface.Renderer,actorIndex,surface);
                    surfaces.Add(surface,pending.Surfaces.Count);surfaceMap.Add(surface,capture);pending.Surfaces.Add(capture);
                }
                actorIndex++;
            }
            actorIndex=0;
            foreach(var actor in actors)
            {
                foreach(var node in actor.Nodes)
                {
                    var capture=new NodeCapture{Actor=actorIndex,Id=node.Id,Name=node.Bone?.name??"missing",Active=work.Nodes.Any(x=>ReferenceEquals(x.Node,node)),PreviousOffset=node.OffsetLocal,
                        BeforeLocal=node.Bone==null?V.Zero:Convert.Vec(node.Bone.localPosition),BeforeWorld=node.Bone==null?V.Zero:Convert.Vec(node.Bone.position),
                        ParentMatrix=node.Bone?.parent==null?M.Identity:Convert.Matrix(node.Bone.parent.localToWorldMatrix),
                        Settings=Enumerable.Range(1,3).Select(i=>plugin.Settings((ContactKind)i,actor.Height)).ToArray()};
                    try{var c=node.Cloth;capture.ClothState=c==null?"missing":$"enabled={c.isActiveAndEnabled}; active={c.IsActive()}; calculate={c.IsCalculate}; blend={c.BlendWeight:R}; team={c.TeamId}";}
                    catch(Exception e){capture.ClothState=e.ToString();}
                    foreach(var sample in node.Samples)capture.Samples.Add(new(surfaces[sample.Surface],sample.Vertex,sample.Weight));
                    capture.Probes=node.Samples.Where(s=>s.Surface.Valid).Select(s=>new ContactProbe(s.Surface.World[s.Vertex],s.Weight)).ToArray();
                    foreach(var target in node.Targets)
                    {
                        var found=surfaces.FirstOrDefault(s=>ReferenceEquals(s.Key.Tree,target.Tree));
                        if(found.Key!=null)capture.Targets.Add(new(){Surface=found.Value,Group=target.Group,Excluded=target.Excluded?.ToArray()});
                    }
                    nodeMap.Add(node,capture);pending.Nodes.Add(capture);
                }
                actorIndex++;
            }
        });
    }
    internal void AfterSolve()
    {
        if(pending==null)return;
        Safe(()=>
        {
            foreach(var pair in nodeMap)
            {
                var n=pair.Key;var c=pair.Value;c.Applied=n.Applied;c.AfterLocal=n.Bone==null?V.Zero:Convert.Vec(n.Bone.localPosition);
                c.AfterWorld=n.Bone==null?V.Zero:Convert.Vec(n.Bone.position);c.Offset=n.OffsetLocal;c.RestoredBase=n.SavedLocal;
                c.Results=n.Projection.Results.ToArray();
            }
            foreach(var pair in surfaceMap)
            {
                try{pair.Value.AfterWorld=BakeWorld(pair.Key.Renderer);}
                catch(Exception e){pair.Value.AfterError=e.ToString();}
            }
            pending.Complete=true;
            // Submit on the next restore so the same file also contains evidence
            // of later pose writers. If nothing is applied, no wait is necessary.
            if(nodeMap.Count==0 || !nodeMap.Keys.Any(n=>n.Applied))Submit();
        });
    }
    internal void BeforeRestore()
    {
        if(pending==null || !pending.Complete)return;
        Safe(()=>
        {
            foreach(var pair in nodeMap)
            {
                var n=pair.Key;var c=pair.Value;
                if(n.Bone!=null){c.Observed=true;c.ObservedLocal=Convert.Vec(n.Bone.localPosition);}
            }
            pending.ObservedBeforeRestore=true;Submit();
        });
    }
    internal void Ground(V position,float capture,GroundContact? contact)
    {if(pending!=null)pending.Ground.Add(new(position,capture,contact));}
    internal void Failure(Exception error)
    {
        if(!Enabled || Time.unscaledTime<nextFailure)return;nextFailure=Time.unscaledTime+5;
        Safe(()=>
        {
            pending??=Header("solver exception");pending.Error=error.ToString();pending.Stage=stage;
            if(pending.Inventory.Count==0)ReadInventory(pending,true);
            Submit();
        });
    }
    private CaptureFrame Header(string reason)=>new(){Reason=reason,Stage=stage,Frame=Time.frameCount,Time=Time.unscaledTime,Delta=Math.Clamp(Time.unscaledDeltaTime,0,.05f),Enabled=plugin.Enabled.Value,Switches=plugin.Switches,
        Timeline=counts.Select(p=>$"count {p.Key}={p.Value}").Concat(timeline.Select(p=>$"{p.Frame}: {p.Stage}")).ToList()};
    private void ReadInventory(CaptureFrame frame,bool rawBodies)
    {
        try
        {
            var humans=Human._list;if(humans==null){frame.Inventory.Add("Human._list=null");return;}
            frame.Inventory.Add($"humans={humans.Count}");
            for(int i=0;i<humans.Count;i++)
            {
                try
                {
                    var human=humans[i];if(human==null)continue;
                    frame.Inventory.Add($"actor {i}: disposed={human.disposed}, reloading={human.isReloading}, active={human.isActiveAndEnabled}, hiPoly={human.hiPoly}");
                    if(human.disposed || human.gameObject==null)continue;
                    var entries=human.body?.magicaSpringSetting?.Get(HumanMagicaSpringSetting.Type.Bust,HumanMagicaSpringSetting.Side.All);
                    if(entries!=null)foreach(var entry in entries)
                    {
                        var c=entry?.spring;var roots=c?.ClothTarget?.rootList;
                        if(roots!=null)foreach(var root in roots)if(root!=null)
                        {
                            frame.Inventory.Add($"actor {i} springRoot {root.name}#{root.GetInstanceID()} team={c.TeamId}");
                            frame.RawRoots.Add(new(i,root.GetInstanceID(),root.name,Convert.Matrix(root.localToWorldMatrix),$"enabled={c.isActiveAndEnabled}; active={c.IsActive()}; calculate={c.IsCalculate}; blend={c.BlendWeight:R}; team={c.TeamId}"));
                        }
                    }
                    foreach(var r in human.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if(r==null)continue;var mesh=r.sharedMesh;
                        frame.Inventory.Add($"actor {i} renderer {r.name}#{r.GetInstanceID()}: enabled={r.enabled}, active={r.gameObject.activeInHierarchy}, mesh={(mesh==null?"missing":mesh.name+"#"+mesh.GetInstanceID()+" vertices="+mesh.vertexCount+" readable="+mesh.isReadable)}");
                        var main=human.body?.rendBody;
                        bool body=(main!=null && r.GetInstanceID()==main.GetInstanceID()) || r.name.StartsWith("o_body",StringComparison.OrdinalIgnoreCase) || (mesh!=null && mesh.name.StartsWith("o_body",StringComparison.OrdinalIgnoreCase));
                        if(rawBodies && body && mesh!=null && mesh.isReadable)frame.Surfaces.Add(ReadSurface(r,i,null));
                    }
                }
                catch(Exception e){frame.Errors.Add($"Inventory actor {i}: {e}");}
            }
        }
        catch(Exception e){frame.Errors.Add("Inventory: "+e);}
    }
    private static SurfaceCapture ReadSurface(SkinnedMeshRenderer renderer,int actor,BodySurface live)
    {
        var result=new SurfaceCapture{Actor=actor};
        Mesh mesh=null;
        void Read(string label,Action read)
        {try{read();}catch(Exception e){result.RawError+=label+": "+e+"\n";}}
        Read("renderer",()=>
        {
            mesh=renderer.sharedMesh;result.RendererId=renderer.GetInstanceID();result.MeshId=mesh.GetInstanceID();result.Name=renderer.name+" / "+mesh.name;
            result.RendererMatrix=Convert.Matrix(renderer.transform.localToWorldMatrix);
        });
        if(mesh==null)return result;
        result.Enabled=live?.Tree.Enabled??false;result.Valid=live?.Valid??false;
        Read("topology",()=>
        {
            result.Triangles=live?.Triangles.ToArray()??(int[])mesh.triangles;
            result.Regions=live?.Tree.Regions.ToArray()??new Region[result.Triangles.Length/3];
        });
        Read("source vertices",()=>{Vector3[] vertices=mesh.vertices;result.SourceVertices=vertices.Select(Convert.Vec).ToArray();});
        Read("bindposes",()=>{Matrix4x4[] binds=mesh.bindposes;result.Bindposes=binds.Select(Convert.Matrix).ToArray();});
        Read("weights",()=>
        {
            BoneWeight[] weights=mesh.boneWeights;result.BoneIndices=new int[weights.Length*4];result.Weights=new float[weights.Length*4];
            for(int i=0;i<weights.Length;i++)
            {
                var w=weights[i];int j=i*4;result.BoneIndices[j]=w.boneIndex0;result.BoneIndices[j+1]=w.boneIndex1;result.BoneIndices[j+2]=w.boneIndex2;result.BoneIndices[j+3]=w.boneIndex3;
                result.Weights[j]=w.weight0;result.Weights[j+1]=w.weight1;result.Weights[j+2]=w.weight2;result.Weights[j+3]=w.weight3;
            }
        });
        Read("bone palette",()=>
        {
            var metadata=new Dictionary<IntPtr,(int Id,string Name,Transform Parent)>();
            (int Id,string Name,Transform Parent) Meta(Transform t)
            {
                if(!metadata.TryGetValue(t.Pointer,out var value))metadata[t.Pointer]=value=(t.GetInstanceID(),t.name,t.parent);
                return value;
            }
            foreach(var bone in renderer.bones)
            {
                if(bone==null){result.Bones.Add(new(){Name="missing",WorldMatrix=M.Identity});continue;}
                var ids=new List<int>();var names=new List<string>();
                for(var t=bone;t!=null && ids.Count<128;)
                {var item=Meta(t);ids.Add(item.Id);names.Add(item.Name);t=item.Parent;}
                var info=Meta(bone);
                result.Bones.Add(new(){Id=info.Id,ParentId=info.Parent==null?0:Meta(info.Parent).Id,Name=info.Name,WorldMatrix=Convert.Matrix(bone.localToWorldMatrix),AncestorIds=ids.ToArray(),AncestorNames=names.ToArray()});
            }
        });
        Read("blend shapes",()=>
        {
            result.BlendShapeWeights=new float[mesh.blendShapeCount];
            for(int i=0;i<result.BlendShapeWeights.Length;i++)result.BlendShapeWeights[i]=renderer.GetBlendShapeWeight(i);
        });
        // A failed native bake must not erase the independent raw mesh and
        // skinning evidence needed to investigate that very failure.
        Read("world mesh",()=>
        {
            result.World=live?.World.ToArray()??BakeWorld(renderer);
            result.WorldNormals=live?.WorldNormals.ToArray()??Array.Empty<V>();
        });
        return result;
    }
    private static V[] BakeWorld(SkinnedMeshRenderer renderer)
    {
        var mesh=new Mesh{name="SVSCollisionPlus_DiagnosticScratch",hideFlags=HideFlags.DontUnloadUnusedAsset};
        try
        {
            renderer.BakeMesh(mesh,false);Vector3[] positions=mesh.vertices;
            var matrix=Convert.Matrix(renderer.transform.localToWorldMatrix);
            return positions.Select(p=>V.Transform(Convert.Vec(p),matrix)).ToArray();
        }
        finally{UnityEngine.Object.Destroy(mesh);}
    }
    private void Safe(Action action)
    {
        try{action();}
        catch(Exception e){messages.Enqueue("[Capture] recorder error (collision remains active): "+e);pending=null;nodeMap.Clear();surfaceMap.Clear();}
    }
    private void Submit()
    {
        if(pending==null)return;
        var frame=pending;pending=null;nodeMap.Clear();surfaceMap.Clear();
        if(!writer.IsCompleted){messages.Enqueue("[Capture] writer busy; snapshot skipped.");return;}
        string name=$"capture-{++serial:D4}-frame-{frame.Frame}.json.gz";
        // No Unity calls or borrowed mutable arrays beyond this boundary.
        writer=Task.Run(()=>
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);string file=Path.Combine(DirectoryPath,name);
                CaptureCodec.Write(file+".partial",frame);File.Move(file+".partial",file,true);
                files.Enqueue(file);while(files.Count>8)File.Delete(files.Dequeue());
                // Preserve decisive evidence beyond the two-minute rolling
                // window. These fixed names cannot grow without bound.
                void Pin(string stem)=>File.Copy(file,Path.Combine(DirectoryPath,stem+".json.gz"),true);
                if(!firstBoundSaved && frame.Complete && frame.Nodes.Any(n=>n.Active && n.Probes.Length>0)){Pin("first-bound");firstBoundSaved=true;}
                float depth=frame.Nodes.SelectMany(n=>n.Results).Select(r=>r.MaxDepth).DefaultIfEmpty(0).Max();
                if(frame.Complete && depth>largestDepth){Pin("largest-penetration");largestDepth=depth;}
                if(frame.Nodes.Any(n=>n.Probes.Length==0))Pin("zero-probes");
                if(frame.Nodes.Any(n=>n.Applied && n.Observed && V.DistanceSquared(n.ObservedLocal,n.AfterLocal)>=1e-10f))Pin("pose-overwritten");
                if(!string.IsNullOrEmpty(frame.Error))Pin("solver-error");
                messages.Enqueue($"[Capture] Saved {file}; nodes={frame.Nodes.Count}, surfaces={frame.Surfaces.Count}, complete={frame.Complete}");
            }
            catch(Exception e){messages.Enqueue("[Capture] file write failed: "+e);}
        });
    }
    internal void Flush(){if(pending!=null)Submit();writer.GetAwaiter().GetResult();}
    public void Dispose(){if(stopped)return;stopped=true;Flush();}
}
