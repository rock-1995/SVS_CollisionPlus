using Character;
using UnityEngine;
using SVSCollisionPlus;
using V=System.Numerics.Vector3;
using M=System.Numerics.Matrix4x4;

internal static class CaptureChecks
{
    internal static void Run(Human human,Transform breast,SkinnedMeshRenderer renderer,Action<bool,string> check)
    {
        var plugin=new Plugin();plugin.Load();plugin.CaptureEvidence.Value=true;plugin.Recorder.Dispose();
        string path=Path.Combine(AppContext.BaseDirectory,"capture-test-"+Guid.NewGuid().ToString("N"));
        plugin.Recorder=new DiagnosticRecorder(plugin,path);
        renderer.sharedMesh.bindposes=new[]{Matrix4x4.From(M.Identity),Matrix4x4.From(M.Identity)};
        Human._list.Add(human);Time.unscaledTime=100;Time.frameCount=1000;
        plugin.Runtime.Step();Time.frameCount++;plugin.Runtime.Restore();plugin.Recorder.Flush();
        string first=Directory.GetFiles(path,"capture-*.json.gz").Single();var captured=CaptureCodec.Read(first);
        check(captured.Complete && captured.ObservedBeforeRestore,"Capture includes solve and next restoration boundary");
        check(captured.Surfaces.Count==1 && captured.Surfaces[0].SourceVertices.Length==6 && captured.Surfaces[0].BoneIndices.Length==24,"Raw skinning data survives compressed capture roundtrip");
        var report=CaptureReplay.Analyze(captured);
        check(report.Nodes.Single().Contacts.All(c=>c.Matches),"Offline replay exactly reproduces recorded solver output");
        check(report.Nodes[0].Applied && report.Nodes[0].Kept && report.Nodes[0].MaximumSkinShift>0,"Capture verifies bone correction reached skinned vertices and survived until restore");
        check(report.Nodes[0].Contacts[0].Rejections.GetValueOrDefault("accepted")>0,"Replay identifies accepted belly contacts");
        // Mutate live objects after capture: serialized and pending data must
        // remain immutable and contain no Unity references.
        var original=captured.Surfaces[0].World[0];renderer.sharedMesh.vertices[0].y+=.01f;
        check(CaptureCodec.Read(first).Surfaces[0].World[0]==original,"Snapshot is detached from later mesh changes");renderer.sharedMesh.vertices[0].y-=.01f;
        Time.unscaledTime+=16;Time.frameCount++;plugin.Runtime.Step();breast.localPosition=new(0,.2f,0);
        Time.frameCount++;plugin.Runtime.Restore();plugin.Recorder.Flush();
        var overwritten=CaptureCodec.Read(Directory.GetFiles(path,"capture-*.json.gz").OrderBy(x=>x).Last());
        check(CaptureReplay.Analyze(overwritten).Findings.Any(x=>x.Contains("later update replaced")),"Replay identifies an external pose overwrite");breast.localPosition=new();
        // Zero samples, inactive cloth, disabled switches and missing hooks are
        // explicit outcomes, rather than reports silently claiming success.
        var zero=CaptureCodec.Read(first);zero.Nodes[0].Probes=Array.Empty<ContactProbe>();
        check(CaptureReplay.Analyze(zero).Findings.Any(x=>x.Contains("zero probes")),"Replay identifies zero probe binding");
        zero.Nodes[0].Active=false;check(CaptureReplay.Analyze(zero).Findings.Any(x=>x.Contains("inactive")),"Replay identifies inactive native physics");
        zero.Enabled=false;check(CaptureReplay.Analyze(zero).Findings.Any(x=>x.Contains("Master switch")),"Replay identifies disabled controls");
        renderer.ThrowOnBake=true;
        plugin.Recorder.Failure(new InvalidOperationException("injected mesh-read failure"));plugin.Recorder.Flush();renderer.ThrowOnBake=false;
        var failure=CaptureCodec.Read(Directory.GetFiles(path,"capture-*.json.gz").OrderBy(x=>x).Last());
        check(failure.Error.Contains("injected mesh-read failure") && failure.Inventory.Count>0 && failure.Surfaces.Count>0,"Exception capture preserves raw inventory when no solve is possible");
        check(failure.Surfaces[0].RawError.Contains("world mesh") && failure.Surfaces[0].SourceVertices.Length==6 && failure.Surfaces[0].Weights.Length==24 && failure.Surfaces[0].Bones.Count==2,"Failed native bake still saves independent raw vertices, weights and bone palette");
        for(int i=0;i<10;i++)
        {
            Time.unscaledTime+=21;Time.frameCount++;plugin.Recorder.Pulse(false);plugin.Recorder.Flush();
        }
        check(Directory.GetFiles(path,"capture-*.json.gz").Length==8,"Recording retains only eight rolling snapshots per session");
        check(File.Exists(Path.Combine(path,"first-bound.json.gz")) && File.Exists(Path.Combine(path,"pose-overwritten.json.gz")) && File.Exists(Path.Combine(path,"solver-error.json.gz")),"Key captures survive eviction from the rolling window");
        var heartbeat=CaptureCodec.Read(Directory.GetFiles(path,"capture-*.json.gz").OrderBy(x=>x).Last());
        check(!heartbeat.Complete && heartbeat.Inventory.Count>0 && heartbeat.Surfaces.Count>0,"Missing solver activity still captures raw scene state");
        plugin.Recorder.Dispose();string badPath=Path.Combine(path,"not-a-directory");File.WriteAllText(badPath,"file");
        plugin.Recorder=new DiagnosticRecorder(plugin,badPath);Time.unscaledTime+=21;Time.frameCount++;
        plugin.Runtime.Step();check(breast.localPosition.y>0,"Solver runs when evidence output path is unwritable");
        plugin.Runtime.Restore();plugin.Recorder.Flush();plugin.Recorder.Pulse(true);
        check(plugin.Log.Messages.Any(m=>m.Contains("file write failed")),"Capture I/O failure is reported without disabling collisions");
        plugin.Unload();Human._list.Clear();
        Console.WriteLine("CAPTURE TEST SESSION "+path);
        // Preserve a complete scene capture for the standalone replay CLI test.
        CaptureCodec.Write(Path.Combine(path,"verified-scene.json.gz"),captured);
    }
}
