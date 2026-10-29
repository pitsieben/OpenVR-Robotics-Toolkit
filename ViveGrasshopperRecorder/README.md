# Vive TCP Plugin for Grasshopper

`ViveTcpRecorderV2.gha` receives real-time tracking streams from `ViveTcpPublisher.exe` (or `dummy_sender.py`) and provides a modular suite of components for Vive Trackers, Lighthouses, Tool Center Point (TCP) calibration, and trajectory recording.

---

## Component Suite Overview

### 1. Vive TCP Listener V2 (`ViveListener`)
Main upstream network receiver. Streams OpenVR tracking poses for all connected devices (trackers, base stations, controllers) over TCP.

- **Inputs**:
  - `AutoUpdate` (`A`, bool): Connect to a Toggle. Set to `True` to connect and stream live tracking data.
  - `IP` (`IP`, string, optional): Publisher endpoint (default: `127.0.0.1:7000`).
- **Outputs**:
  - `Msg` (`Msg`, string): Live connection info and summary of detected devices.
  - `Vive` (`Vive`, Generic): Master tracking data context passed into `Tracker` and `Lighthouse` components.
  - `Plane` (`Plane`, Plane): Real-time primary tracker plane with **Rhino Z facing UP**.
  - `Time` (`Time`, double): Stream timestamp in seconds.

---

### 2. Vive Tracker (`Tracker`)
Extracts the tracker plane, rigid transformation matrix, and 3D geometry representation for any selected tracker index.

- **Inputs**:
  - `Vive` (`V`, Generic): Connect from the `Vive` output of `Vive TCP Listener` (or original `ViveTrack`).
  - `Index` (`I`, int, default `0`): Tracker index (`0` for first tracker, `1` for second, etc.).
- **Outputs**:
  - `Tracker` (`T`, Geometry): 3D mesh model of the HTC Vive Tracker.
  - `Plane` (`P`, Plane): Rhino plane with **Z facing UP** out of the tracker dome (no extra flipping components required!).
  - `Matrix` (`M`, Transform): 4x4 rigid transformation matrix from `Plane.WorldXY` to the tracker plane.

*(Right-click context menu: **Pause**)*

---

### 3. Vive Lighthouse (`Lighthouse`)
Extracts the pose, beam aiming direction, and 3D geometry of HTC Vive Lighthouse base stations.

- **Auto-Incrementing Instances**: Every time you place a new `Vive Lighthouse` component onto the Grasshopper canvas, it automatically detects any existing lighthouse components and auto-assigns the next index (`Lighthouse1` -> `Lighthouse2` -> `Lighthouse3` -> `Lighthouse4`).
- **Inputs**:
  - `Vive` (`V`, Generic): Connect from the `Vive` output of `Vive TCP Listener` (or original `ViveTrack`).
  - `Index` (`I`, int, optional): Optional override for the lighthouse index (`0` for LH1, `1` for LH2, `2` for LH3, `3` for LH4). If left unconnected, it automatically uses the assigned index.
- **Outputs**:
  - `Lighthouse` (`L`, Geometry): 3D mesh model of the Lighthouse base station.
  - `Plane` (`P`, Plane): Lighthouse plane with `Y` pointing in the optical beam emission direction and `Z` pointing up.
  - `Matrix` (`M`, Transform): 4x4 rigid transformation matrix.

*(Right-click context menu: **Pause**, **Select Lighthouse Device (LH 1..4)**)*

---

### 4. Tool Center Point (`TCP`)
Calculates the Tool Center Point (tip) and fixed pivot point using least-squares calibration from at least 4 flange/tracker poses pointing at a common fixed pivot tip.

- **Inputs**:
  - `Poses` (`P`, List of Plane): At least 4 diverse tracker poses touching the pivot tip at different orientations.
- **Outputs**:
  - `TCP_Relative` (`Rel`, Point3d): TCP position relative to the flange coordinate system ($p_F$).
  - `TCP` (`TCP`, Point3d): Tool Center Point in world/base coordinates.
  - `FixedPoint` (`Fix`, Point3d): Coordinates of the fixed calibration pivot point in the world frame ($P_B$).
  - `Residual` (`R`, double): Root Mean Square (RMS) fitting error in millimetres.
  - `Errors` (`E`, list of double): Fitting error for each individual pose in mm.
  - `Status` (`S`, string): Calibration status text.

---

### 5. Vive Take Recorder (`ViveTakes`)
Records trajectory takes automatically while the tracker is inside a designated Brep working volume.

- **Inputs**:
  - `Data` (`D`): Tracker plane or DataTree.
  - `Brep` (`B`, optional): Working volume Brep.
  - `Record` (`R`, bool): Master recording switch.
  - `Delay` (`Ms`, double): Minimum milliseconds between frames.
  - `Tolerance` (`Tol`, double): Minimum movement in mm to register a frame.
  - `Reset` (`Rst`, bool): Clears all recorded takes.
- **Outputs**:
  - `Takes` (`T`): DataTree structured as `{ID_Device; ID_Take; Frame_Index}`.
  - `TakeCount` (`N`), `FrameCount` (`F`), `IsInside` (`In`), `Status` (`S`).

---

### 6. Vive Calibration Recorder (`ViveCalib`)
Captures single calibration planes on a Grasshopper Button press (rising edge) with Undo and Reset.

- **Inputs**: `Data`, `Record` (Button), `Reset` (Button), `Undo` (Button).
- **Outputs**: `Planes`, `Points`, `Tree` (`{Point_Index}`), `Count`, `Status`.

---

## Typical Canvas Wiring (Matching Reference Setup)

```
[Toggle: True] ---> (AutoUpdate) [Vive TCP Listener V2]
                                 (Msg)
                                 (Vive) ----+-----> (Vive) [Lighthouse2] ---> Plane / Matrix
                                            |
                                            +-----> (Vive) [Lighthouse1] ---> Plane / Matrix
                                            |
                                            +-----> (Vive) [Tracker] ------> Plane (Z-UP) / Matrix
                                                    (Index: 0)
```

---

## Testing with Dummy Sender

You can test the entire multi-device workflow without VR hardware:

```powershell
python dummy_sender.py --mode spiral
```

This simulates 1 tracker moving on a spiral trajectory and 2 fixed Lighthouse base stations streaming over TCP on port 7000.
