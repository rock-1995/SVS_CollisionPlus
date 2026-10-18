using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SVSCollisionPlus;
using Old=Baseline013;

if(args.Length<2)throw new ArgumentException("PerformanceTests <capture directory> <report.json>");
var frames=Directory.GetFiles(args[0],"*.json.gz").Select(CaptureCodec.Read).Where(f=>f.Complete && f.Nodes.Any(n=>n.Active && n.Probes.Length>0)).OrderBy(f=>f.Frame).GroupBy(f=>f.Frame).Select(g=>g.First()).ToArray();
var report=new Report{Frames=frames.Length,Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription};
int checks=0;
void Check(bool valid,string message){checks++;if(!valid)throw new Exception(message);}
SearchChecks.Run(Check);
Console.WriteLine($"PASS synthetic search: {checks} assertions including tied normals, moving meshes, changed regions and every toggle.");
var firstSurfaces=frames.SelectMany(f=>f.Surfaces).Where(s=>s.Valid && s.World.Length>0).GroupBy(s=>s.RendererId).ToDictionary(g=>g.Key,g=>g.First());
foreach(bool refitted in new[]{false,true})foreach(var frame in frames)
{
    var liveTrees=new SurfaceTree[frame.Surfaces.Count];var oldTrees=new Old.SurfaceTree[liveTrees.Length];
    for(int i=0;i<liveTrees.Length;i++)
    {
        var s=frame.Surfaces[i];if(!s.Valid || s.World.Length==0)continue;
        var initial=refitted?firstSurfaces[s.RendererId]:s;
        Check(initial.World.Length==s.World.Length && initial.Triangles.SequenceEqual(s.Triangles),"capture topology changed");
        liveTrees[i]=new(initial.World,s.Triangles,s.Regions){Vertices=s.World,Enabled=s.Enabled};liveTrees[i].Refit(s.WorldNormals);
        oldTrees[i]=new(initial.World,s.Triangles,s.Regions.Select(r=>(Old.Region)r).ToArray()){Vertices=s.World,Enabled=s.Enabled};oldTrees[i].Refit(s.WorldNormals);
    }
    var cases=new List<Case>();
    foreach(var n in frame.Nodes.Where(n=>n.Active && n.Probes.Length>0))
    {
        var targets=n.Targets.Where(t=>liveTrees[t.Surface]!=null).Select(t=>new ContactTarget(liveTrees[t.Surface],t.Excluded,t.Group)).ToArray();
        var oldTargets=n.Targets.Where(t=>oldTrees[t.Surface]!=null).Select(t=>new Old.ContactTarget(oldTrees[t.Surface],t.Excluded,t.Group)).ToArray();
        var oldProbes=n.Probes.Select(p=>new Old.ContactProbe(p.Position,p.Influence)).ToArray();
        var contacts=new List<FrozenContact>();var oldContacts=new List<Old.FrozenContact>();
        foreach(var kind in new[]{ContactKind.BreastAbdomen,ContactKind.BreastBody})
        {
            var setting=n.Settings[(int)kind-1];
            var oldSetting=ToOld(setting);int mismatch=0;
            var baseline=Old.ContactProjection.Solve(oldProbes,oldTargets,(Old.ContactKind)kind,oldSetting,oldContacts);
            foreach(var variant in new[]{setting,setting with{Capture=setting.Capture*2},setting with{NormalAlignment=-1},setting with{Radius=setting.Capture},setting with{Compression=0},setting with{ConflictTolerance=.007f}})
            {
                var expected=Old.ContactProjection.Solve(oldProbes,oldTargets,(Old.ContactKind)kind,ToOld(variant),oldContacts);
                var actual=ContactProjection.Solve(n.Probes,targets,kind,variant,contacts);
                Check(expected.Offset==actual.Offset && expected.Count==actual.Count && expected.ProbeCount==actual.ProbeCount && expected.Coverage==actual.Coverage && expected.MaxDepth==actual.MaxDepth,$"result mismatch frame={frame.Frame} refit={refitted} node={n.Name} kind={kind}: {expected} / {actual}");
                Check(contacts.Count==oldContacts.Count,"frozen contact count mismatch");
                for(int c=0;c<contacts.Count;c++)Check(contacts[c].Normal==oldContacts[c].Normal && contacts[c].Distance==oldContacts[c].Distance && contacts[c].Weight==oldContacts[c].Weight,"frozen contact mismatch");
                report.SolverComparisons++;
            }
            foreach(var target in targets)foreach(var p in n.Probes)
            {
                var oldTarget=oldTargets[Array.IndexOf(targets,target)];
                bool a=target.Tree.Nearest(p.Position,setting.Capture,target.Excluded,Switches.Only(kind),Region.Breast,out var aq,out var an,out var ak);
                bool b=oldTarget.Tree.Nearest(p.Position,setting.Capture,oldTarget.Excluded,Old.Switches.Only((Old.ContactKind)kind),Old.Region.Breast,out var bq,out var bn,out var bk);
                Check(a==b && aq==bq && an==bn && (int)ak==(int)bk,"nearest hit/normal differs");report.NearestComparisons++;
            }
            if(frame.Switches.Enabled(kind) && (int)kind-1<n.Results.Length)
            {
                var recorded=n.Results[(int)kind-1];
                if(recorded.Count!=baseline.Count || Vector3.Distance(recorded.Offset,baseline.Offset)>1e-6f || Math.Abs(recorded.MaxDepth-baseline.MaxDepth)>1e-6f)mismatch++;
            }
            cases.Add(new(){Kind=kind,Current=()=>ContactProjection.Solve(n.Probes,targets,kind,setting,contacts),Baseline=()=>Old.ContactProjection.Solve(oldProbes,oldTargets,(Old.ContactKind)kind,oldSetting,oldContacts),RecordedMismatch=mismatch});
        }
    }
    foreach(var kind in new[]{ContactKind.BreastAbdomen,ContactKind.BreastBody})
    {
        var selected=cases.Where(c=>c.Kind==kind).ToArray();
        void Baseline(){foreach(var c in selected)c.Baseline();}
        void Current(){foreach(var c in selected)c.Current();}
        for(int i=0;i<8;i++){Baseline();Current();}
        var oldMs=new List<double>();var newMs=new List<double>();
        for(int round=0;round<5;round++)
        {
            if(round%2==0){oldMs.Add(Measure(Baseline));newMs.Add(Measure(Current));}
            else{newMs.Add(Measure(Current));oldMs.Add(Measure(Baseline));}
        }
        oldMs.Sort();newMs.Sort();
        var sample=new Sample{Frame=frame.Frame,RefittedFromFirst=refitted,Kind=kind.ToString(),ActiveNodes=selected.Length,Probes=frame.Nodes.Where(n=>n.Active).Sum(n=>n.Probes.Length),BaselineMs=oldMs[2],OptimizedMs=newMs[2],RecordedMismatch=selected.Sum(c=>c.RecordedMismatch)};
        report.Samples.Add(sample);
        Console.WriteLine($"frame {frame.Frame} {(refitted?"refit":"fresh")} {kind}: {sample.BaselineMs:F3} -> {sample.OptimizedMs:F3} ms; {sample.BaselineMs/sample.OptimizedMs:F2}x; recorded mismatches {sample.RecordedMismatch}");
    }
    void OldRefit(){for(int i=0;i<oldTrees.Length;i++)oldTrees[i]?.Refit(frame.Surfaces[i].WorldNormals);}
    void NewRefit(){for(int i=0;i<liveTrees.Length;i++)liveTrees[i]?.Refit(frame.Surfaces[i].WorldNormals);}
    OldRefit();NewRefit();
    report.Samples.Add(new(){Frame=frame.Frame,RefittedFromFirst=refitted,Kind="RefitAllSurfaces",BaselineMs=Measure(OldRefit),OptimizedMs=Measure(NewRefit)});
}
report.Checks=checks;
File.WriteAllText(args[1],JsonSerializer.Serialize(report,new JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
Console.WriteLine($"PASS: {checks} assertions; {report.SolverComparisons} exact solver comparisons, {report.NearestComparisons} exact nearest-surface comparisons.");

static double Measure(Action action)
{
    // Warmed median across alternating old/new runs on the same .NET 6 host.
    var sw=Stopwatch.StartNew();int count=0;
    do{action();count++;}while(sw.Elapsed.TotalMilliseconds<45);
    return sw.Elapsed.TotalMilliseconds/count;
}
static Old.ContactSettings ToOld(ContactSettings s)=>new(s.Radius,s.Limit,s.Strength,s.Compression,s.Coverage,s.NormalAlignment,s.Transition,s.Capture,s.MinInfluence,s.ConflictTolerance,s.Iterations,s.Response,s.MaxSpeed,s.GroundNormalY);
sealed class Case{public ContactKind Kind;public Func<ContactResult> Current;public Func<Old.ContactResult> Baseline;public int RecordedMismatch;}
sealed class Sample{public int Frame,ActiveNodes,Probes,RecordedMismatch;public bool RefittedFromFirst;public string Kind;public double BaselineMs,OptimizedMs;}
sealed class Report{public int Frames,Checks,SolverComparisons,NearestComparisons;public string Runtime;public List<Sample> Samples=new();}
