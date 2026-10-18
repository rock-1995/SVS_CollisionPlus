using System.Text.Json;
using SVSCollisionPlus;
if(args.Length==0)throw new ArgumentException("Usage: Replay <capture.json.gz or session directory> [report.json]");
var files=Directory.Exists(args[0])?Directory.GetFiles(args[0],"*.json.gz").OrderBy(x=>x).ToArray():new[]{args[0]};
var reports=files.Select(path=>new{File=Path.GetFileName(path),Report=CaptureReplay.Analyze(CaptureCodec.Read(path))}).ToArray();
var options=new JsonSerializerOptions(CaptureCodec.Options){WriteIndented=true};
string output=args.Length>1?args[1]:Path.Combine(Directory.Exists(args[0])?Path.GetFullPath(args[0]):Path.GetDirectoryName(Path.GetFullPath(args[0]))!,"collision-replay-report.json");
File.WriteAllText(output,JsonSerializer.Serialize(reports,options));
foreach(var r in reports)
{
    Console.WriteLine($"{r.File}: frame={r.Report.Frame}, complete={r.Report.Complete}, nodes={r.Report.Nodes.Count}");
    foreach(var n in r.Report.Nodes)Console.WriteLine($"  {n.Name}#{n.Id}: probes={n.Probes}, active={n.Active}, rootShift={n.RootShift:R}, skinShift={n.MaximumSkinShift:R}, observed={n.Observed}, kept={n.Kept}, replayMatches={n.Contacts.All(c=>c.Matches)}");
    foreach(var finding in r.Report.Findings)Console.WriteLine("  "+finding);
}
Console.WriteLine("Saved "+Path.GetFullPath(output));
