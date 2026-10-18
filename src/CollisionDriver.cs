using UnityEngine;

namespace SVSCollisionPlus;

public sealed class CollisionDriver : MonoBehaviour
{
    public CollisionDriver(IntPtr pointer) : base(pointer) { }
    public void LateUpdate()=>Plugin.Instance?.Recorder?.Mark("Driver.LateUpdate");
    public void Update()
    {
        Plugin.Instance?.Recorder?.Mark("Driver.Update");
        // Also release the previous render pose if the native manager was paused
        // or disabled after our last pass. This runs on the Unity main thread.
        Plugin.Instance?.Runtime?.Restore();
        Plugin.Instance?.Runtime?.PollControls();
        Plugin.Instance?.Recorder?.Pulse(Plugin.Instance.Runtime.HasActors);
    }
    public void OnGUI()
    {
        var plugin=Plugin.Instance;
        if(plugin==null || !plugin.ShowStatusOverlay.Value || plugin.Runtime==null)return;
        GUI.Box(new Rect(12,12,740,72),plugin.Runtime.StatusText);
    }
}

