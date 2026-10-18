using System.Globalization;

namespace BepInEx
{
    public class BepInPlugin:Attribute{public BepInPlugin(string id,string name,string version){}}
    public class BepInDependency:Attribute{public enum DependencyFlags{SoftDependency}public BepInDependency(string id,DependencyFlags flags){}}
    public class BepInProcess:Attribute{public BepInProcess(string name){}}
}
namespace BepInEx.Configuration
{
    public record ConfigDefinition(string Section,string Key);
    public class ConfigDescription{public string Description;public object Range;public object[] Tags;public ConfigDescription(string text,object range=null,params object[] tags){Description=text;Range=range;Tags=tags;}}
    public class AcceptableValueRange<T>{public T Min,Max;public AcceptableValueRange(T min,T max){Min=min;Max=max;}}
    public class ConfigEntryBase
    {
        public ConfigDefinition Definition;public ConfigDescription Description;public Type SettingType;
        public object BoxedValue,DefaultValue;
    }
    public class ConfigEntry<T>:ConfigEntryBase{public T Value{get=>(T)BoxedValue;set=>BoxedValue=value;}}
    public class SettingChangedEventArgs:EventArgs{public ConfigEntryBase ChangedSetting;}
    public class ConfigFile
    {
        public event EventHandler<SettingChangedEventArgs> SettingChanged;
        public void Raise(ConfigEntryBase entry)=>SettingChanged?.Invoke(this,new(){ChangedSetting=entry});
        public readonly Dictionary<ConfigDefinition,ConfigEntryBase> Entries=new();
        public readonly Dictionary<ConfigDefinition,object> InitialValues=new();
        public ConfigEntry<T> Bind<T>(string section,string key,T value,string text)=>Bind(section,key,value,new ConfigDescription(text));
        public ConfigEntry<T> Bind<T>(string section,string key,T value,ConfigDescription text)
        {
            var def=new ConfigDefinition(section,key);var entry=new ConfigEntry<T>{Definition=def,BoxedValue=InitialValues.GetValueOrDefault(def,value),DefaultValue=value,Description=text,SettingType=typeof(T)};
            Entries.Add(def,entry);return entry;
        }
    }
}
namespace BepInEx.Unity.IL2CPP
{
    public class TestLog
    {
        public readonly List<string> Messages=new();
        public void LogInfo(string text){Messages.Add(text);Console.WriteLine(text);}
        public void LogWarning(string text)=>throw new Exception(text);
        public void LogError(string text)=>throw new Exception(text);
    }
    public class BasePlugin
    {
        public readonly BepInEx.Configuration.ConfigFile Config=new();public readonly TestLog Log=new();
        public virtual void Load(){}public virtual bool Unload()=>true;
        protected T AddComponent<T>() => (T)Activator.CreateInstance(typeof(T),IntPtr.Zero);
    }
}
namespace HarmonyLib
{
    public class Harmony
    {
        public static readonly List<System.Reflection.MethodInfo> Patched=new();
        public Harmony(string id){}
        public void Patch(object method,HarmonyMethod prefix=null,HarmonyMethod postfix=null)
        {if(method is not System.Reflection.MethodInfo m)throw new Exception("Missing hook target");Patched.Add(m);}
        public void UnpatchSelf(){}
    }
    public class HarmonyMethod{public int priority;public HarmonyMethod(Type t,string name){}}
}
namespace MagicaCloth
{
    public class MagicaPhysicsManager{public bool IsDelay;public void AfterEarlyUpdate(){}public void AfterLateUpdate(){}public void PostLateUpdate(){}public void AfterRendering(){}}
}
namespace UnityEngine
{
    public class MonoBehaviour:Object{public MonoBehaviour(IntPtr p){}}
    public readonly record struct Rect(int X,int Y,int Width,int Height);
    public static class GUI{public static int Boxes;public static void Box(Rect rect,string text){Boxes++;}}
    public static class Application
    {
        public static int AuditSubscriptions;
        public static void add_onBeforeRender(Events.UnityAction action){AuditSubscriptions++;throw new NotSupportedException("Method unstripping failed");}
        public static void remove_onBeforeRender(Events.UnityAction action)=>throw new NotSupportedException("Method unstripping failed");
    }
}
namespace UnityEngine.Events
{
    public class UnityAction{public static implicit operator UnityAction(Action action)=>new();}
}


