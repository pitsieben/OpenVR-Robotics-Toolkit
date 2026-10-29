# ViveOpenVR-Grasshopper

A real-time tracking, calibration, and recording pipeline bridging **HTC Vive Trackers & Base Stations** (via SteamVR/OpenVR) directly into **Rhino & Grasshopper** over high-speed TCP.

Built specifically for creative robotics, digital fabrication, tool tracking, and workshop research.

---

## What This Repository Contains

1. **`ViveTcpPublisher`**  
   A lightweight, standalone C# console application that talks to SteamVR / OpenVR. It streams 6-DoF poses, tracking status, and pogo-pin button/trigger inputs over a local TCP socket (`127.0.0.1:7000`) as clean JSON packets at up to 90 Hz.
2. **`ViveGrasshopperRecorder` (`ViveTcpRecorderV2.gha`)**  
   A Grasshopper plugin for Rhino 7 and Rhino 8 (.NET 7 / .NET Framework) providing 6 dedicated components:
   - **Vive TCP Listener V2**: Receives real-time streams with a 50 FPS rate limiter to keep the Rhino viewport smooth and lag-free.
   - **Vive Tracker**: Extracts the tracker plane (Z-up aligned to Rhino), 4x4 transform, optional custom tool mesh visualization, and physical button/trigger data.
   - **Vive Lighthouse**: Auto-increments lighthouse devices (LH1 to LH4) on canvas placement and displays their optical emission directions.
   - **Tool Center Point (TCP)**: Calculates the tool tip ($p_F$) and stationary pivot point ($P_B$) from 4+ tracker calibration poses using least-squares estimation.
   - **Vive Take Recorder**: Records trajectory takes while the tracker is inside a designated Brep working volume or on physical trigger press.
   - **Vive Calibration Recorder**: Captures single calibration points on button clicks or directly via the tracker's physical trigger pin.
3. **Headless SteamVR Utilities**  
   PowerShell scripts to enable tracking Vive Trackers **without an HMD / VR Headset connected**.
4. **Offline Simulator (`dummy_sender.py`)**  
   Simulates 1 tracker and 4 lighthouses along circle/spiral/wave trajectories so students can test their Grasshopper definitions without hardware.

---

## Hardware Requirements

- **HTC Vive Tracker** (2.0 or 3.0) + USB wireless dongle.
- **HTC Vive Base Stations / Lighthouses** (1.0 or 2.0, at least 1 or 2 stations).
- Windows 10 or 11 (64-bit).
- **Rhino 7 or Rhino 8** with Grasshopper.
- **Steam & SteamVR** installed.
- *(Optional)* A VR headset. If you do not have a headset, you can run in **Headless Mode** using the included script.

---

## Step-by-Step Tutorial for Students

### Step 1: Install Prerequisites

1. Ensure **Steam** and **SteamVR** are installed on your machine.
2. Ensure **.NET 8.0 Runtime** (or SDK) is installed so the publisher can run. If needed, download the free x64 installer from Microsoft's [.NET download page](https://dotnet.microsoft.com/download/dotnet/8.0).
3. If building from source, install [.NET 8 SDK](https://dotnet.microsoft.com/download) and [.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0).

---

### Step 2: Build and Install the Grasshopper Plugin

1. Close Rhino if it is currently open.
2. Double-click **`build_all.bat`** in the repository root. This compiles both the publisher and the Grasshopper `.gha` file.
3. Double-click **`copy_gha_to_grasshopper.bat`**. This automatically copies `ViveTcpRecorderV2.gha` into your Grasshopper libraries folder:
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

*(Note: To revert SteamVR back to standard VR headset mode later, run `restore_steamvr_settings.ps1`).*

---

### Step 4: Start Streaming Tracking Data

You have two options: real hardware or the offline simulator.

#### Option A: Using Real Vive Hardware
1. Make sure SteamVR is running and the tracker light is green.
2. Go to `ViveTcpPublisher\publish-win-x64\` and double-click **`Launch_ViveTcpPublisher.bat`** (or run `ViveTcpPublisher.exe`).
3. Press `[1]` to list connected devices and verify your tracker is detected.
4. Press `[2]` to start streaming on `127.0.0.1:7000`.

#### Option B: Offline Simulator (No Hardware Needed)
If you are at home or do not have the tracker hardware:
1. Double-click **`run_dummy_sender.bat`**.
2. A terminal window will open streaming simulated poses and 4 corner base stations on port `7000`.

---

### Step 5: Connect in Grasshopper

1. In Grasshopper, place a **Vive TCP Listener V2** component on the canvas (`Vive > TCP`).
2. Attach a Grasshopper **Boolean Toggle** to the `AutoUpdate` input and switch it to `True`.
   - The `Msg` output will display `Connected` and list all detected devices (e.g. `1 Tracker, 4 Lighthouses`).
3. Place a **Vive Tracker** component (`Vive > Tracking Device`).
   - Connect the `Vive` output from the Listener into the `Vive` input of the Tracker component.
   - The `Plane` output delivers the tracker's live plane (Rhino Z facing up).
   - The `Tracker` output displays the 3D model of the Vive Tracker in your Rhino viewport.

---

## Tool Visualization and Custom Meshes

You can attach a 3D model of your custom tool (gripper, welding head, stylus, camera mount) to the live tracker:

1. Model your tool in Rhino in millimeters.
2. Position the model at the Rhino origin `(0, 0, 0)` such that `(0, 0, 0)` is the tracker's tracking center point.
3. Reference your tool geometry with a Grasshopper **Mesh** or **Geometry** parameter.
4. Connect it to the **`Mesh` (`M`)** input on the `Vive Tracker` component.
5. The `Tracker` output will now transform and display your tool in real time.

---

## Physical Trigger & Button Wiring (Pogo Pins)

On the underside of the Vive Tracker are 6 gold contact pads (pogo pins). Connecting a pin to **Pin 2 (Ground)** registers a button click in SteamVR:

| Pin | Function | Wiring |
| :--- | :--- | :--- |
| **Pin 1** | GPO / Haptics | ~3.3V feedback output |
| **Pin 2** | **Ground (GND)** | Connect to one terminal of your momentary switch |
| **Pin 3** | Grip Button | Active-low (short to Pin 2) |
| **Pin 4** | **Trigger Button** | Active-low (connect to second terminal of your momentary switch) |
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
