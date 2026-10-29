# Vive TCP Publisher

`ViveTcpPublisher.exe` reads a SteamVR/OpenVR generic tracker and publishes its pose as newline-delimited JSON over TCP. It does not use Unity.

## Run

1. Start SteamVR and confirm the tracker is paired and tracking.
2. Run `ViveTcpPublisher.exe --list` to view connected tracker serial numbers.
3. Run `ViveTcpPublisher.exe --serial YOUR_SERIAL`.
4. Connect Grasshopper to `127.0.0.1:7000`.

If there is exactly one tracker, the `--serial` option is optional. Use `--help` for TCP host, port, and rate options.

The default TCP listener is loopback only, so it is inaccessible from other machines. Pass a LAN IP using `--host` only when intentional and ensure the network is trusted.

## Packet format

Every UTF-8 line is one JSON message:

```json
{"v":1,"seq":42,"time":0.466,"utcUnixMilliseconds":1760000000000,"id":"TRACKER_SERIAL","connected":true,"tracked":true,"trackingResult":"Running_OK","position":[0.1,1.2,-0.3],"rotation":[0,0,0,1],"matrix":[...]}
```

`position` is in metres. `matrix` contains the OpenVR 3×4 device-to-standing-tracking-space transform in row-major order. The coordinate system is right-handed: +X right, +Y up, +Z backward.

For Rhino millimetres and Z-up, map a point as `(X, -Z, Y) × 1000`. Use the mapped tracker right axis as Rhino X and the negative mapped OpenVR Z axis as Rhino Y to form a right-handed `Rhino.Geometry.Plane`.

## Distribution

The `publish-win-x64` folder is built as a self-contained, single executable and includes the OpenVR runtime DLL. Users do not need a .NET installation or the OpenVR SDK, but they must install SteamVR and the device drivers required for their tracker.
