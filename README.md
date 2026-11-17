# OpenVR Robotics Toolkit

Stream **HTC Vive Trackers and Lighthouse base stations** (via SteamVR / OpenVR) live into **Rhino 8 + Grasshopper** for robotics, digital fabrication, tool tracking, motion capture and workshop teaching.

```
 Vive Tracker ──► SteamVR ──► ViveTcpPublisher.exe ──TCP :7000──► Grasshopper (Vive tab)
                              (or dummy_sender.py for offline testing)
```

Everything you need to run it is in this repository. **You don't need to compile anything.**

| File | What it is |
| :--- | :--- |
| `ViveTcpRecorderV2.gha` | The Grasshopper plugin, ready to install |
| `Grasshopper/ViveTracking_Template.gh` | Template definition with the components already wired |
| `ViveTcpPublisher/publish-win-x64/ViveTcpPublisher.exe` | Standalone SteamVR → TCP streamer (doesn't need .NET installed) |
| `dummy_sender.py` | Simulator that streams fake tracker data, so you can test without hardware |

---

## Getting Started

### 0. Requirements

| Need | Live tracking | Offline simulator |
| :--- | :---: | :---: |
| Windows 10/11 (64-bit) | ✅ | ✅ |
| Rhino 8 | ✅ | ✅ |
| Steam + SteamVR | ✅ | — |
| Vive Tracker (2.0 / 3.0) + dongle, 1–4 base stations | ✅ | — |
| Python 3.8+ (standard library only) | — | ✅ |

### 1. Download the repository

Click **Code ▸ Download ZIP** on GitHub and extract it, or clone it:

```bash
git clone https://github.com/pitsieben/OpenVR-Robotics-Toolkit.git
```

> **ZIP download:** right-click the `.zip` ▸ **Properties** ▸ tick **Unblock** *before* you extract it. Otherwise Windows may block the `.gha` and `.exe`.

### 2. Install the Grasshopper plugin

1. Close Rhino.
2. Double-click **`copy_gha_to_grasshopper.bat`**. It copies `ViveTcpRecorderV2.gha` into `%APPDATA%\Grasshopper\Libraries\`.
   *(To do it by hand instead: in Grasshopper, open **File ▸ Special Folders ▸ Components Folder** and drop the `.gha` there.)*
3. Start Rhino and type `Grasshopper`. A new **Vive** tab appears in the ribbon.

No Vive tab? Right-click the `.gha` in the Libraries folder ▸ **Properties** ▸ **Unblock**, then restart Rhino.

### 3. Start a data stream

Choose **A** (real hardware) or **B** (no hardware).

#### A: Real Vive Trackers

1. **Pair the tracker (one time only):** start SteamVR and plug in the tracker dongle. Choose *SteamVR menu ▸ Devices ▸ Pair Controller*, then hold the tracker's power button until the LED blinks blue.
2. **Track without a headset (optional):** close SteamVR. Right-click **`enable_headless_trackers.ps1`** ▸ **Run with PowerShell**. This turns on SteamVR's *null* driver, so SteamVR no longer requires a headset. To undo it later, run `restore_steamvr_settings.ps1`.
   - If PowerShell won't run scripts, run this once: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`
3. Start SteamVR and switch on the base stations and the tracker. In SteamVR, the tracker icon must be **solid** (not blinking). The LED on the tracker must be **green**.
4. Run **`ViveTcpPublisher\publish-win-x64\Launch_ViveTcpPublisher.bat`**:
   - `[1]` lists the connected devices and their serial numbers.
   - `[2]` streams every device on `127.0.0.1:7000`.
   Leave this window open while you work.

To run it from the command line: `ViveTcpPublisher.exe [--serial SERIAL] [--host 127.0.0.1] [--port 7000] [--rate 90]`

#### B: Offline simulator (no hardware)

Double-click **`run_dummy_sender.bat`**. It simulates one moving tracker and four base stations on port 7000. For two trackers, use **`run_two_dummy_senders.bat`**.
You need `python` on your PATH: install it from python.org and tick *Add Python to PATH* during setup.

### 4. Open the template

1. In Grasshopper, choose **File ▸ Open** ▸ `Grasshopper/ViveTracking_Template.gh`.
2. Set the **AutoUpdate** toggle on the *Vive TCP Listener V2* to **True**.
3. The listener's `Msg` output lists the connected devices. The tracker and the lighthouses show up in the Rhino viewport and move live.

Before you change the template, save a copy under a new name.

### 5. Build your own definition from scratch

1. Place **Vive TCP Listener V2** (`Vive ▸ TCP`). Connect a **Boolean Toggle** to `AutoUpdate` and set it to `True`.
2. Place **Vive Tracker** (`Vive ▸ Tracking Device`). Wire the listener's `Vive` output to the tracker's `Vive` input. For more than one tracker, set `Index` to 0, 1, and so on.
3. Optional: add **Vive Lighthouse** components. Each new one numbers itself automatically (LH1 to LH4).
4. Use the tracker's `Plane` / `Matrix` outputs to drive geometry, robot targets and so on.

### Troubleshooting

| Problem | Fix |
| :--- | :--- |
| No **Vive** tab in Grasshopper | Windows has blocked the `.gha`. Unblock it (see step 2) and restart Rhino. |
| Listener says *not connected* | The publisher or simulator isn't running, or the port doesn't match. Check that `IP` is `127.0.0.1:7000`. |
| Publisher lists no trackers | SteamVR isn't running, the tracker isn't paired, or the base stations can't see it (icon blinking). |
| SteamVR asks for a headset | Close SteamVR and run `enable_headless_trackers.ps1`. |
| `run_dummy_sender.bat` closes straight away | Python isn't installed or isn't on PATH. |
| Port 7000 is already in use | Only one publisher or simulator can use a port. Close the other one, or pick another port with `--port`. |
| The copy script fails | Rhino is still running. Close it completely and try again. |

---

## Grasshopper Component Suite Overview

| Component | Ribbon Subcategory | Primary Role | Key Inputs | Key Outputs |
| :--- | :--- | :--- | :--- | :--- |
| **Vive TCP Listener V2** | `Vive > TCP` | Upstream network receiver; streams poses from publisher at a smooth, rate-limited 50 FPS. | `AutoUpdate` (bool)<br>`IP` (string, default: `127.0.0.1:7000`) | `Msg` (status text)<br>`Vive` (core context)<br>`Plane` (Rhino Z-up)<br>`Time` (timestamp) |
| **Vive Tracker** | `Vive > Tracking Device` | Extracts live tracker pose, transforms custom tool meshes, and exposes trigger/pogo-pin states. | `Vive` (context)<br>`Index` (int, default `0`)<br>`Mesh` (custom tool geometry at `0,0,0`) | `Tracker` (3D geometry)<br>`Plane` (Z-up plane)<br>`Matrix` (4x4 Transform)<br>`TrackerData` (payload)<br>`Trigger` (bool) |
| **Vive Lighthouse** | `Vive > Tracking Device` | Displays base station position, 3D model, and optical beam emission plane. Auto-increments from LH1 to LH4. | `Vive` (context)<br>`Index` (optional int override) | `Lighthouse` (3D geometry)<br>`Plane` (beam direction)<br>`Matrix` (4x4 Transform) |
| **Tool Center Point** | `Vive > Calibration` | Calculates the tool tip ($p_F$) and stationary pivot point ($P_B$) from 4+ tracker poses using least-squares fitting. | `Poses` (list of at least 4 calibration planes) | `TCP_Relative` ($p_F$ in tool frame)<br>`TCP` (tip in world coordinates)<br>`FixedPoint` ($P_B$ world pivot)<br>`Residual` (RMS error in mm) |
| **Vive Take Recorder** | `Vive > TCP` | Records takes of the TCP plane inside a Brep volume (or on trigger) and carves the same volume live in the external Clay Viewer. | `Data` (TCP plane)<br>`Brep` (working volume = stock)<br>`Record`, `Delay`, `Tolerance`, `Reset`<br>`Tool` (tracker-posed mesh) | `Takes` (one branch per take)<br>`Take Count`, `Frame Count`, `Is Inside`, `Status`<br>`Carved Mesh`<br>`Results` (for Take Analytics) |
| **Vive Take Analytics** | `Vive > Utilities` | Per-take and per-plane statistics: speed, path length, tilt from world Z, share above a robot limit, removed volume. | `Results`<br>`Robot Limit` (deg, default 30) | per take: `Frames`, `Duration`, `Path Length`, speeds, tilts, `% Above Limit`<br>per plane: `Plane Tilt`, `Plane Speed`, `Above Limit`<br>`Removed Volume`, `% of Stock`, `Summary` |
| **Vive Calibration Recorder** | `Vive > TCP` | Point-by-point calibration recorder. Captures points on software button press or via physical tracker pogo pin switch. | `Data` (Plane or `TrackerData`)<br>`Record` (capture button)<br>`Reset` (clear button)<br>`Undo` (remove last button) | `Planes` (list of calibration planes)<br>`Points` (list of origin points)<br>`Tree` (DataTree `{point_idx}`)<br>`Count` (int) |

---

## Detailed Component Specifications

### 1. Vive TCP Listener V2 (`ViveListener`)
Main upstream receiver connecting directly to `ViveTcpPublisher.exe` or `dummy_sender.py`. Includes an internal high-resolution throttle (50 FPS) to prevent Rhino viewport redraw lag.

- **Inputs**:
  - `AutoUpdate` (`A`, bool): Connect to a Toggle. Set to `True` to stream live tracking data.
  - `IP` (`IP`, string, optional): Publisher endpoint (default: `127.0.0.1:7000`).
- **Outputs**:
  - `Msg` (`Msg`, string): Live connection info and summary of detected devices.
  - `Vive` (`Vive`, Generic): Master tracking data context passed into `Tracker` and `Lighthouse` components.
  - `Plane` (`Plane`, Plane): Real-time primary tracker plane with Rhino Z facing up.
  - `Time` (`Time`, double): Stream timestamp in seconds.

---

### 2. Vive Tracker (`Tracker`)
Extracts the tracker plane, rigid transformation matrix, 3D geometry representation, custom tool meshes, and physical button/trigger data for any selected tracker index.

- **Inputs**:
  - `Vive` (`V`, Generic): Connect from the `Vive` output of `Vive TCP Listener`.
  - `Index` (`I`, int, default `0`): Tracker index (`0` for first tracker, `1` for second, etc.).
  - `Mesh` (`M`, Geometry, optional): Custom tool geometry modeled in Rhino at `(0, 0, 0)`.
- **Outputs**:
  - `Tracker` (`T`, Geometry): 3D model of the HTC Vive Tracker 3.0 or your transformed custom tool mesh.
  - `Plane` (`P`, Plane): Rhino plane with **Z facing UP** out of the tracker dome (pre-rotated by 90° around X for direct viewport alignment).
  - `Matrix` (`M`, Transform): 4x4 rigid transformation matrix from `Plane.WorldXY` to the tracker pose.
  - `TrackerData` (`TD`, Generic): Composite data payload containing plane, trigger state, and button inputs for recorder components.
  - `Trigger` (`Trig`, bool): Direct boolean state of the physical trigger switch connected to the tracker pogo pins.

*(Right-click context menu: **Pause**)*

---

### 3. Vive Lighthouse (`Lighthouse`)
Extracts the pose, beam aiming direction, and 3D geometry of HTC Vive Lighthouse base stations.

- **Auto-Incrementing Instances**: Every time you place a new `Vive Lighthouse` component onto the canvas, it automatically detects existing lighthouse components and assigns the next index (`Lighthouse1` -> `Lighthouse2` -> `Lighthouse3` -> `Lighthouse4`).
- **Inputs**:
  - `Vive` (`V`, Generic): Connect from the `Vive` output of `Vive TCP Listener`.
  - `Index` (`I`, int, optional): Optional override index (`0` for LH1, `1` for LH2, `2` for LH3, `3` for LH4).
- **Outputs**:
  - `Lighthouse` (`L`, Geometry): 3D mesh model of the Lighthouse base station.
  - `Plane` (`P`, Plane): Lighthouse plane with `Y` pointing in the optical beam emission direction and `Z` pointing up.
  - `Matrix` (`M`, Transform): 4x4 rigid transformation matrix.

*(Right-click context menu: **Pause**, **Select Lighthouse Device (LH 1..4)**)*

---

### 4. Tool Center Point (`TCP`)
Calculates the Tool Center Point (tip) and fixed pivot point using least-squares calibration from at least 4 flange/tracker poses touching a common stationary pivot tip from different angles.

- **Inputs**:
  - `Poses` (`P`, List of Plane): At least 4 diverse tracker poses touching the pivot tip at different orientations.
- **Outputs**:
  - `TCP_Relative` (`Rel`, Point3d): TCP position relative to the flange/tracker coordinate frame ($p_F$).
  - `TCP` (`TCP`, Point3d): Tool Center Point in world coordinates for the first pose.
  - `FixedPoint` (`Fix`, Point3d): Coordinates of the fixed calibration pivot point in the world frame ($P_B$).
  - `Residual` (`R`, double): Root Mean Square (RMS) fitting error in millimetres.
  - `Errors` (`E`, list of double): Individual fitting error for each pose in mm.
  - `Status` (`S`, string): Calibration status text.

---

### 5. Vive Take Recorder (`ViveTakes`) - now includes the Clay Viewer
Records continuous takes of the plane wired into `Data` (feed it the **tool TCP plane**) while it is inside the working volume, or on a physical trigger press, and carves that same volume live in the external `ClayViewer.exe` with the live tool mesh. This component replaces the former separate *Vive Clay Viewer* component (old definitions still open; the old component is hidden and marked obsolete).

- **Inputs**:
  - `Data` (`D`, Tree): the plane to record, normally the TCP plane. A `TrackerData` payload supplies its plane only when no plane is given. Tilt, the viewer's recorded planes and all analytics use this plane.
  - `Brep` (`B`, optional): working volume and carving stock: Box, closed Brep or closed Mesh. A box is carved as a box, anything else as a mesh. Recording happens while the Data plane is inside.
  - `Record` (`R`, bool, default `True`): master recording switch.
  - `Delay` (`Ms`, double, default `0`): minimum milliseconds between frames.
  - `Tolerance` (`Tol`, double, default `0`): minimum movement in mm to register a new frame.
  - `Reset` (`Rst`, bool): clears all recorded takes.
  - `Tool` (`Tl`, Mesh, optional): tool mesh already placed in world by the `Vive Tracker` component. The first mesh is the tool reference, later meshes must be rigid copies of it. Carving is always on.
- **Buttons on the component**: **Open Visualiser**, **Reset Stock**, **Get Mesh**, plus a status bubble below them. Carving resolution (0.25 / 0.5 / 1 mm) is in the right-click menu and is saved with the file.
- **Outputs**:
  - `Takes` (`T`, Tree): planes only, one branch per take `{take}` (several devices: `{device;take}`), in recording order.
  - `Take Count` (`N`), `Frame Count` (`F`), `Is Inside` (`In`), `Status` (`S`).
  - `Carved Mesh` (`M`): the carved stock after pressing **Get Mesh** (document units).
  - `Results` (`Res`): the takes (planes, times, triggers) and the viewer's job numbers (removed volume, stock volume, resolution); wire it to `Vive Take Analytics`.

*(Right-click context menu: **"Require Trigger Button to Record"**, **"Clear All Takes"**, **"Carving Resolution"**)*

The viewer shows the recorded planes live (path, tilt, speed) and carves whatever the tool sweeps. `ClayViewer.exe` is found in a `ClayViewer` folder next to the `.gha`, or in `ClayViewer/publish-win-x64` in the repo.

---

### 5b. Vive Take Analytics (`TakeStats`, `Vive > Utilities`)
Statistics of the recorded takes. Tilt is the angle between the recorded TCP plane's Z axis and world Z, from 0 (pointing up) to 180 degrees.

- **Inputs**: `Results` (from the Take Recorder), `Robot Limit` (`Lim`, degrees, default 30).
- **Outputs, one item per take**: `Frames`, `Duration` (s), `Path Length` (mm), `Mean Speed`, `Max Speed` (mm/s), `Tilt Min` / `Tilt Mean` / `Tilt Max` (deg), `% Above Limit` (share of planes steeper than the limit), `% Above Limit All` (overall).
- **Outputs, per plane** (trees with the recorder's `Takes` paths): `Plane Tilt` (deg), `Plane Speed` (mm/s), `Above Limit` (bool flags).
- **Job**: `Removed Volume` (mm3), `% of Stock`, and a text `Summary`.

---

### 6. Vive Calibration Recorder (`ViveCalib`)
Captures single calibration poses on a Grasshopper Button press (rising edge) or directly via the physical pogo-pin trigger switch, with Undo and Reset functions.

- **Inputs**:
  - `Data` (`D`, Tree): Current tracker plane or `TrackerData`.
  - `Record` (`R`, bool): Connect to a Grasshopper Button (captures on rising edge).
  - `Reset` (`Rst`, bool): Connect to a Grasshopper Button (clears all points).
  - `Undo` (`U`, bool, optional): Connect to a Grasshopper Button (removes the last captured point).
- **Outputs**:
  - `Planes` (`P`, List of Plane): List of recorded calibration planes (connect directly to the `Tool Center Point` component).
  - `Points` (`Pt`, List of Point3d): List of origin points of recorded calibration planes.
  - `Tree` (`T`, Tree): DataTree of all recorded frames structured as `{Point_Index}`.
  - `Count` (`N`, int): Number of calibration points recorded.
  - `Status` (`S`, string): Calibration recording status.

---

### 7. Vive Plane Exchange (`PlaneX`)
Pushes recorded planes to a GitHub data branch and pulls the partner site's planes, so a calibration recorded in France can be used in Austria (and the other way round). Runs in the background; no git install needed. Setup and tokens: [docs/PLANE_EXCHANGE.md](docs/PLANE_EXCHANGE.md).

- **Inputs**: `Planes`; `Site` (France / Austria: a Value List with both is created when you place the component; text `france` / `austria` works too, any case); `Push`, `Pull` (buttons); `Push Name` (empty = timestamp; `latest` is always written too); `Pull Name` (default `latest`).
- **Folders**: Site France pushes to `france` and pulls from `austria`; Site Austria the reverse.
- **Optional inputs, hidden by default**: `Repo` (`owner/name`), `Pull Repo`, `Branch` (`plane-data`), `Token` (leave empty: run `set_exchange_token.ps1`), `List` (button), `Note`. Zoom in on the component and click the **+** / **-** at its inputs to add or remove them (they are added in that order); the choice is saved with the file. Defaults are unchanged.
- **Outputs**: `Pulled Planes` (scaled to the document units), `Info`, `Available`, `Status`.

Definitions saved with the old 13-input version keep all their inputs (including `Remote Site`) and still work.

---

### 8. Vive Plane Straighten (`Straighten`, `Vive > Calibration`)
Makes recorded planes easier for a robot to reproduce. Any plane whose tool axis leans more than `Max Tilt` away from `Up` is rotated back to exactly `Max Tilt`, with the smallest possible rotation about its own origin, so positions never move. Planes within the limit are left untouched.

- **Inputs**: `Planes` (Take Recorder `Takes`, or one list of planes per branch), `Max Tilt` (deg, default 30; 0 = all upright), `Reference` (its Z = Up, its X = roll reference; default World XY), `Flip Axis` (use -Z as the tool axis), `Fix Roll` (remove twist about the tool axis).
- **Outputs**: `Planes` (one branch per take), `Tilt` (original angle per plane), `Clamped` (true where straightened), `Status`.

---

### 9. Vive Take Smooth (`Smooth`, `Vive > Calibration`)
Removes tracking jitter per take, with separate control over position and angle. The smoothing is a centred Gaussian, so the path doesn't lag behind the recording. Orientation is averaged as quaternions, so the planes stay orthonormal.

- **Inputs**: `Planes`, `Position` (sigma in frames, default 3, 0 = off), `Angle` (sigma in frames, default 5), `Keep Ends` (first and last plane stay exact), `Spacing` (resample to equal distance along the path, 0 = off), `Degree` (output curve: 1 = polyline, 3 = smooth).
- **Outputs**: `Planes`, `Curve` (one per take), `Position Change` / `Angle Change` (largest correction per take, to check you aren't over-smoothing), `Status`.

Typical chain: `Take Recorder` → `Take Smooth` → `Plane Straighten` → robot targets.

---

## Tool Visualization and Custom Meshes

You can attach a 3D model of your custom tool (gripper, welding head, stylus, camera mount) to the live tracker:

1. Model your tool in Rhino in millimeters.
2. Position the model at the Rhino origin `(0, 0, 0)` such that `(0, 0, 0)` aligns with the tracker's tracking center point.
3. Reference your tool geometry with a Grasshopper **Mesh** or **Geometry** parameter.
4. Connect it to the **`Mesh` (`M`)** input on the `Vive Tracker` component.
5. The `Tracker` output will now transform and display your tool in real time.

---

## Physical Trigger & Button Wiring (Pogo Pins)

On the underside of the Vive Tracker are 6 gold contact pads (pogo pins). Connecting a pin to **Pin 2 (Ground)** registers a button click in SteamVR:

```
               [ 1/4"-20 Thread ]
               (O)   (o) [Index Hole]
  
       [Pin 1] [Pin 2] [Pin 3] [Pin 4] [Pin 5] [Pin 6]
         GPO     GND    Grip   Trigger  Trackpad  Menu
```

| Pin | Function | Wiring |
| :--- | :--- | :--- |
| **Pin 1** | GPO / Haptics | ~3.3V feedback output |
| **Pin 2** | **Ground (GND)** | Connect to COM terminal of switch |
| **Pin 3** | Grip Button | Active-low (short to Pin 2) |
| **Pin 4** | **Trigger Button** | Connect to NO (Normally Open) terminal of switch |
| **Pin 5** | Trackpad Click | Active-low (short to Pin 2) |
| **Pin 6** | Menu Button | Active-low (short to Pin 2) |

### In Grasshopper:
- **`Vive Tracker`**: The `Trigger` output outputs `True` when your switch is pressed.
- **`Vive Take Recorder`**: Right-click the component and select **"Require Trigger Button to Record"** to record trajectory takes only while the switch is held down.
- **`Vive Calibration Recorder`**: Connect `TrackerData` into `Data`. Clicking the physical switch automatically captures calibration points without using a mouse or keyboard.

---

## Typical Canvas Workflows

### Live Tracking Workflow
```
[Toggle: True] ---> (AutoUpdate) [Vive TCP Listener V2]
                                 (Vive) ----+-----> (Vive) [Vive Tracker] ---> Plane, Matrix, Model
                                            |
                                            +-----> (Vive) [Vive Lighthouse (LH1)] ---> Plane, Model
                                            |
                                            +-----> (Vive) [Vive Lighthouse (LH2)] ---> Plane, Model
```

### TCP Calibration Workflow
```
[Vive Tracker] (TrackerData) ---> (Data) [Vive Calibration Recorder]
[GH Button] --------------------> (Record)
                                         (Planes) ---> (Poses) [Tool Center Point]
                                                                (TCP_Relative) -> Point
                                                                (Residual)     -> Error mm
```

---

## Building from Source (developers)

You need the **.NET 8 SDK** and **Rhino 8** in its default install location. Then run:

```bash
build_all.bat
```

This builds the plugin to `ViveGrasshopperRecorder\bin\Release\net7.0-windows\ViveTcpRecorderV2.gha`. It also publishes a self-contained `ViveTcpPublisher.exe` to `ViveTcpPublisher\publish-win-x64\`. If Rhino is installed somewhere else, pass `-p:RhinoSystemDir=... -p:GrasshopperDir=...` to `dotnet build`. To update the plugin that ships with the repo, copy the new `.gha` to the repo root.

[ViveTcpPublisher/README.md](ViveTcpPublisher/README.md) documents the TCP packet format.

## Repository Structure

```
├── ViveTcpRecorderV2.gha            # Grasshopper plugin, ready to install
├── copy_gha_to_grasshopper.bat      # One-click plugin installer
├── Grasshopper/
│   └── ViveTracking_Template.gh     # Starter Grasshopper definition
├── ViveTcpPublisher/                # SteamVR/OpenVR → TCP streamer (C#)
│   └── publish-win-x64/
│       ├── Launch_ViveTcpPublisher.bat
│       └── ViveTcpPublisher.exe     # Self-contained; doesn't need .NET installed
├── ViveGrasshopperRecorder/         # Grasshopper plugin source (C#)
├── enable_headless_trackers.ps1     # Lets SteamVR track without a headset
├── restore_steamvr_settings.ps1     # Undoes the headless setup
├── dummy_sender.py                  # Offline simulator
├── run_dummy_sender.bat             # 1 simulated tracker
├── run_two_dummy_senders.bat        # 2 simulated trackers
└── build_all.bat                    # Developer build script
```

## License & Credits

- Uses the Valve OpenVR SDK (`openvr_api.dll`). See [THIRD_PARTY_NOTICES](ViveTcpPublisher/THIRD_PARTY_NOTICES.md).
- Rhino and Grasshopper are registered trademarks of Robert McNeel & Associates.
