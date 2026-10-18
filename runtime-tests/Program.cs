using System.Text.Json;
using System.Reflection;
using Character;
using UnityEngine;
using SVSCollisionPlus;
using V=System.Numerics.Vector3;
using M=System.Numerics.Matrix4x4;
using U=UnityEngine.Vector3;
using C=SVSCollisionPlus.Convert;

int checks=0;
void Check(bool good,string text){checks++;if(!good)throw new Exception(text);}
// Both saved switches used to trigger a stripped callback during Load,
// before any physics hooks were installed.
foreach(var option in new[]{"Diagnostics","ShowStatusOverlay"})
{
    var diagnosticPlugin=new Plugin();
    diagnosticPlugin.Config.InitialValues.Add(new("04 Advanced / 高级",option),true);
    int hooks=HarmonyLib.Harmony.Patched.Count;
    diagnosticPlugin.Load();
    Check(diagnosticPlugin.TrackStatistics && Application.AuditSubscriptions==0,"Saved "+option+" loads without stripped callback registration");
    Check(HarmonyLib.Harmony.Patched.Skip(hooks).Select(m=>m.Name).SequenceEqual(new[]{"AfterEarlyUpdate","AfterLateUpdate","PostLateUpdate","LateUpdate"}),"Physics and observation hooks installed with "+option);
    var driver=new CollisionDriver(IntPtr.Zero);driver.Update();driver.OnGUI();
    Check(diagnosticPlugin.Unload(),"Diagnostic unload avoids stripped callback removal");
}
var plugin=new Plugin();plugin.Load();
Check(plugin.BreastAbdomen.Value && !plugin.BreastBody.Value && !plugin.BreastGround.Value,"Focused default switches");
Check(!plugin.Diagnostics.Value && !plugin.ShowStatusOverlay.Value && Application.AuditSubscriptions==0,"Quiet release default");
Check(!plugin.CaptureEvidence.Value && !plugin.Recorder.Enabled,"Normal play does not capture full mesh evidence by default");
var root=new Transform{name="actor"};
var breast=new Transform{name="cf_j_bust01_L",parent=root};
var child=new Transform{name="cf_j_bust02_L",parent=breast};
var belly=new Transform{name="cf_s_waist01",parent=root};
var spring=new MagicaCloth.MagicaBoneSpring();spring.ClothTarget.rootList.AddRange(new[]{breast,child});
var mesh=new Mesh{name="o_body",vertices=new[]{new U(-.3f,0,-.3f),new U(0,0,.3f),new U(.3f,0,-.3f),new U(0,-.04f,0),new U(-.03f,.08f,.02f),new U(.03f,.08f,.02f)},
    boneWeights=Enumerable.Range(0,6).Select(i=>new BoneWeight{boneIndex0=i<3?0:1,weight0=1}).ToArray(),triangles=new[]{0,1,2,3,4,5}};
var renderer=new SkinnedMeshRenderer{name="o_body",sharedMesh=mesh,bones=new[]{belly,child}};
var go=new GameObject();go.Components.Add(renderer);
// Deliberately misleading liquid collider: selecting it would miss the body.
go.Components.Add(new SkinnedMeshRenderer{name="o_hit_hara",sharedMesh=new Mesh{name="o_hit_hara"},bones=Array.Empty<Transform>()});
var human=new Human{gameObject=go,transform=root,body=new(){rendBody=renderer,magicaSpringSetting=new(){Entries=new[]{new HumanMagicaSpringSetting.Entry{spring=spring}}}}};
foreach(int instanceId in new[]{-1,int.MinValue,-1024,42})
{
    breast.InstanceIdOverride=instanceId;
    using var signedActor=new ActorData(human,0,plugin);
    Check(signedActor.Ready && signedActor.Nodes[0].Samples.Count==3,"Signed instance ID "+instanceId+" binds all breast samples");
    Check(signedActor.Surfaces[0].Owners.Take(3).All(owner=>owner==null),"Unowned belly remains unowned for instance ID "+instanceId);
}
breast.InstanceIdOverride=null;
using(var actor=new ActorData(human,0,plugin))
{
    Check(actor.Nodes.Count==1 && actor.Nodes[0].Bone==breast,"Nested spring roots must not double shift");
    Check(actor.Surfaces.Count==1 && actor.Surfaces[0].Renderer==renderer,"Actual body selected instead of flat liquid collider");
    var s=actor.Surfaces[0];Check(actor.Nodes[0].Samples.Count==3,"Actual breast weights bind");
    var scratch=s.Baked;scratch.SimulateUnusedAssetCleanup();
    Check(!scratch.Destroyed && s.Update(),"Scratch survives Unity unused-asset cleanup");
    UnityEngine.Object.Destroy(scratch);
    Check(s.Update() && !ReferenceEquals(s.Baked,scratch) && !s.Baked.Destroyed,"Destroyed scratch is recreated before BakeMesh");
    Check(s.Exclusions[breast.GetInstanceID()][1] && !s.Exclusions[breast.GetInstanceID()][0],"Self exclusion leaves belly available");
    var old=s.World[0];mesh.vertices[0].y+=.03f;s.Update();Check(s.World[0]!=old,"Same mesh vertex deformation is read live");mesh.vertices[0].y-=.03f;
    mesh.bindposes=new[]{Matrix4x4.From(M.CreateTranslation(0,.02f,0)),Matrix4x4.From(M.Identity)};
    s.Update();Check(Math.Abs(s.World[1].Y-.02f)<1e-6,"Live pregnancy bind-pose palette is included");
    mesh.bindposes=Array.Empty<Matrix4x4>();
    mesh.boneWeights[3]=new(){boneIndex0=0,weight0=1};s.Update();actor.RebuildSamples();Check(actor.Nodes[0].Samples.Count==2,"In-place weight changes refresh samples");
    mesh.boneWeights[3]=new(){boneIndex0=1,weight0=1};s.Update();actor.RebuildSamples();
    var n=actor.Nodes[0];n.SavedLocal=new(0,0,0);n.AppliedLocal=new(0,.1f,0);n.Applied=true;breast.localPosition=new(0,.1f,0);
    Check(n.Restore()==RestoreResult.Restored && breast.localPosition.y==0,"Owned correction restores");
    n.Applied=true;breast.localPosition=new(0,.2f,0);Check(n.Restore()==RestoreResult.Replaced && breast.localPosition.y==.2f,"External pose writer preserved");breast.localPosition=new();
    var f=new FrameWork();spring.Running=false;f.Prepare(new[]{actor},plugin.Switches);Check(f.Nodes.Count==0,"Paused native physics skipped");spring.Running=true;
    renderer.sharedMesh=new Mesh();Check(!s.Update() && !s.Tree.Enabled,"Replaced mesh invalidates old surface");renderer.sharedMesh=mesh;
    renderer.enabled=false;Check(!s.Update(),"Hidden mesh skipped");renderer.enabled=true;
    var finalScratch=s.Baked;actor.Dispose();
    Check(finalScratch.Destroyed && !s.Update() && s.Baked==null,"Disposal releases protected scratch and cannot resurrect it");
}
Human._list.Add(human);
for(int frame=1;frame<=180;frame++){Time.frameCount=frame;Time.unscaledTime=frame/60f;plugin.Runtime.Restore();plugin.Runtime.Step();}
Check(breast.localPosition.y>.025f && breast.localPosition.y<.06f,"End-to-end breast correction converges outward");
Check(belly.localPosition.y==0,"Belly bone never moved");
plugin.Enabled.Value=false;plugin.Runtime.PollControls();Check(Math.Abs(breast.localPosition.y)<1e-6,"Master off restores immediately");
plugin.Enabled.Value=true;
var manager=new MagicaCloth.MagicaPhysicsManager();
void Hook(string name)
{
    var method=typeof(Plugin).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic);
    method.Invoke(null,method.GetParameters().Length==0?null:new object[]{manager});
}
Hook("AfterNormalPhysics");float normalY=breast.localPosition.y;Check(normalY>0,"Normal mode solves after normal write-back");
Hook("AfterDelayedPhysics");Check(breast.localPosition.y==normalY,"Normal mode does not solve twice");
new CollisionDriver(IntPtr.Zero).Update();Check(breast.localPosition.y==0,"Paused manager releases last render pose via driver");
manager.IsDelay=true;Hook("AfterNormalPhysics");Check(breast.localPosition.y==0,"Delay mode does not solve before delayed write-back");
Hook("AfterDelayedPhysics");Check(breast.localPosition.y>0,"Delay mode solves after delayed write-back");
plugin.ShowStatusOverlay.Value=true;
Time.frameCount++;plugin.Runtime.Restore();Hook("AfterDelayedPhysics");
Time.frameCount++;Hook("BeforePhysics");
Check(plugin.Runtime.StatusText.Contains("last restore kept=1, overwritten=0"),"Earliest restore observes previous frame's retained correction without a render callback");
Time.frameCount++;plugin.Runtime.Restore();Hook("AfterDelayedPhysics");
breast.localPosition=new(0,.2f,0);Time.frameCount++;new CollisionDriver(IntPtr.Zero).Update();
Check(plugin.Runtime.StatusText.Contains("last restore kept=0, overwritten=1"),"Driver restore detects later pose writer without a render callback");
Check(breast.localPosition.y==.2f,"Audit and restoration preserve external writer");
plugin.Runtime.Restore();breast.localPosition=new();Time.frameCount++;Hook("AfterDelayedPhysics");
plugin.Unload();Check(Math.Abs(breast.localPosition.y)<1e-6,"Unload restores");Human._list.Clear();
CaptureChecks.Run(human,breast,renderer,Check);
Console.WriteLine($"PASS SVS adapter: {checks} checks.");

if(args.Length==0)return;
using var doc=JsonDocument.Parse(File.ReadAllText(args[0]));
var fixture=doc.RootElement.GetProperty("meshes")[0];
float[] Floats(JsonElement x)=>x.EnumerateArray().Select(e=>e.GetSingle()).ToArray();
V Vec(JsonElement x){var f=Floats(x);return new(f[0],f[1],f[2]);}
M Mat(JsonElement x){var a=Floats(x);return new(a[0],a[4],a[8],a[12],a[1],a[5],a[9],a[13],a[2],a[6],a[10],a[14],a[3],a[7],a[11],a[15]);}
var names=fixture.GetProperty("bones").EnumerateArray().Select(e=>e.GetString()).ToArray();
var binds=fixture.GetProperty("bindposes").EnumerateArray().Select(Mat).ToArray();
var vertices=fixture.GetProperty("vertices").EnumerateArray().Select(Vec).ToArray();
var boneMap=new Dictionary<string,Transform>();var fixtureRoot=new Transform{name="fixture"};
foreach(var chain in fixture.GetProperty("boneAncestors").EnumerateArray())
{
    Transform parent=fixtureRoot;
    foreach(var name in chain.EnumerateArray().Select(e=>e.GetString()).Reverse())
    {if(!boneMap.TryGetValue(name,out var t))boneMap[name]=t=new(){name=name,parent=parent};parent=t;}
}
var bones=names.Select(n=>boneMap[n]).ToArray();
for(int i=0;i<bones.Length;i++){M.Invert(binds[i],out var inv);bones[i].Matrix=inv;}
var weights=fixture.GetProperty("weights").EnumerateArray().Select(Floats).ToArray();
var indices=fixture.GetProperty("boneIndices").EnumerateArray().Select(e=>e.EnumerateArray().Select(v=>v.GetInt32()).ToArray()).ToArray();
var sm=new Mesh{name="o_body",vertices=vertices.Select(C.Vec).ToArray(),bindposes=binds.Select(Matrix4x4.From).ToArray(),triangles=fixture.GetProperty("triangles").EnumerateArray().Select(v=>v.GetInt32()).ToArray(),
    boneWeights=weights.Select((w,i)=>new BoneWeight{boneIndex0=indices[i][0],weight0=w[0],boneIndex1=indices[i][1],weight1=w[1],boneIndex2=indices[i][2],weight2=w[2],boneIndex3=indices[i][3],weight3=w[3]}).ToArray()};
var sr=new SkinnedMeshRenderer{name="o_body",sharedMesh=sm,bones=bones};var fg=new GameObject();fg.Components.Add(sr);
var roots=new[]{boneMap["cf_j_bust01_L"],boneMap["cf_j_bust01_R"]};
var entries=roots.Select(t=>{var spring=new MagicaCloth.MagicaBoneSpring();spring.ClothTarget.rootList.Add(t);return new HumanMagicaSpringSetting.Entry{spring=spring};}).ToArray();
var fh=new Human{gameObject=fg,transform=fixtureRoot,body=new(){rendBody=sr,magicaSpringSetting=new(){Entries=entries}}};
var fp=new Plugin();fp.Load();using var fa=new ActorData(fh,0,fp);
Check(fa.Ready && fa.Nodes.Count==2 && fa.Nodes.All(n=>n.Samples.Count>100),"Actual SVS skin has substantial breast samples on both sides");
var lookup=names.Select((name,i)=>(name,p:bones[i].Matrix.Translation)).ToDictionary(x=>x.name,x=>x.p);
V center=lookup["cf_s_waist01"];
var points=vertices.Select(v=>v-center).ToArray();
var lm=SVSPregnancy.TorsoLandmarks.Read(lookup.ToDictionary(x=>x.Key,x=>x.Value-center));
var profile=SVSPregnancy.TorsoProfile.Build(points,lm.Floor.Y,lm.Waist.Y,lm.Chest.Y);
var settings=args.Length>1?JsonSerializer.Deserialize<SVSPregnancy.VtxSettings>(JsonDocument.Parse(File.ReadAllText(args[1])).RootElement.GetProperty("vtx").GetRawText()):new SVSPregnancy.VtxSettings();
var results=new List<object>();int improved=0,active=0;
foreach(float stage in new[]{0f,.5f,1f})foreach(float drop in new[]{0f,.03f,.06f})
{
    // Real SVS topology with the installed pregnancy formula; breast vertices
    // retain their original shape, just as pregnancy's breast exclusion does.
    for(int i=0;i<vertices.Length;i++)
    {
        bool breastOwned=Enumerable.Range(0,4).Any(k=>weights[i][k]>0 && (names[indices[i][k]].Contains("bust") || names[indices[i][k]].Contains("bnip")));
        sm.vertices[i]=C.Vec(breastOwned?vertices[i]:SVSPregnancy.BellyShape.Deform(points[i],profile,stage,settings)+center);
    }
    foreach(var t in roots)t.localPosition=new(0,-drop*fa.Height,0);
    var s=fa.Surfaces[0];s.Update();
    foreach(var node in fa.Nodes)
    {
        var probes=node.Samples.Select(p=>new ContactProbe(s.World[p.Vertex],p.Weight)).ToArray();
        var targets=new[]{new ContactTarget(s.Tree,s.Exclusions[node.Id])};var tuning=fp.Settings(ContactKind.BreastAbdomen,fa.Height);
        var before=ContactProjection.Solve(probes,targets,ContactKind.BreastAbdomen,tuning,new());
        var afterProbes=probes.Select(p=>p with{Position=p.Position+before.Offset*p.Influence}).ToArray();
        var after=ContactProjection.Solve(afterProbes,targets,ContactKind.BreastAbdomen,tuning,new());
        Check(Geometry.Finite(before.Offset) && before.Offset.Length()<=tuning.Limit+1e-6,"Actual mesh correction finite and bounded");
        if(before.Offset.Length()>1e-5){active++;if(after.MaxDepth<before.MaxDepth)improved++;}
        results.Add(new{stage,drop,side=node.Bone.name,probes=probes.Length,contacts=before.Count,offset=before.Offset.Length(),beforeDepth=before.MaxDepth,afterDepth=after.MaxDepth});
    }
}
Check(active>0 && improved>0,"Real mesh pregnancy scenarios exercise and improve penetration");
Console.WriteLine($"PASS actual SVS mesh: {vertices.Length} vertices, {fa.Nodes.Sum(n=>n.Samples.Count)} breast probes, height={fa.Height:F4}; {active} active contact cases, {improved} lower maximum accepted depth. Total checks={checks}.");
if(args.Length>2)File.WriteAllText(args[2],JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
// Exercise the entire capture/serialization/replay path at real mesh scale,
// using the same controlled fixture (not a claim of native game execution).
Human._list.Add(fh);Time.unscaledTime+=30;Time.frameCount++;
fp.CaptureEvidence.Value=true;
fp.Runtime.Step();Time.frameCount++;fp.Runtime.Restore();fp.Recorder.Flush();
var realCapture=CaptureCodec.Read(Directory.GetFiles(fp.Recorder.DirectoryPath,"capture-*.json.gz").OrderBy(x=>x).Last());
var realReport=CaptureReplay.Analyze(realCapture);
Check(realReport.Nodes.Count==2 && realReport.Nodes.All(n=>n.Contacts.All(c=>c.Matches)),"Real-mesh serialized capture replays both breast solvers exactly");
Check(realCapture.Surfaces[0].SourceVertices.Length==vertices.Length && realCapture.RawRoots.Count==2,"Real-mesh capture retains full vertex and root data");
Console.WriteLine($"PASS full capture/replay: {vertices.Length} vertices, both breast results match; total checks={checks}. Capture={fp.Recorder.DirectoryPath}");
Human._list.Clear();
fp.Unload();

