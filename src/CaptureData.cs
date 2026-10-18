using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVSCollisionPlus;

// Pure managed DTOs: no Unity objects may reach the background writer or replay.
internal sealed class CaptureFrame
{
    public int Schema=1,Frame;
    public string Version=PluginVersion,Reason="",Stage="",Error="";
    public const string PluginVersion="0.1.4";
    public DateTime Utc=DateTime.UtcNow;
    public float Time,Delta;
    public Switches Switches;
    public bool Enabled,Complete,ObservedBeforeRestore;
    public List<string> Timeline=new(),Inventory=new(),Errors=new();
    public List<SurfaceCapture> Surfaces=new();
    public List<NodeCapture> Nodes=new();
    public List<GroundCapture> Ground=new();
    public List<RootCapture> RawRoots=new();
}
internal sealed class SurfaceCapture
{
    public int Actor,RendererId,MeshId;
    public string Name="";
    public bool Valid,Enabled;
    public Vector3[] World=Array.Empty<Vector3>(),WorldNormals=Array.Empty<Vector3>(),SourceVertices=Array.Empty<Vector3>(),AfterWorld=Array.Empty<Vector3>();
    public int[] Triangles=Array.Empty<int>(),BoneIndices=Array.Empty<int>();
    public float[] Weights=Array.Empty<float>();
    public float[] BlendShapeWeights=Array.Empty<float>();
    public Region[] Regions=Array.Empty<Region>();
    public Matrix4x4 RendererMatrix;
    public Matrix4x4[] Bindposes=Array.Empty<Matrix4x4>();
    public List<BoneCapture> Bones=new();
    public string RawError="",AfterError="";
}
internal sealed class BoneCapture
{
    public int Id,ParentId;
    public string Name="";
    public Matrix4x4 WorldMatrix;
    public int[] AncestorIds=Array.Empty<int>();
    public string[] AncestorNames=Array.Empty<string>();
}
internal readonly record struct RootCapture(int Actor,int Id,string Name,Matrix4x4 WorldMatrix,string ClothState);
internal sealed class NodeCapture
{
    public int Actor,Id;
    public string Name="",ClothState="";
    public bool Active,Applied,Observed;
    public Vector3 BeforeLocal,BeforeWorld,PreviousOffset,AfterLocal,AfterWorld,Offset,ObservedLocal,RestoredBase;
    public Matrix4x4 ParentMatrix;
    public ContactProbe[] Probes=Array.Empty<ContactProbe>();
    public ContactSettings[] Settings=Array.Empty<ContactSettings>();
    public ContactResult[] Results=Array.Empty<ContactResult>();
    public List<SampleCapture> Samples=new();
    public List<TargetCapture> Targets=new();
}
internal readonly record struct SampleCapture(int Surface,int Vertex,float Weight);
internal sealed class TargetCapture
{
    public int Surface,Group;
    public bool[] Excluded;
}
internal readonly record struct GroundCapture(Vector3 Position,float Capture,GroundContact? Contact);
internal static class CaptureCodec
{
    internal static readonly JsonSerializerOptions Options=new(){IncludeFields=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
    internal static void Write(string path,CaptureFrame frame)
    {
        using var file=File.Create(path);
        using var gzip=new GZipStream(file,CompressionLevel.Fastest);
        JsonSerializer.Serialize(gzip,frame,Options);
    }
    internal static CaptureFrame Read(string path)
    {
        using var file=File.OpenRead(path);
        using var gzip=new GZipStream(file,CompressionMode.Decompress);
        var frame=JsonSerializer.Deserialize<CaptureFrame>(gzip,Options)??throw new InvalidDataException("Empty capture");
        if(frame.Schema!=1)throw new InvalidDataException("Unsupported capture schema");
        return frame;
    }
}
