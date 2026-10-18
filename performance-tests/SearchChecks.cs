using System.Numerics;
using SVSCollisionPlus;
using Old=Baseline013;

internal static class SearchChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var random=new Random(80314);
        float R(float size=1)=>(float)(random.NextDouble()*2-1)*size;
        Vector3 Point(float size=1)=>new(R(size),R(size),R(size));
        for(int model=0;model<16;model++)
        {
            var vertices=new Vector3[192];var triangles=Enumerable.Range(0,vertices.Length).ToArray();
            var regions=new Region[64];var excluded=new bool[64];
            for(int t=0;t<64;t++)
            {
                var p=Point(2);vertices[t*3]=p;vertices[t*3+1]=p+Point(.4f);vertices[t*3+2]=p+Point(.4f);
                regions[t]=(Region)(t%3);excluded[t]=t%5==0;
            }
            // Exact ties with opposite normals across many leaves. Keep the
            // original traversal priority when near-first visits a later hit.
            for(int t=32;t<64;t++)
            {vertices[t*3]=vertices[(t-32)*3];vertices[t*3+1]=vertices[(t-32)*3+2];vertices[t*3+2]=vertices[(t-32)*3+1];regions[t]=regions[t-32];}
            var current=new SurfaceTree(vertices,triangles,regions);
            var baseline=new Old.SurfaceTree(vertices,triangles,regions.Select(r=>(Old.Region)r).ToArray());
            for(int pose=0;pose<3;pose++)
            {
                if(pose>0)
                {
                    var rotation=Matrix4x4.CreateFromYawPitchRoll(1.1f,.8f,-.3f);
                    for(int v=0;v<vertices.Length;v++)vertices[v]=Vector3.Transform(vertices[v],rotation)+new Vector3(0,v%9*.1f,0);
                    for(int t=0;t<regions.Length;t++){regions[t]=(Region)((t+pose)%3);baseline.Regions[t]=(Old.Region)regions[t];}
                    current.Refit();baseline.Refit();
                }
                for(int q=0;q<128;q++)for(int toggle=0;toggle<8;toggle++)
                {
                    var p=q<64?vertices[q*3]+Vector3.UnitY*.3f:Point(3);
                    float range=q%8==0?0:q%8==1?float.PositiveInfinity:q%8==2?.3f:2;
                    var source=(Region)(q%3);var omit=q%2==0?excluded:null;
                    var switches=new Switches((toggle&1)!=0,(toggle&2)!=0,(toggle&4)!=0);
                    var oldSwitches=new Old.Switches(switches.BreastAbdomen,switches.BreastBody,switches.BreastGround);
                    bool a=current.Nearest(p,range,omit,switches,source,out var aq,out var an,out var ak);
                    bool b=baseline.Nearest(p,range,omit,oldSwitches,(Old.Region)source,out var bq,out var bn,out var bk);
                    check(a==b && aq==bq && an==bn && (int)ak==(int)bk,$"search regression model={model} pose={pose} query={q} mask={toggle}");
                }
            }
        }
        var empty=new SurfaceTree(Array.Empty<Vector3>(),Array.Empty<int>(),Array.Empty<Region>());
        check(!empty.Nearest(Vector3.Zero,1,null,new(true,true,true),Region.Breast,out _,out _,out _),"empty surface");
        var boundary=new SurfaceTree(new[]{new Vector3(-1,0,-1),new Vector3(0,0,1),new Vector3(1,0,-1)},new[]{0,1,2},new[]{Region.Abdomen});
        check(!boundary.Nearest(Vector3.UnitY,1,null,new(true,false,false),Region.Breast,out _,out _,out _),"strict search radius boundary");
    }
}
