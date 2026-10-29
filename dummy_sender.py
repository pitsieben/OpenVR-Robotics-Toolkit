#!/usr/bin/env python3
"""
HTC Vive Dummy Data Sender for Grasshopper ViveTcpRecorder
=========================================================
Simulates the TCP pose stream from ViveTcpPublisher.exe without requiring
SteamVR, OpenVR runtime, or physical HTC Vive trackers.

Streams newline-delimited JSON packets matching the PosePacket format expected
by ViveTcpRecorderComponent on port 7000.
"""

import argparse
import asyncio
import json
import math
import sys
import time
from typing import Dict, List, Set, Tuple


def compute_quaternion(r: List[List[float]]) -> List[float]:
    """Computes quaternion [x, y, z, w] from a 3x3 rotation matrix r[row][col]."""
    m00, m01, m02 = r[0][0], r[0][1], r[0][2]
    m10, m11, m12 = r[1][0], r[1][1], r[1][2]
    m20, m21, m22 = r[2][0], r[2][1], r[2][2]

    trace = m00 + m11 + m22
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        w = 0.25 * s
        x = (m21 - m12) / s
        y = (m02 - m20) / s
        z = (m10 - m01) / s
    elif m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2.0
        w = (m21 - m12) / s
        x = 0.25 * s
        y = (m01 + m10) / s
        z = (m02 + m20) / s
    elif m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2.0
        w = (m02 - m20) / s
        x = (m01 + m10) / s
        y = 0.25 * s
        z = (m12 + m21) / s
    else:
        s = math.sqrt(1.0 + m22 - m00 - m11) * 2.0
        w = (m10 - m01) / s
        x = (m02 + m20) / s
        y = (m12 + m21) / s
        z = 0.25 * s

    return [round(x, 6), round(y, 6), round(z, 6), round(w, 6)]


def rotation_matrix_y(yaw: float) -> List[List[float]]:
    """Rotation around OpenVR Y-axis (vertical up)."""
    c, s = math.cos(yaw), math.sin(yaw)
    return [
        [c, 0.0, s],
        [0.0, 1.0, 0.0],
        [-s, 0.0, c],
    ]


def rotation_matrix_rpy(roll: float, pitch: float, yaw: float) -> List[List[float]]:
    """
    Euler angles (Roll X, Pitch Y, Yaw Z) rotation matrix.
    R = Rz(yaw) * Ry(pitch) * Rx(roll)
    """
    cr, sr = math.cos(roll), math.sin(roll)
    cp, sp = math.cos(pitch), math.sin(pitch)
    cy, sy = math.cos(yaw), math.sin(yaw)

    return [
        [cy * cp, cy * sp * sr - sy * cr, cy * sp * cr + sy * sr],
        [sy * cp, sy * sp * sr + cy * cr, sy * sp * cr - cy * sr],
        [-sp, cp * sr, cp * cr],
    ]


def generate_pose(
    t: float, mode: str, speed: float, radius: float
) -> Tuple[List[float], List[List[float]]]:
    """
    Generates tracker position [x, y, z] and 3x3 rotation matrix in OpenVR coords:
      +X: Right, +Y: Up, +Z: Backward (metres)
    Rhino maps this to:
      Rhino X = +X * 1000 mm
      Rhino Y = -Z * 1000 mm (Forward)
      Rhino Z = +Y * 1000 mm (Up)
    """
    st = t * speed

    if mode == "circle":
        # Horizontal circle at 1.0m height, facing tangential direction
        omega = 2.0 * math.pi * 0.2 * st
        x = radius * math.cos(omega)
        y = 1.0 + 0.08 * math.sin(2.0 * omega)
        z = -1.2 + radius * math.sin(omega)
        yaw = -omega + math.pi / 2.0
        r = rotation_matrix_y(yaw)

    elif mode == "spiral":
        # Upward corkscrew / helix
        omega = 2.0 * math.pi * 0.25 * st
        x = radius * math.cos(omega)
        # Bounces between 0.6m and 1.6m
        cycle = (st * 0.1) % 2.0
        y = 0.6 + (cycle if cycle <= 1.0 else 2.0 - cycle)
        z = -1.2 + radius * math.sin(omega)
        yaw = -omega + math.pi / 2.0
        pitch = 0.15 * math.sin(omega)
        r = rotation_matrix_rpy(0.0, pitch, yaw)

    elif mode == "figure8":
        # Lemniscate of Gerono (figure-8)
        omega = 2.0 * math.pi * 0.15 * st
        x = radius * math.sin(omega)
        y = 1.0 + 0.15 * math.sin(2.0 * omega)
        z = -1.2 + radius * math.sin(omega) * math.cos(omega)
        # Tangent angle for yaw
        dx = radius * math.cos(omega)
        dz = radius * (math.cos(omega) ** 2 - math.sin(omega) ** 2)
        yaw = math.atan2(-dx, -dz) if (dx != 0 or dz != 0) else 0.0
        r = rotation_matrix_y(yaw)

    elif mode == "wave":
        # S-curve / wave across X
        x = radius * 1.5 * math.sin(st * 0.8)
        y = 1.0 + 0.2 * math.cos(st * 1.6)
        z = -1.2 + 0.3 * math.sin(st * 1.2)
        roll = 0.2 * math.sin(st * 1.6)
        pitch = 0.15 * math.cos(st * 1.2)
        yaw = 0.3 * math.cos(st * 0.8)
        r = rotation_matrix_rpy(roll, pitch, yaw)

    elif mode == "static":
        # Stationary position with small micro-jitter
        x = 0.0 + 0.001 * math.sin(st * 5.0)
        y = 1.0 + 0.001 * math.cos(st * 4.0)
        z = -1.2 + 0.001 * math.sin(st * 3.0)
        r = rotation_matrix_y(0.0)

    else:
        raise ValueError(f"Unknown mode: {mode}")

    return [round(x, 5), round(y, 5), round(z, 5)], r


def build_packet(
    seq: int,
    elapsed_time: float,
    position: List[float],
    rot_matrix: List[List[float]],
    tracker_id: str = "LHR-SIMULATED",
) -> Dict:
    """Builds a PosePacket conforming to the OpenVR publisher format."""
    r00, r01, r02 = rot_matrix[0]
    r10, r11, r12 = rot_matrix[1]
    r20, r21, r22 = rot_matrix[2]
    tx, ty, tz = position

    # 3x4 matrix in row-major order
    matrix = [
        r00, r01, r02, tx,
        r10, r11, r12, ty,
        r20, r21, r22, tz,
    ]

    quaternion = compute_quaternion(rot_matrix)
    rounded_matrix = [round(val, 6) for val in matrix]

    trigger = (int(elapsed_time * 0.5) % 4) == 0
    trigger_val = 1.0 if trigger else 0.0

    devices = [
        {
            "deviceIndex": 1,
            "id": tracker_id,
            "deviceClass": "Tracker",
            "model": "Vive Tracker 3.0",
            "connected": True,
            "tracked": True,
            "trackingResult": "Running_OK",
            "position": position,
            "rotation": quaternion,
            "matrix": rounded_matrix,
            "trigger": trigger,
            "triggerValue": trigger_val,
            "grip": False,
            "menu": False,
            "trackpad": False,
            "buttonMask": (1 << 33) if trigger else 0,
        },
        {
            "deviceIndex": 2,
            "id": "LHB-SIMULATED-1",
            "deviceClass": "Lighthouse",
            "model": "HTC VIVE Base Station",
            "connected": True,
            "tracked": True,
            "trackingResult": "Running_OK",
            "position": [-1.8, 2.2, -1.8],
            "rotation": [0.183, -0.383, 0.183, 0.887],
            "matrix": [0.7071, 0.3536, 0.6124, -1.8, 0.0, 0.8660, -0.5000, 2.2, -0.7071, 0.3536, 0.6124, -1.8],
        },
        {
            "deviceIndex": 3,
            "id": "LHB-SIMULATED-2",
            "deviceClass": "Lighthouse",
            "model": "HTC VIVE Base Station",
            "connected": True,
            "tracked": True,
            "trackingResult": "Running_OK",
            "position": [1.8, 2.2, 1.8],
            "rotation": [-0.183, 0.383, 0.183, 0.887],
            "matrix": [-0.7071, 0.3536, -0.6124, 1.8, 0.0, 0.8660, -0.5000, 2.2, 0.7071, 0.3536, -0.6124, 1.8],
        },
        {
            "deviceIndex": 4,
            "id": "LHB-SIMULATED-3",
            "deviceClass": "Lighthouse",
            "model": "HTC VIVE Base Station",
            "connected": True,
            "tracked": True,
            "trackingResult": "Running_OK",
            "position": [-1.8, 2.2, 1.8],
            "rotation": [0.383, 0.183, -0.183, 0.887],
            "matrix": [-0.7071, 0.3536, 0.6124, -1.8, 0.0, 0.8660, -0.5000, 2.2, -0.7071, -0.3536, -0.6124, 1.8],
        },
        {
            "deviceIndex": 5,
            "id": "LHB-SIMULATED-4",
            "deviceClass": "Lighthouse",
            "model": "HTC VIVE Base Station",
            "connected": True,
            "tracked": True,
            "trackingResult": "Running_OK",
            "position": [1.8, 2.2, -1.8],
            "rotation": [-0.383, -0.183, 0.183, 0.887],
            "matrix": [0.7071, 0.3536, -0.6124, 1.8, 0.0, 0.8660, -0.5000, 2.2, 0.7071, -0.3536, 0.6124, -1.8],
        },
    ]

    return {
        "v": 2,
        "seq": seq,
        "time": round(elapsed_time, 6),
        "utcUnixMilliseconds": int(time.time() * 1000),
        "id": tracker_id,
        "connected": True,
        "tracked": True,
        "trackingResult": "Running_OK",
        "position": position,
        "rotation": quaternion,
        "matrix": rounded_matrix,
        "trigger": trigger,
        "triggerValue": trigger_val,
        "devices": devices,
    }


class DummyViveServer:
    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 7000,
        rate_hz: int = 90,
        mode: str = "circle",
        speed: float = 1.0,
        radius: float = 0.5,
        tracker_id: str = "LHR-SIMULATED",
    ):
        self.host = host
        self.port = port
        self.rate_hz = rate_hz
        self.interval = 1.0 / rate_hz
        self.mode = mode
        self.speed = speed
        self.radius = radius
        self.tracker_id = tracker_id

        self.clients: Set[asyncio.StreamWriter] = set()
        self.running = False
        self.total_sent = 0

    async def handle_client(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter
    ):
        peer = writer.get_extra_info("peername")
        print(f"[+] Grasshopper receiver connected: {peer[0]}:{peer[1]}")
        self.clients.add(writer)

        try:
            # Keep client open until connection drops or server stops
            while self.running:
                data = await reader.read(1024)
                if not data:
                    break
        except (ConnectionResetError, asyncio.CancelledError):
            pass
        finally:
            self.clients.discard(writer)
            print(f"[-] Grasshopper receiver disconnected: {peer[0]}:{peer[1]}")
            try:
                writer.close()
                await writer.wait_closed()
            except Exception:
                pass

    async def broadcast_loop(self):
        start_time = time.perf_counter()
        seq = 0
        last_stat_time = start_time
        packets_since_stat = 0

        while self.running:
            now = time.perf_counter()
            elapsed = now - start_time

            pos, rot = generate_pose(elapsed, self.mode, self.speed, self.radius)
            packet = build_packet(seq, elapsed, pos, rot, self.tracker_id)
            seq += 1

            payload = (json.dumps(packet) + "\n").encode("utf-8")

            if self.clients:
                dead_clients = set()
                for writer in list(self.clients):
                    try:
                        writer.write(payload)
                        await writer.drain()
                        self.total_sent += 1
                        packets_since_stat += 1
                    except (ConnectionResetError, BrokenPipeError):
                        dead_clients.add(writer)

                for dead in dead_clients:
                    self.clients.discard(dead)

            # Live status update every 2 seconds if clients are connected
            if now - last_stat_time >= 2.0:
                dt = now - last_stat_time
                actual_fps = packets_since_stat / dt if dt > 0 else 0.0
                if self.clients:
                    print(
                        f"    [Broadcasting] Clients: {len(self.clients)} | "
                        f"Total sent: {self.total_sent} | "
                        f"Rate: {actual_fps:.1f} Hz | "
                        f"Pos (Rhino mm): [{pos[0]*1000:.0f}, {-pos[2]*1000:.0f}, {pos[1]*1000:.0f}]"
                    )
                last_stat_time = now
                packets_since_stat = 0

            # Accurate sleep for target frequency
            target_next = start_time + (seq * self.interval)
            sleep_duration = target_next - time.perf_counter()
            if sleep_duration > 0:
                await asyncio.sleep(sleep_duration)
            else:
                await asyncio.sleep(0)

    async def run(self):
        self.running = True
        server = await asyncio.start_server(self.handle_client, self.host, self.port)
        print("=" * 68)
        print(" HTC VIVE DUMMY POSE SENDER (GRASSHOPPER TEST HARNESS)")
        print("=" * 68)
        print(f" Listening on : {self.host}:{self.port}")
        print(f" Target rate  : {self.rate_hz} Hz ({self.interval*1000:.1f} ms interval)")
        print(f" Motion mode  : '{self.mode}' (speed: {self.speed}x, radius: {self.radius}m)")
        print(f" Tracker ID   : {self.tracker_id}")
        print("=" * 68)
        print("Waiting for Grasshopper ViveTcpRecorder to connect...")
        print("(In Grasshopper: set IP to 127.0.0.1, toggle Listen = True)")
        print("Press Ctrl+C to stop.\n")

        broadcast_task = asyncio.create_task(self.broadcast_loop())

        async with server:
            try:
                await server.serve_forever()
            except asyncio.CancelledError:
                pass
            finally:
                self.running = False
                broadcast_task.cancel()
                try:
                    await broadcast_task
                except asyncio.CancelledError:
                    pass


def main():
    parser = argparse.ArgumentParser(
        description="Dummy HTC Vive Tracker TCP publisher for testing Grasshopper components."
    )
    parser.add_argument(
        "--host",
        default="127.0.0.1",
        help="IP address to bind to (default: 127.0.0.1).",
    )
    parser.add_argument(
        "--port",
        type=int,
        default=7000,
        help="TCP port to listen on (default: 7000).",
    )
    parser.add_argument(
        "--rate",
        type=int,
        default=90,
        help="Broadcast frequency in Hz (default: 90).",
    )
    parser.add_argument(
        "--mode",
        choices=["circle", "spiral", "figure8", "wave", "static"],
        default="circle",
        help="Simulated trajectory: circle, spiral, figure8, wave, static (default: circle).",
    )
    parser.add_argument(
        "--speed",
        type=float,
        default=1.0,
        help="Speed multiplier for motion (default: 1.0).",
    )
    parser.add_argument(
        "--radius",
        type=float,
        default=0.5,
        help="Trajectory radius in meters (default: 0.5m = 500mm in Rhino).",
    )
    parser.add_argument(
        "--id",
        default="LHR-SIMULATED",
        help="Simulated Tracker Serial ID (default: LHR-SIMULATED).",
    )

    args = parser.parse_args()

    dummy_server = DummyViveServer(
        host=args.host,
        port=args.port,
        rate_hz=args.rate,
        mode=args.mode,
        speed=args.speed,
        radius=args.radius,
        tracker_id=args.id,
    )

    try:
        asyncio.run(dummy_server.run())
    except KeyboardInterrupt:
        print("\n[!] Dummy Vive Sender stopped.")
        sys.exit(0)


if __name__ == "__main__":
    main()
