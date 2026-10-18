using System.Numerics;

namespace SVSCollisionPlus;

internal sealed class ReplayReport
{
    public string Version="",Reason="",Error="";
    public int Frame;
    public bool Complete,Observed;
    public List<string> Findings=new();
    public List<NodeReport> Nodes=new();
}
internal sealed class NodeReport
{
    public string Name="",ClothState="";
    public int Id,Probes;
    public bool Active,Applied,Observed,Kept;
    public float RootShift,MaximumSkinShift;
    public List<KindReport> Contacts=new();
}
internal sealed class KindReport
{
    public string Kind="";
    public ContactResult Recorded,Replayed,AfterSkin;
    public bool Enabled,Matches;
    public Dictionary<string,int> Rejections=new();
    public List<string> Examples=new();
    public Dictionary<string,ContactResult> OfflineVariants=new();
}
internal static class CaptureReplay
{
    internal static ReplayReport Analyze(CaptureFrame frame)
    {
        var report=new ReplayReport{Version=frame.Version,Reason=frame.Reason,Frame=frame.Frame,Complete=frame.Complete,Observed=frame.ObservedBeforeRestore,Error=frame.Error};
        if(!frame.Enabled)report.Findings.Add("Master switch is disabled.");
        if(frame.Nodes.Count==0)report.Findings.Add("No bound nodes. Inspect Inventory, raw surfaces, stage and errors; this is not a contact-strength issue.");
        var trees=frame.Surfaces.Select(s=>Tree(s,false)).ToArray();
        var afterTrees=frame.Surfaces.Select(s=>Tree(s,true)).ToArray();
        for(int index=0;index<frame.Surfaces.Count;index++)
        {
            var s=frame.Surfaces[index];
            if(!string.IsNullOrEmpty(s.RawError))report.Findings.Add($"surface {index} raw capture error: {s.RawError}");
            if(!string.IsNullOrEmpty(s.AfterError))report.Findings.Add($"surface {index} after-bake error: {s.AfterError}");
            report.Findings.Add($"surface {index}: valid={s.Valid}; vertices={s.World.Length}; abdomenTriangles={s.Regions.Count(r=>r==Region.Abdomen)}; breastTriangles={s.Regions.Count(r=>r==Region.Breast)}");
            if(s.SourceVertices.Length!=s.World.Length || s.Weights.Length!=s.SourceVertices.Length*4)continue;
            float max=0;double sum=0;int count=0,invalid=0;
            for(int v=0;v<s.SourceVertices.Length;v++)
            {
                var p=Vector3.Zero;bool valid=true;
                for(int j=v*4;j<v*4+4;j++)
                {
                    float w=s.Weights[j];if(w==0)continue;int b=s.BoneIndices[j];
                    if((uint)b>=s.Bindposes.Length || (uint)b>=s.Bones.Count){invalid++;valid=false;break;}
                    p+=Vector3.Transform(s.SourceVertices[v],s.Bindposes[b]*s.Bones[b].WorldMatrix)*w;
                }
                if(!valid)continue;float d=Vector3.Distance(p,s.World[v]);max=Math.Max(max,d);sum+=d*d;count++;
            }
            report.Findings.Add($"surface {index} independent skin check: max={max:R}; rms={Math.Sqrt(sum/Math.Max(1,count)):R}; invalidIndices={invalid}; activeBlendShapes={s.BlendShapeWeights.Count(w=>Math.Abs(w)>1e-5f)} (blend shapes can explain residuals)");
        }
        foreach(var n in frame.Nodes)
        {
            var nr=new NodeReport{Name=n.Name,Id=n.Id,ClothState=n.ClothState,Probes=n.Probes.Length,Active=n.Active,Applied=n.Applied,Observed=n.Observed,
                Kept=n.Applied && n.Observed && Vector3.DistanceSquared(n.ObservedLocal,n.AfterLocal)<1e-10f,RootShift=Vector3.Distance(n.BeforeWorld,n.AfterWorld)};
            report.Nodes.Add(nr);
            if(!n.Active)report.Findings.Add($"{n.Name}: physics node inactive ({n.ClothState}).");
            if(n.Probes.Length==0)report.Findings.Add($"{n.Name}: zero probes; inspect skin IDs, weights, renderer validity and root hierarchy.");
            int expectedSamples=0;
            foreach(var s in frame.Surfaces.Where(s=>s.Actor==n.Actor))
                for(int v=0;v<s.SourceVertices.Length && v*4+3<s.Weights.Length;v++)
                {
                    float influence=0;
                    for(int j=v*4;j<v*4+4;j++)
                    {int b=s.BoneIndices[j];if((uint)b<s.Bones.Count && s.Bones[b].AncestorIds.Contains(n.Id))influence+=s.Weights[j];}
                    if(influence>=.25f)expectedSamples++;
                }
            report.Findings.Add($"{n.Name}: raw hierarchy/weights imply {expectedSamples} breast samples; recorded samples={n.Samples.Count}; valid probes={n.Probes.Length}.");
            var targets=n.Targets.Where(t=>(uint)t.Surface<trees.Length && trees[t.Surface]!=null).Select(t=>new ContactTarget(trees[t.Surface],t.Excluded,t.Group)).ToArray();
            var afterTargets=n.Targets.Where(t=>(uint)t.Surface<afterTrees.Length && afterTrees[t.Surface]!=null).Select(t=>new ContactTarget(afterTrees[t.Surface],t.Excluded,t.Group)).ToArray();
            var afterProbes=new List<ContactProbe>();
            foreach(var sample in n.Samples)
            {
                var s=frame.Surfaces[sample.Surface];if((uint)sample.Vertex>=s.AfterWorld.Length)continue;
                afterProbes.Add(new(s.AfterWorld[sample.Vertex],sample.Weight));
                if((uint)sample.Vertex<s.World.Length)nr.MaximumSkinShift=Math.Max(nr.MaximumSkinShift,Vector3.Distance(s.World[sample.Vertex],s.AfterWorld[sample.Vertex]));
            }
            GroundContact? Ground(Vector3 p,float range)
            {
                foreach(var g in frame.Ground)if(g.Position==p && g.Capture==range)return g.Contact;
                return null;
            }
            for(int i=0;i<3 && i<n.Settings.Length;i++)
            {
                var kind=(ContactKind)(i+1);var settings=n.Settings[i];var trace=new ContactTrace();
                var actual=n.Active && frame.Enabled && frame.Switches.Enabled(kind)?ContactProjection.Solve(n.Probes,targets,kind,settings,new(),Ground,trace):default;
                var recorded=i<n.Results.Length?n.Results[i]:default;
                var kr=new KindReport{Kind=kind.ToString(),Enabled=frame.Switches.Enabled(kind),Recorded=recorded,Replayed=actual,Rejections=trace.Counts,Examples=trace.Examples,
                    Matches=!n.Active || (recorded.Count==actual.Count && Vector3.Distance(recorded.Offset,actual.Offset)<1e-6f && Math.Abs(recorded.MaxDepth-actual.MaxDepth)<1e-6f)};
                if(afterProbes.Count>0 && kind!=ContactKind.BreastGround)kr.AfterSkin=ContactProjection.Solve(afterProbes,afterTargets,kind,settings,new());
                // These are offline counterfactuals. They never change the game.
                if(kind!=ContactKind.BreastGround && n.Active)
                {
                    kr.OfflineVariants["no direction filter"]=ContactProjection.Solve(n.Probes,targets,kind,settings with{NormalAlignment=-1},new());
                    kr.OfflineVariants["double search range"]=ContactProjection.Solve(n.Probes,targets,kind,settings with{Capture=settings.Capture*2},new());
                    kr.OfflineVariants["wide edge tolerance"]=ContactProjection.Solve(n.Probes,targets,kind,settings with{Radius=settings.Capture},new());
                    kr.OfflineVariants["no compression allowance"]=ContactProjection.Solve(n.Probes,targets,kind,settings with{Compression=0},new());
                }
                nr.Contacts.Add(kr);
                if(!kr.Matches)report.Findings.Add($"{n.Name}/{kind}: replay differs from recorded live output; inspect capture completeness or scheduling.");
            }
            if(n.Applied && nr.RootShift>1e-5f && nr.MaximumSkinShift<1e-7f)report.Findings.Add($"{n.Name}: root moved but captured skinned mesh did not follow.");
            if(n.Applied && n.Observed && !nr.Kept)report.Findings.Add($"{n.Name}: later update replaced the added pose before restoration.");
        }
        return report;
    }
    private static SurfaceTree Tree(SurfaceCapture surface,bool after)
    {
        var vertices=after?surface.AfterWorld:surface.World;
        if(vertices.Length==0 || surface.Triangles.Length%3!=0 || surface.Regions.Length!=surface.Triangles.Length/3 || surface.Triangles.Any(i=>(uint)i>=vertices.Length))return null;
        var tree=new SurfaceTree(vertices,surface.Triangles,surface.Regions){Enabled=surface.Enabled};
        tree.Refit(surface.WorldNormals.Length==vertices.Length?surface.WorldNormals:null);return tree;
    }
}
