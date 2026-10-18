using System.Diagnostics;
using Character;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using V = System.Numerics.Vector3;
using M = System.Numerics.Matrix4x4;

namespace SVSCollisionPlus;

internal sealed class CollisionRuntime : IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<IntPtr,ActorData> actors=new();
    private readonly Il2CppStructArray<RaycastHit> groundHits=new(64);
    private readonly Il2CppReferenceArray<Collider> groundCandidates=new(64);
    private readonly long[] contacts=new long[4];
    private float nextScan, nextReport, retryAt;
    private int failureCount, timedFrames;
    private Switches lastSwitches;
    private bool? lastEnabled;
    private float currentBreastRatio;
    private float currentChestTargetRatio,currentChestDepthRatio;
    private int currentChestContacts,currentChestProbes;
    private int poseKept,poseReplaced;
    private long restoredPoses,replacedPoses,poseAudits,solveSequence,auditSequence;
    private string controlStatus="waiting for first update";
    private double milliseconds;
    private float maxBreastCorrection;
    private int activeBreasts, cacheSurfaces;
    private bool disposed;
    private readonly List<Transform> characterRoots=new();
    private readonly Func<V,float,GroundContact?> groundQuery;
    private readonly int[] currentContacts=new int[3];
    private readonly FrameWork frameWork=new();
    private long scanTicks,prepareTicks,meshTicks,solveTicks,groundQueries,groundBroadQueries,groundBroadSkips,meshUpdates,clothChecks;
    internal CollisionRuntime(Plugin plugin){this.plugin=plugin;groundQuery=Ground;}
    internal bool HasActors=>actors.Count>0;
    internal void Restore()
    {
        if(disposed) return;
        plugin.Recorder?.BeforeRestore();
        ObserveBeforeRestore();
        foreach(var actor in actors.Values) foreach(var node in actor.Nodes)
            try
            {
                var result=node.Restore();
                if(plugin.TrackStatistics)
                {
                    if(result==RestoreResult.Restored)restoredPoses++;
                    else if(result==RestoreResult.Replaced)replacedPoses++;
                }
            }
            catch{node.Applied=false;}
    }
    internal bool PollControls()
    {
        if(disposed)return false;
        bool enabled=plugin.Enabled.Value;var switches=plugin.Switches;
        if(lastEnabled!=enabled || switches!=lastSwitches)
        {
            ResetOffsets();lastEnabled=enabled;lastSwitches=switches;
            if(plugin.TrackStatistics)controlStatus=$"Enabled={enabled}; breast-abdomen={switches.BreastAbdomen}, breast-body={switches.BreastBody}, breast-ground={switches.BreastGround}";
            if(plugin.Diagnostics.Value)plugin.Log.LogInfo($"[Control applied] frame={Time.frameCount}; {controlStatus}; ownedOffsets=0, pendingOffsets=0; restored={restoredPoses}, externallyReplaced={replacedPoses}");
        }
        return enabled && switches.Any;
    }
    internal string StatusText
    {
        get
        {
            bool enabled=plugin.Enabled.Value;
            string audit=poseAudits>0?$"last restore kept={poseKept}, overwritten={poseReplaced}":"restore audit: waiting";
            string off=lastEnabled==false?"Own offsets cleared; native physics remains":"Reset pending on main thread";
            float cap=0;
            for(int i=1;i<=3;i++)if(enabled && plugin.Switches.Enabled((ContactKind)i))
            {var settings=plugin.Settings((ContactKind)i,1);cap=Math.Max(cap,settings.Limit*settings.Strength*100);}
            return $"Collision Plus {Plugin.Version}: {(enabled?"ON":"OFF")} | Root shift: {(enabled?currentBreastRatio*100:0):F3}% height (not overlap)\nChest target: {(enabled?currentChestTargetRatio*100:0):F3}% | Cap: {cap:F3}% | Probes: {(enabled?currentChestProbes:0)} | {(enabled?audit:off)}\nAbdomen: {plugin.BreastAbdomen.Value} [{currentContacts[0]}] | Body: {plugin.BreastBody.Value} [{currentContacts[1]}] | Ground: {plugin.BreastGround.Value} [{currentContacts[2]}]";
        }
    }
    private void ObserveBeforeRestore()
    {
        // Called before the earliest restore (native EarlyUpdate or driver
        // Update), including when the manager's render callback is inlined.
        if(!plugin.TrackStatistics || solveSequence==auditSequence)return;
        auditSequence=solveSequence;poseAudits++;poseKept=poseReplaced=0;
        foreach(var actor in actors.Values)foreach(var node in actor.Nodes)
        {
            if(node.Region!=Region.Breast || !node.Applied || node.Bone==null)continue;
            try
            {
                if(V.DistanceSquared(Convert.Vec(node.Bone.localPosition),node.AppliedLocal)<1e-10f)poseKept++;
                else poseReplaced++;
            }
            catch{poseReplaced++;}
        }
    }
    internal void Step()
    {
        if(disposed) return;
        plugin.Recorder?.Mark("Step");
        if(!PollControls())return;
        var switches=plugin.Switches;
        if(Time.unscaledTime<retryAt) return;
        bool timing=plugin.Diagnostics.Value,statistics=plugin.TrackStatistics;
        long started=timing?Stopwatch.GetTimestamp():0;
        try
        {
            long stage=timing?Stopwatch.GetTimestamp():0;
            if(Time.unscaledTime>=nextScan){plugin.Recorder?.Mark("Scan");Scan();nextScan=Time.unscaledTime+1;}
            if(timing){scanTicks+=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();}
            plugin.Recorder?.Mark("Prepare");frameWork.Prepare(actors.Values,switches);
            if(timing){clothChecks+=frameWork.ClothChecks;prepareTicks+=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();}
            plugin.Recorder?.Mark("MeshUpdate");frameWork.UpdateSurfaces();
            if(timing){meshUpdates+=frameWork.MeshUpdates;cacheSurfaces=frameWork.NativeSurfaces;meshTicks+=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();}
            float dt=Math.Clamp(Time.unscaledDeltaTime,0,.05f);
            activeBreasts=0;currentBreastRatio=currentChestTargetRatio=currentChestDepthRatio=0;currentChestContacts=currentChestProbes=0;
            Array.Clear(currentContacts,0,currentContacts.Length);
            if(statistics)solveSequence++;
            plugin.Recorder?.Begin(actors.Values,frameWork);plugin.Recorder?.Mark("Solve");
            foreach(var entry in frameWork.Nodes)
            {
                if(statistics && entry.Node.Region==Region.Breast)activeBreasts++;
                Solve(entry.Actor,entry.Node,switches,dt,statistics);
            }
            if(timing)solveTicks+=Stopwatch.GetTimestamp()-stage;
            plugin.Recorder?.AfterSolve();
            failureCount=0;
        }
        catch(Exception e)
        {
            plugin.Recorder?.Failure(e);
            ResetOffsets();failureCount++;retryAt=Time.unscaledTime+Math.Min(30,failureCount*3);nextScan=0;
            plugin.Log.LogError($"Collision pass suspended for {retryAt-Time.unscaledTime:F0}s after {e}");
            // Discard cached component/mesh references after a scene reload or failed interop access.
            foreach(var a in actors.Values)a.Dispose();actors.Clear();
            frameWork.Clear();
        }
        if(timing){milliseconds+=(Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency;timedFrames++;}
        if(plugin.Diagnostics.Value && Time.unscaledTime>=nextReport)
        {
            nextReport=Time.unscaledTime+15;
            double tickMs=1000d/Stopwatch.Frequency/Math.Max(1,timedFrames);
            if(actors.Count>0)plugin.Log.LogInfo($"Performance/15s: scan={scanTicks*tickMs:F2}ms, prepare={prepareTicks*tickMs:F2}ms, meshes={meshTicks*tickMs:F2}ms, solve={solveTicks*tickMs:F2}ms; per-frame meshUpdates={meshUpdates/(double)Math.Max(1,timedFrames):F1}, clothChecks={clothChecks/(double)Math.Max(1,timedFrames):F1}, groundQueries={groundQueries/(double)Math.Max(1,timedFrames):F1}, groundBounds={groundBroadQueries/(double)Math.Max(1,timedFrames):F1}, groundSkipped={groundBroadSkips/(double)Math.Max(1,timedFrames):F1}; lastSources={frameWork.Nodes.Count}");
            if(actors.Count>0)plugin.Log.LogInfo($"Contacts/15s: breast-abdomen={contacts[1]}, breast-body={contacts[2]}, breast-ground={contacts[3]}; activeBreasts={activeBreasts}, nativeCacheSurfacesUnused={cacheSurfaces}, maxBreastCorrection={maxBreastCorrection:F5}; currentChestTarget={currentChestTargetRatio*100:F3}%h, currentAcceptedExcessDepth={currentChestDepthRatio*100:F3}%h, currentChestContacts={currentChestContacts}, currentChestProbes={currentChestProbes}; avg={milliseconds/Math.Max(1,timedFrames):F2}ms, actors={actors.Count}; {controlStatus}; restoreAudits={poseAudits}, lastKept={poseKept}, lastOverwritten={poseReplaced}, restored={restoredPoses}, externallyReplaced={replacedPoses}");
            Array.Clear(contacts,0,contacts.Length);milliseconds=0;timedFrames=0;
            maxBreastCorrection=0;
            scanTicks=prepareTicks=meshTicks=solveTicks=groundQueries=groundBroadQueries=groundBroadSkips=meshUpdates=clothChecks=0;
        }
    }
    private void ResetOffsets()
    {
        Restore();foreach(var a in actors.Values)foreach(var n in a.Nodes){n.OffsetLocal=V.Zero;n.MotionKinds=0;}
        currentBreastRatio=currentChestTargetRatio=currentChestDepthRatio=0;currentChestContacts=currentChestProbes=0;
        Array.Clear(currentContacts,0,currentContacts.Length);poseKept=poseReplaced=0;solveSequence=auditSequence=0;poseAudits=0;
    }
    private static long Fingerprint(Human human)
    {
        unchecked
        {
            long hash=17;
            void Add(int id)=>hash=hash*31+id;
            var body=human.body;
            Add(human.transform.lossyScale.GetHashCode());
            Add(body?.GetObjBone()?.GetInstanceID()??0);
            foreach(var s in ActorData.BodyRenderers(human))
            {
                Add(s.GetInstanceID());Add(s.sharedMesh.GetInstanceID());Add(s.sharedMesh.vertexCount);
                foreach(var bone in s.bones)Add(bone?.GetInstanceID()??0);
            }
            var springs=body?.magicaSpringSetting?.GetSpringAll();
            if(springs!=null)foreach(var c in springs){Add(c?.GetInstanceID()??0);Add(c?.TeamId??0);}
            return hash;
        }
    }
    private void Scan()
    {
        var keep=new HashSet<IntPtr>();characterRoots.Clear();
        var humans=Human._list;
        if(humans!=null)
        {
            for(int i=0;i<humans.Count;i++)
            {
                var human=humans[i];
                if(human==null || human.disposed || human.transform==null)continue;
                characterRoots.Add(human.transform); // Includes low-poly/excess characters so a person never becomes a floor.
                if(keep.Count>=plugin.MaxCharacters.Value || human.isReloading || !human.isActiveAndEnabled || !human.hiPoly)continue;
                keep.Add(human.Pointer);long signature=Fingerprint(human);
                if(actors.TryGetValue(human.Pointer,out var old))
                {
                    if(old.Fingerprint==signature && old.Ready)continue;
                    old.Dispose();actors.Remove(human.Pointer);
                }
                var actor=new ActorData(human,signature,plugin);actors.Add(human.Pointer,actor);
            }
        }
        foreach(var key in actors.Keys.Where(k=>!keep.Contains(k)).ToArray()){actors[key].Dispose();actors.Remove(key);}
        foreach(var actor in actors.Values)foreach(var node in actor.Nodes)
        {
            node.Targets.Clear();
            int group=0;
            foreach(var target in actors.Values)
            {
                bool self=ReferenceEquals(actor,target);
                foreach(var surface in target.Surfaces)node.Targets.Add(new ContactTarget(surface.Tree,self?surface.Exclusions.GetValueOrDefault(node.Id):null,group));
                group++;
            }
        }
    }
    private void Solve(ActorData actor,BoneNode node,Switches switches,float dt,bool statistics)
    {
        if(node.Region!=Region.Breast)return;
        float height=actor.Height;
        node.Probes.Clear();
        foreach(var sample in node.Samples)if(sample.Surface.Valid)
            node.Probes.Add(new(sample.Surface.World[sample.Vertex],sample.Weight));
        var work=node.Projection;int activeMask=0;
        for(int i=0;i<3;i++)
        {
            var kind=(ContactKind)(i+1);var settings=plugin.Settings(kind,height);work.Settings[i]=settings;
            bool query=switches.Enabled(kind) && settings.Strength>0 && settings.Limit>0;
            if(query && kind==ContactKind.BreastGround)query=MayTouchGround(node.Probes,settings.Capture);
            work.Results[i]=query?ContactProjection.Solve(node.Probes,node.Targets,kind,settings,work.Contacts,groundQuery):default;
            var result=work.Results[i];
            if(statistics)
            {
                contacts[i+1]+=result.Count;currentContacts[i]+=result.Count;
                currentChestContacts+=result.Count;currentChestDepthRatio=Math.Max(currentChestDepthRatio,result.MaxDepth/height);
            }
            if(result.Offset.LengthSquared()>1e-12f)activeMask|=1<<i;
        }
        if(statistics)currentChestProbes+=node.Probes.Count;
        // Retain the limiting response while an old correction is returning.
        // Changing a toggle resets all state immediately in PollControls.
        int motionMask=activeMask|(node.OffsetLocal.LengthSquared()>1e-12f?node.MotionKinds:0);
        float cap=0,response=float.PositiveInfinity,speed=float.PositiveInfinity;
        for(int i=0;i<3;i++)if((motionMask&(1<<i))!=0 && switches.Enabled((ContactKind)(i+1)))
        {
            var settings=work.Settings[i];
            cap=Math.Max(cap,settings.Limit*settings.Strength);
            response=Math.Min(response,settings.Response);speed=Math.Min(speed,settings.MaxSpeed);
        }
        var desired=ContactProjection.Merge(work.Results,cap);
        if(statistics)currentChestTargetRatio=Math.Max(currentChestTargetRatio,desired.Length()/height);
        var parent=node.Bone.parent;
        M parentMatrix=parent!=null?Convert.Matrix(parent.localToWorldMatrix):M.Identity;
        if(!M.Invert(parentMatrix,out var parentInverse))return;
        var previous=V.TransformNormal(node.OffsetLocal,parentMatrix);
        V offset=cap>0?Geometry.SmoothLimited(previous,desired,1-MathF.Exp(-response*dt),speed*dt,cap):V.Zero;
        node.OffsetLocal=V.TransformNormal(offset,parentInverse);node.MotionKinds=motionMask;
        if(!Geometry.Finite(offset) || offset.LengthSquared()<1e-12f){node.OffsetLocal=V.Zero;node.MotionKinds=0;return;}
        V final=node.BaseWorld+offset;
        node.SavedLocal=Convert.Vec(node.Bone.localPosition);
        node.Bone.position=Convert.Vec(final);
        node.AppliedLocal=Convert.Vec(node.Bone.localPosition);node.Applied=true;
        if(statistics)
        {
            float distance=V.Distance(final,node.BaseWorld);
            maxBreastCorrection=Math.Max(maxBreastCorrection,distance);
            currentBreastRatio=Math.Max(currentBreastRatio,distance/height);
        }
    }
    private bool IsGroundCandidate(Collider collider)
    {
        if(collider==null || collider.isTrigger)return false;
        var t=collider.transform;
        foreach(var root in characterRoots)if(root!=null && t.IsChildOf(root))return false;
        return true;
    }
    private bool MayTouchGround(List<ContactProbe> probes,float capture)
    {
        var min=new V(float.PositiveInfinity);var max=new V(float.NegativeInfinity);
        foreach(var probe in probes)if(Geometry.Finite(probe.Position) && float.IsFinite(probe.Influence) && probe.Influence>0)
        {min=V.Min(min,probe.Position);max=V.Max(max,probe.Position);}
        if(!Geometry.Finite(min) || !Geometry.Finite(max))return false;
        // This box encloses every ray segment from p+Y*capture to p-Y*capture.
        // A small expansion keeps boundary hits conservative. Only an empty
        // eligible result can skip rays; a saturated buffer always falls back.
        var padding=new V(Math.Max(.0001f,capture*.0001f));
        var half=(max-min)*.5f+V.UnitY*capture+padding;
        if(plugin.Diagnostics.Value)groundBroadQueries++;
        int count=Physics.OverlapBoxNonAlloc(Convert.Vec((min+max)*.5f),Convert.Vec(half),groundCandidates,Quaternion.identity,plugin.GroundMask.Value,QueryTriggerInteraction.Ignore);
        if(count>=64)return true;
        for(int i=0;i<count;i++)if(IsGroundCandidate(groundCandidates[i]))return true;
        if(plugin.Diagnostics.Value)groundBroadSkips++;return false;
    }
    private GroundContact? Ground(V p,float capture)
    {
        var result=QueryGround(p,capture);plugin.Recorder?.Ground(p,capture,result);return result;
    }
    private GroundContact? QueryGround(V p,float capture)
    {
        if(plugin.Diagnostics.Value)groundQueries++;
        Vector3 origin=Convert.Vec(p+V.UnitY*capture);
        int count=Physics.RaycastNonAlloc(origin,Vector3.down,groundHits,capture*2,plugin.GroundMask.Value,QueryTriggerInteraction.Ignore);
        // A full buffer has no reliable nearest hit; omit this ambiguous query.
        if(count>=64)return null;
        float best=float.PositiveInfinity;GroundContact? result=null;
        for(int i=0;i<count;i++)
        {
            var hit=groundHits[i];var collider=hit.collider;
            if(!IsGroundCandidate(collider))continue;
            V point=Convert.Vec(hit.point),normal=Convert.Vec(hit.normal);
            if(!Geometry.Finite(normal) || !Geometry.Finite(point) || normal.LengthSquared()<.5f)continue;
            normal=V.Normalize(normal);
            if(normal.Y<plugin.GroundConfig.GroundNormalY.Value)continue;
            float separation=V.Dot(p-point,normal);
            float distance=V.DistanceSquared(p,point);
            if(separation>=0 || distance>capture*capture || distance>=best)continue;
            best=distance;result=new(point,normal);
        }
        return result;
    }
    public void Dispose()
    {
        if(disposed)return;Restore();foreach(var a in actors.Values)a.Dispose();actors.Clear();frameWork.Clear();disposed=true;
    }
}



