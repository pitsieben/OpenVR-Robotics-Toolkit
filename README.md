# ViveOpenVR-Grasshopper

A real-time tracking, calibration, and recording pipeline bridging **HTC Vive Trackers & Base Stations** (via SteamVR/OpenVR) directly into **Rhino & Grasshopper** over high-speed TCP.

Built specifically for creative robotics, digital fabrication, tool tracking, and workshop teaching.

---

## Quick Start for Students (No Building Required)

Students **do not** need to install .NET SDK or compile any code. A ready-to-use Grasshopper plugin (`ViveTcpRecorderV2.gha`) is included right in the root folder of this repository.

### 1. Install the Grasshopper Plugin
1. Close Rhino if it is open.
2. In this folder, double-click **`copy_gha_to_grasshopper.bat`**.  
   *(Or manually copy `ViveTcpRecorderV2.gha` into `%APPDATA%\Grasshopper\Libraries\`)*.
3. Open Rhino and launch Grasshopper. You will see a new tab in the ribbon named **Vive**.

### 2. Start Streaming (Choose One)

- **If using real Vive Trackers (Without a VR Headset)**:
  1. Close SteamVR.
  2. Right-click **`enable_headless_trackers.ps1`** and choose **Run with PowerShell** (enables tracking without a headset).
  3. Start SteamVR from Steam and turn on your tracker (LED should turn solid green).
  4. Run **`ViveTcpPublisher\publish-win-x64\Launch_ViveTcpPublisher.bat`** (or `ViveTcpPublisher.exe`) and choose option `[2]`.
- **If testing without hardware (Offline Simulator)**:
  1. Double-click **`run_dummy_sender.bat`**.
  2. This immediately starts streaming simulated tracker motion and 4 corner base stations on port 7000.

### 3. Connect in Grasshopper
1. Place **Vive TCP Listener V2** (`Vive > TCP`) on your canvas.
2. Connect a **Boolean Toggle** to `AutoUpdate` and set it to **True**.
3. Place a **Vive Tracker** component (`Vive > Tracking Device`) and connect `Vive` from the listener to `Vive` on the tracker.

---

## Grasshopper Component Suite Overview

| Component | Ribbon Subcategory | Primary Role | Key Inputs | Key Outputs |
| :--- | :--- | :--- | :--- | :--- |
| **Vive TCP Listener V2** | `Vive > TCP` | Upstream network receiver; streams poses from publisher at a smooth, rate-limited 50 FPS. | `AutoUpdate` (bool)<br>`IP` (string, default: `127.0.0.1:7000`) | `Msg` (status text)<br>`Vive` (core context)<br>`Plane` (Rhino Z-up)<br>`Time` (timestamp) |
| **Vive Tracker** | `Vive > Tracking Device` | Extracts live tracker pose, transforms custom tool meshes, and exposes trigger/pogo-pin states. | `Vive` (context)<br>`Index` (int, default `0`)<br>`Mesh` (custom tool geometry at `0,0,0`) | `Tracker` (3D geometry)<br>`Plane` (Z-up plane)<br>`Matrix` (4x4 Transform)<br>`TrackerData` (payload)<br>`Trigger` (bool) |
| **Vive Lighthouse** | `Vive > Tracking Device` | Displays base station position, 3D model, and optical beam emission plane. Auto-increments from LH1 to LH4. | `Vive` (context)<br>`Index` (optional int override) | `Lighthouse` (3D geometry)<br>`Plane` (beam direction)<br>`Matrix` (4x4 Transform) |
| **Tool Center Point** | `Vive > Calibration` | Calculates the tool tip ($p_F$) and stationary pivot point ($P_B$) from 4+ tracker poses using least-squares fitting. | `Poses` (list of at least 4 calibration planes) | `TCP_Relative` ($p_F$ in tool frame)<br>`TCP` (tip in world coordinates)<br>`FixedPoint` ($P_B$ world pivot)<br>`Residual` (RMS error in mm) |
| **Vive Take Recorder** | `Vive > TCP` | Continuous trajectory recorder. Records takes inside a Brep volume or on physical trigger press. | `Data` (Plane or `TrackerData`)<br>`Brep` (working volume)<br>`Record` (master switch)<br>`Tolerance` (min distance in mm) | `Takes` (DataTree `{dev; take; frame}`)<br>`TakeCount` (int)<br>`FrameCount` (int)<br>`IsInside` (bool) |
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

### 5. Vive Take Recorder (`ViveTakes`)
Records continuous trajectory takes while the tracker is inside a designated Brep working volume, or on a physical trigger switch press.

- **Inputs**:
  - `Data` (`D`, Tree): Tracker plane or `TrackerData` to record.
  - `Brep` (`B`, Brep, optional): Working volume Brep. Recording automatically occurs while inside this volume.
  - `Record` (`R`, bool, default `True`): Master recording switch.
  - `Delay` (`Ms`, double, default `0`): Minimum milliseconds between recorded frames.
  - `Tolerance` (`Tol`, double, default `0`): Minimum movement in mm to register a new frame.
  - `Reset` (`Rst`, bool): Clears all recorded takes when set to True.
- **Outputs**:
  - `Takes` (`T`, Tree): DataTree structured as `{ID_Device; ID_Take; Frame_Index}` storing `Plane`, `Trigger`, `TriggerValue`, and composite `TrackerData`.
  - `TakeCount` (`N`, int): Number of completed takes recorded.
  - `FrameCount` (`F`, int): Total number of frames recorded across all takes.
  - `IsInside` (`In`, bool): True if the tracker origin is inside the working volume Brep.
  - `Status` (`S`, string): Current recording status.

*(Right-click context menu: **"Require Trigger Button to Record"**, **"Clear All Takes"**)*

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

## Repository Structure

```
├── ViveTcpRecorderV2.gha           # Ready-to-use Grasshopper plugin file (install via batch script)
├── copy_gha_to_grasshopper.bat     # One-click installer for ViveTcpRecorderV2.gha
├── ViveTcpPublisher/               # SteamVR OpenVR TCP server
│   └── publish-win-x64/            # Standalone launcher and binaries
│       ├── Launch_ViveTcpPublisher.bat
│       └── ViveTcpPublisher.exe
├── ViveGrasshopperRecorder/        # C# source code for the Grasshopper plugin
├── enable_headless_trackers.ps1    # Configures SteamVR for tracking without a VR headset
├── restore_steamvr_settings.ps1    # Restores default SteamVR settings
├── dummy_sender.py                 # Offline simulator (no hardware needed)
├── run_dummy_sender.bat            # Launches 1 simulated tracker
├── run_two_dummy_senders.bat       # Launches 2 simulated trackers
└── build_all.bat                   # Optional build script for developers
```

---

## License & Credits

- Uses the Valve OpenVR SDK (`openvr_api.dll`).
- Rhino and Grasshopper are registered trademarks of Robert McNeel & Associates.
