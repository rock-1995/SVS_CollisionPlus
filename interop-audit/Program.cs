using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) throw new ArgumentException("Usage: InteropAudit <plugin.dll> <game directory>");
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
resolver.AddSearchDirectory(Path.Combine(args[1], "BepInEx", "SamabakeScramble", "interop"));
resolver.AddSearchDirectory(Path.Combine(args[1], "BepInEx", "core"));
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(Types(t.NestedTypes)));
var roots = Types(assembly.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
var visited = new HashSet<string>();
var failures = new SortedSet<string>();
var unresolved = new SortedSet<string>();
bool IsInterop(string name) => name.StartsWith("UnityEngine") || name.StartsWith("Il2Cpp") || name == "Assembly-CSharp" || name == "MagicaCloth";
void Visit(MethodDefinition method, string path)
{
    string key = method.Module.Name + ":" + method.FullName;
    if (!visited.Add(key) || !method.HasBody) return;
    if (method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains("unstripping failed")))
        failures.Add(path + " -> " + method.FullName);
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is not MethodReference reference) continue;
        var scope = reference.DeclaringType.Scope.Name.Replace(".dll", "");
        if (!IsInterop(scope)) continue;
        try
        {
            var target = reference.Resolve();
            if (target == null) unresolved.Add(reference.FullName);
            else Visit(target, path + " -> " + method.FullName);
        }
        catch (Exception e) { unresolved.Add(reference.FullName + " (" + e.Message + ")"); }
    }
}
foreach (var method in roots) Visit(method, "plugin");
// Harmony targets are selected via reflection, so no call instruction points
// to them. Check these entry points explicitly as well.
using var magica = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "BepInEx", "SamabakeScramble", "interop", "MagicaCloth.dll"), new ReaderParameters { AssemblyResolver = resolver });
var manager = magica.MainModule.Types.Single(t => t.FullName == "MagicaCloth.MagicaPhysicsManager");
foreach (string name in new[] { "AfterEarlyUpdate", "AfterLateUpdate", "PostLateUpdate" })
{
    var method = manager.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Count == 0);
    if (method == null) unresolved.Add("Harmony target " + name);
    else Visit(method, "Harmony target");
}
using var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "BepInEx", "SamabakeScramble", "interop", "Assembly-CSharp.dll"), new ReaderParameters { AssemblyResolver = resolver });
var humanLateUpdate = game.MainModule.Types.Single(t => t.FullName == "Character.Human").Methods.SingleOrDefault(m => m.Name == "LateUpdate" && m.Parameters.Count == 0);
if (humanLateUpdate == null) unresolved.Add("Harmony observation target Human.LateUpdate");
else Visit(humanLateUpdate, "Harmony observation target");
foreach (var failure in failures) Console.WriteLine("UNSUPPORTED: " + failure);
foreach (var missing in unresolved) Console.WriteLine("UNRESOLVED: " + missing);
Console.WriteLine($"{(failures.Count == 0 && unresolved.Count == 0 ? "PASS" : "FAIL")} actual SVS interop audit: {roots.Length} plugin methods; {visited.Count} visited methods; {failures.Count} unsupported paths; {unresolved.Count} unresolved calls.");
Environment.ExitCode = failures.Count == 0 && unresolved.Count == 0 ? 0 : 1;
