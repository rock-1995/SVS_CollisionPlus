using System.Numerics;

namespace SVSPregnancy;

internal static class TorsoLandmarks
{
    internal static Vector3 OrientUp(Vector3 up, Vector3 pelvis, Vector3 upperSpine)
    {
        // SVS cf_s_spine01 lies BELOW cf_s_waist01 in the imported rest mesh.
        // Keep AL's short-bone length, but orient its axis toward the upper torso.
        if (Vector3.Dot(up, upperSpine - pelvis) < 0) up = -up;
        if (Vector3.Dot(up, Vector3.UnitY) > .98f) up = Vector3.UnitY;
        return up;
    }

    // Verified in SVS body_00.unity3d: the 103-bone o_body palette uses
    // cf_j_kokan at index 98; AL instead uses cf_s_kokanskin for this landmark.
    internal static (Vector3 Floor, Vector3 Waist, Vector3 Chest) Read(IReadOnlyDictionary<string, Vector3> bones)
    {
        bool floor = bones.TryGetValue("cf_s_kokanskin", out var f) || bones.TryGetValue("cf_j_kokan", out f);
        bool waist = bones.TryGetValue("cf_s_waist01", out var w);
        bool chest = bones.TryGetValue("cf_s_spine03", out var c);
        if (!floor || !waist || !chest)
            throw new InvalidOperationException($"Missing rest landmarks: pelvis(cf_s_kokanskin/cf_j_kokan)={floor}, waist(cf_s_waist01)={waist}, upper spine(cf_s_spine03)={chest}.");
        if (!(float.IsFinite(f.Y) && float.IsFinite(w.Y) && float.IsFinite(c.Y) && f.Y < w.Y && w.Y < c.Y))
            throw new InvalidOperationException($"Invalid rest landmark heights: pelvis={f.Y}, waist={w.Y}, upper spine={c.Y}.");
        return (f, w, c);
    }
}
