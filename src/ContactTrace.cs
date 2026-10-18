using System.Numerics;

namespace SVSCollisionPlus;

// Enabled only during offline replay; the live solver keeps its original path.
internal sealed class ContactTrace
{
    public readonly Dictionary<string,int> Counts=new();
    public readonly List<string> Examples=new();
    internal void Mark(string reason,Vector3? point=null,float value=0)
    {
        int count=Counts.GetValueOrDefault(reason)+1;Counts[reason]=count;
        if(count<=3)Examples.Add($"{reason}: point={point}, value={value:R}");
    }
}
