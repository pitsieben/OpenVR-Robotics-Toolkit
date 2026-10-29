# ViveOpenVR-Grasshopper

A real-time tracking, calibration, and recording pipeline bridging **HTC Vive Trackers & Base Stations** (via SteamVR/OpenVR) directly into **Rhino & Grasshopper** over high-speed TCP.

Built specifically for creative robotics, digital fabrication, tool tracking, and workshop research.

---

## What This Repository Contains

1. **`ViveTcpPublisher`**  
   A standalone C# console application that talks to SteamVR / OpenVR. It streams 6-DoF poses, tracking status, and pogo-pin button/trigger inputs over a local TCP socket (`127.0.0.1:7000`) as clean JSON packets at up to 90 Hz.
2. **`ViveGrasshopperRecorder` (`ViveTcpRecorderV2.gha`)**  
   A Grasshopper plugin for Rhino 7 and Rhino 8 providing a modular suite of **6 dedicated components**:
   - **Vive TCP Listener V2** (`Vive > TCP`)
   - **Vive Tracker** (`Vive > Tracking Device`)
   - **Vive Lighthouse** (`Vive > Tracking Device`)
   - **Tool Center Point (TCP)** (`Vive > Calibration`)
   - **Vive Take Recorder** (`Vive > TCP`)
   - **Vive Calibration Recorder** (`Vive > TCP`)
3. **Headless SteamVR Utilities**  
   PowerShell scripts to configure SteamVR to track Vive Trackers **without needing a VR Headset (HMD)** connected.
4. **Offline Simulator (`dummy_sender.py`)**  
   Simulates 1 tracker and 4 corner base stations along circle/spiral/wave trajectories with trigger toggling so students can test definitions without hardware.

---

## Component Suite Reference

### 1. Vive TCP Listener V2 (`ViveListener`)
Main upstream network receiver. Streams OpenVR tracking poses for all connected devices (trackers, base stations, controllers) over TCP with a built-in 50 FPS rate limiter to keep the Rhino viewport smooth.

- **Category**: `Vive > TCP`
- **Inputs**:
  - `AutoUpdate` (`A`, bool): Connect to a Toggle. Set to `True` to connect and stream live tracking data.
  - `IP` (`IP`, string, optional): Publisher endpoint (default: `127.0.0.1:7000`).
- **Outputs**:
  - `Msg` (`Msg`, string): Live connection info and summary of detected devices.
  - `Vive` (`Vive`, Generic): Master tracking data context passed into `Tracker` and `Lighthouse` components.
  - `Plane` (`Plane`, Plane): Real-time primary tracker plane with Rhino Z facing up.
  - `Time` (`Time`, double): Stream timestamp in seconds.

---

### 2. Vive Tracker (`Tracker`)
Extracts the tracker plane, rigid transformation matrix, 3D geometry representation, custom tool meshes, and physical button/trigger data for any selected tracker index.

- **Category**: `Vive > Tracking Device`
- **Inputs**:
  - `Vive` (`V`, Generic): Connect from the `Vive` output of `Vive TCP Listener` (or original `ViveTrack`).
  - `Index` (`I`, int, default `0`): Tracker index (`0` for first tracker, `1` for second, etc.).
  - `Mesh` (`M`, Geometry, optional): Custom tool geometry modeled at `(0, 0, 0)`.
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

- **Category**: `Vive > Tracking Device`
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

- **Category**: `Vive > Calibration`
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

- **Category**: `Vive > TCP`
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

- **Category**: `Vive > TCP`
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

## Hardware Requirements

- **HTC Vive Tracker** (2.0 or 3.0) + USB wireless dongle.
- **HTC Vive Base Stations / Lighthouses** (1.0 or 2.0, at least 1 or 2 stations).
- Windows 10 or 11 (64-bit).
- **Rhino 7 or Rhino 8** with Grasshopper.
- **Steam & SteamVR** installed.
- *(Optional)* A VR headset. If you do not have a headset, use the included **Headless Mode** script.

---

## Step-by-Step Tutorial for Students

### Step 1: Install Prerequisites

1. Ensure **Steam** and **SteamVR** are installed on your machine.
2. Ensure **.NET 8.0 Runtime** (or SDK) is installed. If needed, download the free x64 installer from [Microsoft .NET download](https://dotnet.microsoft.com/download/dotnet/8.0).
3. If building from source, install [.NET 8 SDK](https://dotnet.microsoft.com/download) and [.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0).

---

### Step 2: Build and Install the Grasshopper Plugin

1. Close Rhino if it is currently open.
2. Double-click **`build_all.bat`** in the repository root. This compiles the publisher and the Grasshopper `.gha` file.
3. Double-click **`copy_gha_to_grasshopper.bat`**. This automatically copies `ViveTcpRecorderV2.gha` into:
   ```
   %APPDATA%\Grasshopper\Libraries\ViveTcpRecorderV2.gha
   ```
4. Open Rhino and launch Grasshopper. You will see a new tab in the ribbon named **Vive**.

---

### Step 3: Run Without a VR Headset (Headless Mode)

If you are using Vive Trackers without a VR headset:

1. Close SteamVR.
2. Right-click **`enable_headless_trackers.ps1`** and choose **Run with PowerShell** (or run it in a PowerShell terminal).
   - This enables SteamVR's virtual `null` driver and sets `requireHmd: false`.
3. Launch SteamVR from Steam. SteamVR will initialize without complaining about a missing headset.
4. Turn on your Vive Tracker and make sure its LED turns solid green (paired and tracking).

*(Note: To revert SteamVR back to standard headset mode later, run `restore_steamvr_settings.ps1`).*

---

### Step 4: Start Streaming Tracking Data

#### Option A: Using Real Vive Hardware
1. Make sure SteamVR is running and the tracker light is green.
2. Go to `ViveTcpPublisher\publish-win-x64\` and double-click **`Launch_ViveTcpPublisher.bat`** (or run `ViveTcpPublisher.exe`).
3. Press `[1]` to list connected devices and verify your tracker is detected.
4. Press `[2]` to start streaming on `127.0.0.1:7000`.

#### Option B: Offline Simulator (No Hardware Needed)
If you are working remotely or do not have tracker hardware:
1. Double-click **`run_dummy_sender.bat`**.
2. A terminal window will open streaming simulated poses and 4 corner base stations on port `7000`.

---

### Step 5: Typical Canvas Setup in Grasshopper

#### Basic Live Tracking Workflow:
1. Place **Vive TCP Listener V2** (`Vive > TCP`).
2. Attach a **Boolean Toggle** to `AutoUpdate` and set it to `True`.
3. Place a **Vive Tracker** component (`Vive > Tracking Device`). Connect the `Vive` output from the Listener to the `Vive` input on the Tracker.
4. Place one or more **Vive Lighthouse** components (`Vive > Tracking Device`). Connect `Vive` to inspect base station positions in Rhino.

#### TCP Calibration Workflow:
1. Place **Vive Calibration Recorder** (`Vive > TCP`).
2. Connect `TrackerData` from `Vive Tracker` into `Data`.
3. Connect a Grasshopper Button to `Record` (or simply click your physical pogo-pin button on the tool tip).
4. Take at least 4 poses touching a stationary point from different angles.
5. Connect `Planes` from `Vive Calibration Recorder` directly into `Poses` on **Tool Center Point** (`Vive > Calibration`).
6. Inspect `TCP_Relative` ($p_F$) and `Residual` (RMS fitting error in mm).

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

## Repository Structure

```
├── ViveGrasshopperRecorder/        # C# source for Grasshopper plugin (.gha)
│   ├── Resources/                  # Embedded icons, OBJ models, and meshes
│   ├── ViveTcpRecorderComponentV2.cs
│   ├── ViveTrackerComponent.cs
│   ├── ViveLighthouseComponent.cs
│   ├── ViveToolCenterPointComponent.cs
│   ├── ViveTakeRecorderComponent.cs
│   ├── ViveCalibrationRecorderComponent.cs
│   ├── ViveTrackData.cs
│   └── ViveTrackerData.cs
├── ViveTcpPublisher/               # C# OpenVR TCP server
│   ├── Native/win-x64/             # openvr_api.dll
│   ├── OpenVrBindings.cs           # C# OpenVR interop definitions
│   └── Program.cs                  # Publisher server logic
├── copy_gha_to_grasshopper.bat     # Deploys GHA to Grasshopper libraries
├── build_all.bat                   # Builds plugin and publisher
├── enable_headless_trackers.ps1    # Configures SteamVR for no-HMD tracking
├── restore_steamvr_settings.ps1    # Restores default SteamVR settings
├── dummy_sender.py                 # Offline simulator script
├── run_dummy_sender.bat            # Launches 1 simulated tracker
├── run_two_dummy_senders.bat       # Launches 2 simulated trackers
└── .gitignore                      # Git ignore rules for build artifacts
```

---

## License & Credits

- Uses the Valve OpenVR SDK (`openvr_api.dll`).
- Rhino and Grasshopper are registered trademarks of Robert McNeel & Associates.
