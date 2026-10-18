# Development and validation

Soft breast contact for SamabakeScramble, derived from AL_CollisionPlus 0.1.11. Breast–abdomen contact is enabled by default. Breast–body and breast–ground contact can be enabled separately in BepInEx Configuration Manager (F1).

The SVS adapter reads the actual skinned body after pregnancy deformation. It supports both normal and delayed MagicaCloth update modes, applies a bounded and smoothed breast-root displacement after native bone write-back, and restores its owned displacement before the next update. It never edits the body mesh or pregnancy plugin. Diagnostics and the overlay are disabled by default.

## Build

Requires the .NET SDK and a local SVS installation containing BepInEx 6 IL2CPP and generated interop assemblies.

```powershell
dotnet build src/SVS_CollisionPlus.csproj -c Release '-p:GameDir=PATH_TO_GAME'
```

Install the resulting `SVS_CollisionPlus.dll` under `BepInEx/plugins/SVS_CollisionPlus`. Delete that DLL to remove the plugin. No game assets or dependency DLLs are redistributed here.

## Verification

```powershell
dotnet run --project tests/Tests.csproj -c Release
dotnet run --project runtime-tests/RuntimeTests.csproj -c Release
```

The first suite exercises the inherited contact solver. The second compiles the production adapter against test doubles and checks live mesh/weight/bind-pose changes, native pause, both scheduling modes, exclusion, toggles, and restoration. These are offline tests, not an in-game performance measurement.

For the optional real-asset regression, use Python with UnityPy to extract your own game's body mesh, then pass the fixture and pregnancy settings:

```powershell
python runtime-tests/ExtractSvsBodyFixture.py 'GAME/abdata/chara/body/body_00.unity3d' body-fixture.json
dotnet run --project runtime-tests/RuntimeTests.csproj -c Release -- body-fixture.json 'GAME/BepInEx/config/SVSPregnancy_belly.json' results.json
```

The pregnancy-reference test files are copied from SVS Pregnancy 0.6.6 source and are only used for offline deformation tests. They are not compiled into the plugin. The real-asset test covers one default SVS mesh and controlled breast lowering; it does not reproduce all game animations, character sliders, clothing or native simulation.

## Adapter details

- SVS `HumanMagicaSpringSetting.Type` contains only Bust and Hip. Outermost Bust roots are selected from the actual `MagicaBoneSpring.ClothTarget.rootList`, avoiding double translation of nested springs.
- Actual body renderers provide contact geometry. The installed pregnancy plugin excludes `o_hit_*` liquid meshes from deformation, so those meshes cannot represent the enlarged abdomen.
- Unity `BakeMesh` reads the current vertices, bone weights and bind-pose palette. Vertices and normals use reused native lists. Bone weights use the native array property because GetBoneWeights(List) is stripped in SVS. Native spans avoid per-vertex interop indexer calls. Weight changes refresh samples and self exclusions without requiring a new mesh object.
- Normal mode solves after `MagicaPhysicsManager.AfterLateUpdate`; delayed mode solves after `PostLateUpdate`. The native call sites were checked against the local GameAssembly.
- Abdomen triangle classification uses skin influence from waist, hips, lower/middle spine and belly bones. Only breast transforms are writable. There is no new belly or hair simulation.
- The full-body bake and BVH refit add per-frame cost. In-game visual and timing validation is still required.

Original solver: https://github.com/rock-1995/AL_CollisionPlus

## Runtime compatibility

Unity instance IDs are signed, including IDs for instantiated breast bones. Ownership uses nullable IDs, with null representing no owner; negative values, including -1, remain valid. Runtime tests reproduce the old zero-probe failure with negative IDs and cover both signs and boundary values.

The private scratch mesh is protected with DontUnloadUnusedAsset, recreated if destroyed, and explicitly destroyed on disposal. Source meshes are never given new flags or modified. Pose diagnostics run immediately before restoration, through the existing physics/driver update path, and report the previous correction as kept or externally overwritten. They do not claim to observe the exact render instant.

SVS's Mesh.GetBoneWeights(List) and Application.onBeforeRender registration are unavailable unstripping stubs. The adapter uses Mesh.boneWeights and does not register that event. A compatibility audit walks the plugin's interop calls, transitive managed wrappers, and the physics hooks and the read-only Human.LateUpdate observation hook:

```powershell
dotnet run --project interop-audit/InteropAudit.csproj -c Release -- src/bin/Release/net6.0/SVS_CollisionPlus.dll 'PATH_TO_GAME'
```

For another installation, supply -p:GameDir=... at build time, or -p:CecilPath=... to select Mono.Cecil.dll. This audit detects unavailable wrappers, not native execution failures. The optional geometry fixture retains a 0.6.6 pregnancy formula reference; native game behavior and visual results require a game run.
## Automatic diagnostic captures

CaptureEvidence is disabled by default for normal play. When explicitly enabled, every 15 seconds it captures the actual body geometry, raw mesh vertices, skin weights, bindposes, full bone ancestry, spring states, settings, solver results, post-correction skinned geometry and the bone pose observed before restoration. Native/driver timing markers are included. No experimental forces or counterfactual settings are applied in the game.

Compressed JSON is written in the background to diagnostics/<session>/ beside the installed DLL. Eight rolling snapshots are retained, plus at most five fixed key snapshots (first binding, greatest accepted penetration, zero probes, overwritten pose, and solver exception). Sessions have unique names. If solving never starts, a heartbeat records raw actor and mesh inventory; a failing bake does not discard independently readable skinning data. Write failures are logged without disabling collisions. CaptureEvidence can be turned off in Configuration Manager.

Replay does not need Unity or game assets:

```powershell
dotnet run --project replay/Replay.csproj -c Release -- 'GAME/BepInEx/plugins/SVS_CollisionPlus/diagnostics/SESSION' report.json
```

The report compares recorded and recomputed results, checks raw skinning against the baked mesh, lists contact rejection counts/examples, and evaluates search range, direction filtering, edge tolerance and compression variants offline. It distinguishes zero binding, inactive springs, disabled controls, no candidate surface, rejected contacts, absent mesh movement and later pose overwrites. World geometry and raw arrays remain in the capture for further analysis. Exact renderer visibility or all later native execution cannot be established by offline tests alone.

Validation includes compressed capture roundtrips, mutable-state isolation, blocked output paths, missing physics activity, failed native bakes, rolling retention, pinned evidence and a 10,597-vertex controlled SVS fixture whose two breast solvers replay identically. The fixture itself is not redistributed.
## Search performance (0.1.4)

Nearest-surface queries visit closer branches first and use bounds for the requested body region. Bounds and region masks update with each mesh refit, including changes to skin classification. Equal-distance hits retain the original triangle-order priority. Search radius, samples, normal filtering, contact planes, displacement limits, smoothing, physics timing and solver iterations are unchanged.

The performance harness includes the unmodified 0.1.3 algorithm in a separate namespace. On the same .NET 6 host it compares exact nearest points, normals, frozen contacts and solver outputs, then times both versions with the same captured geometry and settings. Two tree layouts are tested: construction at the captured pose, and construction at the first available pose followed by refitting. Synthetic checks cover all switches, equal-distance opposite normals, moving geometry, changed regions and strict range boundaries.

```powershell
dotnet run --project performance-tests/PerformanceTests.csproj -c Release -- 'LOCAL_CAPTURE_SESSION' performance.json
```

The supplied report contains timings and counts only; scene geometry is not redistributed. Nine distinct captured frames produced 432 identical solver comparisons and 44,648 identical nearest-surface comparisons. Mean breast-abdomen solve cost was 12.81 ms before and 1.66 ms after; refitting both body surfaces averaged 0.51 ms before and 0.58 ms after. These are offline measurements on recorded game geometry, not measurements of in-game FPS. Other body and ground contacts are outside this focused performance claim.

The old capture schema does not store the original BVH layout or its equal-distance triangle priority. Reconstructing it from a later pose can therefore differ from the recorded live solver, including when using the unchanged 0.1.3 code. The optimization comparison uses the same initial topology and geometry for both versions and asserts exact equality; it does not claim every reconstructed tree reproduces every recorded live output.

Upgrading an existing diagnostic installation preserves its saved configuration. Set CaptureEvidence=false to stop full mesh/ancestry capture during normal play. Diagnostics can stay enabled for lightweight periodic timing logs. Existing collision strengths and switches do not need changing.