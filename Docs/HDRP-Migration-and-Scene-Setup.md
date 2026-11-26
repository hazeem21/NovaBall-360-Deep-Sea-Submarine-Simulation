# HDRP Migration and Sea-Creature Scene Setup

**Project:** NovaBall Submarine Simulation (Unity 6, `D:\Projects\Shark Sim\Shark Sim`)
**Date:** 26 Nov 2026
**Scene:** `Assets/Scenes/Nova Controller Test.unity`

This guide records how the project moved from URP to HDRP, how the teammate's
"The Meg Environment" ocean was brought in, and how the sea creatures were placed by depth.
Each part says what was done, why, and how to repeat it by hand in the Unity Editor.

---

## 0. Before you start: make a safety copy

A render-pipeline switch touches many settings files, so take a copy first.

1. Close nothing. Just copy these folders somewhere outside the Unity project:
   `Packages`, `ProjectSettings`, `Assets/Scenes`, `Assets/Settings`, `Assets/Materials`, `Assets/Scripts`.
2. Today's copy is in `D:\Projects\Shark Sim\_Backup_before_HDRP_2026-11-26`.
   Git (GitHub Desktop) is the other safety net: commit before big changes.

---

## 1. Install the packages

Open **Window > Package Manager > Unity Registry** and install:

| Package | Version | Why |
|---|---|---|
| High Definition RP | 17.3.0 | Must match the URP version already in the project (17.3.0) |
| XR Interaction Toolkit | 3.3.2 | The ocean package contains its XR samples and needs this version |
| OpenXR Plugin | 1.19.0 | VR headset support (the package ships OpenXR settings) |

Shader Graph and Visual Effect Graph come in automatically with HDRP.

> URP is still installed but **not used**. It is kept because a few older assets
> (`Assets/Settings/*RPAsset*`, the tutorial Readme, and the URP "additional data"
> components already on the cameras and lights) refer to it. Removing it now would
> only turn those into "missing script" warnings. It can be removed later in
> Package Manager once nobody needs the old URP scene.

---

## 2. Import "The Meg Environment" without losing newer work

The package was built in HDRP by a teammate. Six of its files would have
**overwritten newer versions** in our project:

- `Assets/Scenes/Nova Controller Test.unity` (our scene has the newer submarine)
- `Assets/Prefabs/Orientation Cube.prefab` (the submarine prefab)
- `Assets/Materials/Red.mat`, `Blue.mat`, `Green.mat`
- `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset`

**How to repeat by hand:** Assets > Import Package > Custom Package..., choose the
`.unitypackage`, and in the import window **untick those six files** before clicking Import.

(What was actually done: Unity's silent importer kept finishing without copying anything,
so the files were extracted straight from the package (a .unitypackage is a gzipped tar)
into the same paths, skipping those six. The result is identical to an import with them unticked.)

What arrived:
- `Assets/DeepOcean/` - seabed terrain (8 tiles), terrain materials, the teammate's
  `DeepOcean_Main` scene, scripts (`OceanZoneManager`, `OceanAudioManager`, `OceanScaleSettings`,
  `SubmarineController`, `VRCockpitCamera`, the Seabed Generator window)
- `Assets/Sounds/` - waves, underwater, sonar, radar, creature sounds
- `Assets/Settings/HDRP_DeepOcean.asset` - the teammate's tested HDRP settings
- `Assets/HDRPDefaultResources/` - HDRP global settings and default volume profile
- `Assets/XR`, `Assets/XRI`, `Assets/Samples` - OpenXR and XR Interaction Toolkit settings and samples
- `Assets/Models/Submarine.glb` - the teammate's own submarine model (not used in our scene)

---

## 3. Switch the project to HDRP

1. **Edit > Project Settings > Graphics > Default Render Pipeline**: set to `HDRP_DeepOcean`.
2. **Edit > Project Settings > Quality**: for *every* quality level (Mobile, PC) set
   **Render Pipeline Asset** to `HDRP_DeepOcean`. (If a level still points at a URP asset,
   that level renders with URP.)
3. **Graphics > HDRP Global Settings**: `Assets/HDRPDefaultResources/HDRenderPipelineGlobalSettings`.
4. **Window > Rendering > HDRP Wizard**: run the checks and press **Fix** on anything red.
   Results today: colour space Linear OK, shadows OK, SRP batcher OK, volume profile OK,
   lightmap encoding needed a fix (fixed).
5. **Project Settings > Player > XR Settings**: Stereo Rendering Mode = **Single Pass Instanced**
   (much cheaper in VR; HDRP expects it).
6. **Project Settings > Player > Active Input Handling** is already **Both**, which the
   teammate's keyboard scripts (old Input Manager) and the XR Interaction Toolkit (new Input
   System) both need. The yellow "Input Manager is marked for deprecation" message is only a notice.

Not changed on purpose: ray tracing stays off (too heavy for VR on an RTX 2080).

---

## 4. Fix anything that turns pink

URP shaders do not render in HDRP (they show pink). What was checked and fixed:

| Item | Problem | Fix |
|---|---|---|
| All `.glb` creature and sub models | none | glTFast's shader graphs already contain HDRP passes, so they render as-is |
| `Red/Blue/Green.mat`, `Prefabs/New Material.mat` | URP/Lit | Switched to **HDRP/Lit**, colour and smoothness kept. `New Material` is the sub's **Panoramic_Glass**, so it was set to Surface Type **Transparent** |
| `Materials/SonarPulseShader.shader` | hand-written URP shader | Rewritten for HDRP (same name for the properties `_PulseColor`, `_RimPower`, `_Alpha`, so the sonar script still drives it). Now called `Custom/SonarPulse` |
| `Scripts/NovaLoco/SubmarineLightAndSonar.cs` | `using UnityEngine.Rendering.Universal` | Changed to `UnityEngine.Rendering.HighDefinition`. HDRP has its own `ColorAdjustments` with the same fields, so the night-vision tint works unchanged |
| Sub's `Global Volume` profile | held a URP Color Adjustments override | Replaced with the HDRP Color Adjustments override; volume priority set to 5 so the sonar tint wins |
| `Scripts/JellyfishController.cs` | glow used URP's `_EmissionColor` | Also looks for HDRP's `_EmissiveColor` now |
| Sub `Camera` / `Main Camera` | no HDRP data | Added **HD Additional Camera Data** |

How to find pink materials yourself: in the Project window search `t:Material`, select all, and
look at the Shader field in the Inspector. Anything starting with "Universal Render Pipeline/"
needs switching to an HDRP shader (Edit > Rendering > Materials > Convert... can help).

**Known HDRP gap in the teammate's sub script:** `SubmarineLightAndSonar` sets
`RenderSettings.fog`. HDRP ignores that (fog lives in a Volume's *Fog* override), so the
"sonar fog" colour change does nothing in HDRP. The underwater look is handled by the
teammate's `Ocean Zone Manager` instead. Worth porting later if the sonar mode needs its own fog.

---

## 5. Bring the ocean into Nova Controller Test

From `DeepOcean_Main` these objects were moved into our scene under
**"Ocean Environment (from The Meg Environment package)"**:
Volume Profile (sky, exposure, fog), Lighting (Sun, reflection probe, probe volume),
StaticLightingSky, Ocean_Surface (HDRP Water), Ocean_Seabed (terrain), Underwater Volume,
Ocean Zone Manager, Ocean Audio Manager.

**Not** moved: the package's own `Submarine_Rig` + `XR Origin` (we keep the teammate's
NovaBall submarine, the `Orientation Cube`), the disabled test geometry, and the baked probe data.

Then:
- `Ocean Zone Manager > Depth Source` and `Ocean Audio Manager > Depth Source` now point at
  **Orientation Cube**, so the light, colour and sound change with *our* sub's depth.
- The old URP `Directional Light` under the sub was **disabled**; the package's `Sun` replaces it.

How to repeat by hand: open both scenes (drag `DeepOcean_Main` into the Hierarchy to load it
additively), drag the objects across into Nova Controller Test, re-assign Depth Source, then
right-click `DeepOcean_Main` > Remove Scene (do **not** save it).

---

## 6. Depth scale: real depths (1 unit = 1 m)

The teammate's seabed is **true scale**: the abyssal plain is at about **y = -5,900**, and the
trench drops to about **y = -10,980** around z = 22,000-23,000. But `OceanScaleSettings`
said **1 unit = 20 m**, which made the water look "midnight-black" by y = -50 while the
seabed was 5.9 km lower. `metresPerUnit` was set back to **1** (the generator's own
"Mariana Trench scale"), so the light, the zone names and the depth read-out now match the
terrain. zem confirmed: keep real depths for now.

If you ever want a compressed dive instead: set `metresPerUnit` (e.g. 5 or 20) in
`Assets/DeepOcean/Settings/OceanScaleSettings`, then **regenerate the seabed** with
**Tools > Deep Ocean > Seabed Generator** so the terrain matches. Do not change one without the other.

---

## 7. Sea creatures by depth zone

Everything lives under **"Sea Creatures (by depth zone)"**. The journey runs along +Z.

| Zone (real depth) | Group centre (x, y, z) | Creatures |
|---|---|---|
| 1 Sunlight 0-200 m | (0, -40, 300) | Dolphin, Orca, Whale (~14 m), Manta Ray, Loggerhead Turtle, Hawksbill Turtle (animated + static), Swordfish, Emperor Angelfish, Discus, Low-poly Fish Pack, Sardine School (12) |
| 2 Twilight 200-1000 m | (0, -500, 4800) | Squid, Bobtail Squid x4, Pacific Viperfish x5, Luminous Drift Jellyfish |
| 3 Midnight 1000-4000 m | (0, -2000, 9000) | Octopus |
| 4 Abyssal floor 4000-6000 m | (0, -5900, 16500) | Giant Isopod, Warty Sea Cucumber, Brittle Star x4 (on the seabed), Anglerfish (Abyssal Lantern Terror) just above it |
| 5 Trench rim | (0, -6247, 18500) | Megalodon (18 m), patrolling 150 m around |

Left **out** of the scene on purpose:
- **Pliosaur** and **Mosasaur** (zem undecided).
- **brittle_stars_paleocoma_egertoni**: it is a *fossil slab* (a rock with fossil stars), not a living animal, and 343k vertices.

### How each creature is built
- **New models** use `Assets/Scripts/ZoneRoamer.cs`. An empty parent object does the
  swimming (it always swims along its own +Z) and the model is a child that carries the size
  and the facing fix. `ZoneRoamer` loops the model's own built-in animation (if it has one)
  and wanders inside a cylinder: `zoneCenter` + `zoneRadius` sideways, `minY`..`maxY` in depth.
- **Sardine School**: the parent roams; each sardine only plays its swim clip.
- **Sizes** were set to real-world lengths (e.g. dolphin 2.5 m, orca 6.5 m, swordfish 3 m,
  sardine 0.2 m, viperfish 0.3 m, bobtail squid 0.1 m, isopod 0.4 m).
- **Facing**: every model was lined up and screenshotted from the side, and turned so its
  head points along +Z (the swim direction).
- **The six animated creatures from before** (Manta, Whale, Jellyfish, Octopus, Anglerfish,
  Megalodon) were rebuilt with their own controllers and the same settings as in SampleScene
  (whale fluke amplitude 0.16, octopus thrust 1.4, etc.). Their "wander radius" was widened
  for open water. The Megalodon cruise speed was lowered to about 3.7 m/s.
  Note: SampleScene no longer contains these creatures (it was overwritten by a pull before
  they were committed), so Nova Controller Test is now the scene with all creatures.

### Testing helper: jump between zones
`Assets/Scripts/ZoneTeleport.cs` is on the sub. In Play mode press **1-5** to jump to each
zone's "Spawn (test jump point)", **0** to return to the start. Untick it for real sessions.

---

## 8. Reducing the very heavy models

The VR target (RTX 2080) cannot afford half-million-vertex rocks on the seabed.
`Assets/Scripts/Editor/SimpleMeshDecimator.cs` adds **Tools > Shark Sim > Reduce Selected Mesh**.
It merges nearby vertices (vertex clustering) into a new mesh saved in
`Assets/Creatures/_Reduced/` and swaps it onto the scene object. **The original .glb is never changed.**

| Model | Before | After |
|---|---|---|
| Warty Sea Cucumber | 593k verts | 27k |
| Giant Isopod | 483k | 25k |
| Brittle Star (ophiopinna) | 68k | 13k (one mesh shared by all 4) |
| Hawksbill Turtle (static) | 147k | 21k |

`Assets/Creatures/_Reduced/_unused (safe to delete)` holds first-pass meshes that are no longer used.

Still heavy (not reducible with this tool because they are animated/skinned):
**Dolphin** (130k, and its material uses the expensive *StackLit* shader) and the Megalodon (182k).
Only one zone is ever in view at a time (they are kilometres apart), which helps.

---

## 9. Checks done

- Console: **0 errors** after compiling, after building the scene, and during Play mode.
- Play mode: all swimmers move; built-in clips play (bone motion measured on dolphin, orca,
  sardine, turtles, bobtail squid, viperfish, angelfish); floor creatures stay put.
- Render: surface, sky, water and sub render correctly; no pink materials.

## 10. Open issues to look at in the headset

1. **Deep zones may be pitch black.** The teammate's exposure (`Volume Profile > Exposure >
   Limit Min = 9`) suits sunlight but not 5,900 m, where only the sub's lights shine. In tests
   at the abyssal floor the cockpit was black at 9 and visible at 5. Try lowering *Limit Min*
   (e.g. 5) in `Assets/DeepOcean/Scenes/DeepOcean_Main/OutdoorsSceneProfile`, or have the
   Ocean Zone Manager lower it with depth. Left at the teammate's value for now.
2. **Sub spotlights have a range of 100,000 m.** Every creature and terrain tile in range pays
   for those lights. A range of 50-150 m would be far cheaper in VR.
3. **Caustics show on the seabed at 5.9 km** (sunlight caustics cannot reach that deep).
4. **Discus, goldfish and bass** (the "fish" and "Low-poly Fish Pack" models) are freshwater
   fish. They are in the sunlight zone because they were in that folder. Delete them if realism matters.
5. Two OpenXR settings files exist (`Assets/XR/Settings/OpenXR Package Settings.asset` is the
   active one, `OpenXRPackageSettings.asset` came from the package). Pick one profile set for
   the headset and delete the other.
6. Temporary folders that can be deleted when happy: `D:\Projects\Shark Sim\_Import_temp`,
   the project's `Captures` folder (screenshots), and the backup folder once committed.

## Plan vs SDK

No new differences came up in this step. The NOVA SDK still only sends the sub's rotation to
the ball (game drives the ball). The package's own `Submarine_Rig` uses a joystick/XR rig too,
which is consistent with that.

## Headlight fix (2026-11-26)

Symptom: the left and right headlights did not light anything.

There were two causes:

1. **The sub lights still had the model's raw import values.**
   - When `NOVA_ABYSS_Submarine.glb` is imported, its lights come in at 712 lumen, a 60° cone and a 40 m range. The aux lights had a 100 km range.
   - The friend's DeepOcean_Main `Submarine_Rig` uses tuned values, and their `SubmarineController` multiplies the headlights by `headlightBrightness` (10) at Start.
   - Our sub does not use that controller, so I copied the final values by light name:
     - Headlights: 2,445,814 cd, cone 42/23.1°, range 250.
     - Aux spots: the friend's values, range 80.
     - Interior lights: range 4.
   - `SubmarineLightAndSonar` used to set interior lights to 1.0 / 0.25 (URP units, close to invisible in HDRP). It now scales each light's own intensity instead.
2. **HDRP underwater fog blacks out local lights at real depth.**
   - HDRP darkens every pixel by `exp(-(view distance + depth below surface) × extinction)` (AtmosphericScattering.hlsl). In other words, it assumes all light comes from the sun.
   - At real depths, with 1 unit = 1 m, the visibility values gave optical depths of 10 or more at 500 m, so even a 2.4M cd headlight came out black.
   - The friend's package hides this, because it is only tested near the surface.
   - Fix: `OceanZoneManager` has a new "Sub lights at real depth" section. With `keepLocalLightsVisible` (on by default), the water's Absorption Distance Multiplier is raised with depth so that the depth term never exceeds `maxDepthDarkening` (2).
   - Darkness at depth still comes from the sun fading to 0 and the dim sky.
   - Trade-off: the distance haze is weaker in the deep zones.

Screenshots: `Docs/Screenshots/headlights_500m_fixed.png` and `headlights_6240m_fixed.png`.

## Visible headlight beams (2026-11-26, follow-up)

Even with the lights correct, you saw nothing in open water. A spotlight only shows where it hits something, and HDRP's underwater fog does not draw light shafts. I tested volumetric fog and it made the scene darker without showing any beams.

What I added:
- **`Assets/Scripts/HeadlightBeams.cs`**, on Orientation Cube.
  - It builds a closed cone, 30 m long, in front of every spot light whose name contains "Headlight", plus a small glow ball on each lamp.
  - The beams follow the real lights, so F (SubmarineLightAndSonar) switches them on and off.
- **`Assets/Materials/HeadlightBeamVolumeShader.shader`** ("Custom/HeadlightBeamVolume") with the material `HeadlightBeam.mat`.
  - It ray-marches through the cone (20 steps), with a soft radial edge and a fade along the length.
  - It stops at scene depth, so the beam ends on the seabed or a creature and is hidden behind the sub.
  - Brightness is multiplied by the camera exposure. It works in VR single-pass instanced.
- **`Assets/Materials/HeadlightBeamShader.shader`** ("Custom/HeadlightBeam") with `HeadlightGlow.mat`, for the lamp glow.

Tuning:
- Brightness: `_Intensity` on HeadlightBeam.mat (500).
- Beam length and width: `beamLength` and `widthOfCone` on HeadlightBeams.

Screenshots: `Docs/Screenshots/beams_side_500.png` and `beams_cockpit_500.png`.

## Realistic lights, world-space sonar ping, sonar display (2026-11-26)

### Headlights
- `HeadlightBeamVolumeShader` now multiplies the beam by 3D noise sampled in **absolute world space**. The murk stays still in the water and the sub moves through it, so the beam no longer looks painted on the screen.
  - Settings: `_NoiseScale`, `_NoiseStrength`, `_NoiseDrift`.
  - Beam intensity is lowered to 120.
- **`Assets/Scripts/MarineSnow.cs`** (on Orientation Cube) adds a world-space particle system of tiny sinking specks that use `MarineSnow.mat` (HDRP/Lit). They only show where light hits them, which gives the parallax a real dive has.

### Sonar ping in the world
- **`Assets/Scripts/Sonar/SonarScanEffect.cs`** with `FullScreen/SonarScan` and `Materials/Sonar/SonarScan.mat`.
  - It creates an HDRP full-screen Custom Pass (Before Post Process, so it works in VR).
  - A green shell expands from the sub at 60 m/s. Where it meets terrain or creatures it lights them up, then leaves a fading echo with 5 m contour lines.
- **Changes to `SubmarineLightAndSonar.cs`** (the teammate's script):
  - New `OnPing` event that the new effect listens to.
  - `tintWholeScreen` (default off) replaces the old full-screen green tint.
  - `showWaveSphere` (default off) replaces the old sphere around the camera.
  - The scene's `maxSonarRange` is raised from 45 to 150 m.
- Controls are unchanged: F turns the lights off, which starts sonar mode with a ping every 3.5 s. Tab pings straight away while in sonar mode.

### Sonar display (left cockpit panel)
- **`Assets/Scripts/Sonar/SonarDisplay.cs`** with `Custom/SonarDisplay` and `Materials/Sonar/SonarDisplay.mat`.
  - It builds a world-space panel at Start, placed relative to `Pilot_Eye_Position` at offset (-0.40, -0.22, 0.62). The panel must be further away than the cockpit camera's 0.4 m near plane.
  - It works like a mechanically scanned sonar. The sweep turns once every 4 s, and each bearing raycasts at 0/10/25/45° down to find terrain. Creatures under "Sea Creatures (by depth zone)" are checked by position, because they have no colliders.
  - Echoes are painted into a 360×128 texture, with bearing across and range up to 150 m. They fade until the sweep paints them again.
  - The display is heading-up: 0 = ahead, 90 = right.
  - It runs all the time, with lights on or off.
- **Plan difference:** the plan (Phase 3) describes a **passive** hydrophone or waveform monitor that "replaces artificial HUD minimaps". What was built (the teammate's script plus this) is **active** sonar with a map-style display.

Screenshots are in `Docs/Screenshots/`:
- `sonar_cockpit_abyss.png`
- `sonar_panel_closeup.png`
- `sonar_ping_outside.png`
- `lights_cockpit_500_v2.png`

## Sonar v2: visible at every depth, echo delay, passive listening (2026-11-26)

- **Why the ping only showed in the abyssal zone:** it only lit up geometry. Zone 4 is the only zone with seabed close to the sub; in the other zones the sub is in open water, kilometres above the floor.
- **Open-water ping:**
  - `SonarScanEffect` now also creates "Sonar Specks": 14,000 world-space particles (`Custom/SonarSpeck`, `SonarSpeck.mat`).
  - The specks are invisible until the ping front passes, then flash green, so you see a sphere of sparkles sweeping outward.
  - On a big jump (zone teleport keys) the specks and the marine snow refill around the new position. That causes a short hitch, but only on teleport.
- **Same brightness at every depth:** the ping and the sonar screen are no longer scaled by camera exposure (`brightness` 1.5, display `_Nits` 2), so they read the same in the bright shallows and the abyss.
- **Echo delay (`SonarDisplay`):**
  - On a ping, all echoes are worked out at once (terrain raycasts every 2°, plus creatures).
  - Each echo is shown only after 2 × distance / speed, using the same 60 m/s as the wave in the water, so near things appear first.
  - A ping ring expands on the screen in step with the wave in the water. Echoes fade over about 5 s.
- **Passive listening:**
  - It is always on and silent. The sweep "listens", and creatures moving faster than 0.15 m/s, within 400 m, show as flickering bearing lines with no range. Louder means bigger, faster or closer.
  - Terrain and still creatures only appear after a ping.
- **Texture channels:** R = active echoes, G = passive lines.
- **Tab** now pings with the lights on too. The ping does NOT alert the Megalodon, as zem asked.

## Zone 5 sonar scan (2026-11-26)

- **Cause:** the zone 5 jump point ("Zone 5 - Trench rim (Megalodon)") hovered about 60 m above the trench rim, where the floor falls away ahead. The ping did paint the floor (visible when pitching down 35°), but that floor was hidden under the cockpit dashboard, so it looked like nothing was scanned. Zone 4's jump point is only 6 m above flat seabed, so the floor fills the view there.
- **Fix:** the zone 5 jump point moved from y -6247 to y -6297, about 10 m above the floor at the trench lip. The floor and the drop ahead now show the green front and contours like zone 4.
- No code change. The sonar works the same at every depth; it only paints surfaces within 150 m.
