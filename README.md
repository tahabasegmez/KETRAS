# Ketras

**An open-source ground control station for fixed-wing MAVLink drones — plan, check, fly and set up your vehicle
from one desktop app on Windows and Linux.**

Ketras talks MAVLink directly to the autopilot (PX4 first), plans missions over real terrain, warns you before a
flight goes wrong, and can start a PX4 simulator for you so you can try everything without hardware.

> **Status: pre-release.** Every screen is built and tested against a simulated vehicle and PX4 SITL, but there
> are no releases yet. Expect rough edges and breaking changes.

---

## Features

### Plan
- Missions and geofences on a satellite or street map, with or without a vehicle connected.
- Takeoff, waypoints, loiters, speed changes, jumps and a fixed-wing **landing pattern** you drag into place.
- Altitudes relative to home, above sea level, or **above terrain** (Copernicus DEM, downloaded on demand).
- Live checks while you edit: terrain clearance, glide slopes, turn and loiter limits, headwind, and whether the
  **battery lasts** for the whole mission and the way home.
- A flight estimate (time, distance, energy margin) for the whole plan and for each item.
- Open and save `.plan` files, so plans move to and from other ground stations.

### Fly
- The vehicle live on the map and in a **3D view** over the terrain, with traffic, zones and the mission.
- A compact telemetry panel: artificial horizon, altitudes (sea level, home, ground), speeds, throttle, battery.
  Expand it for navigation (distance and time to the next waypoint and to the end, altitude and cross-track error)
  and power (voltage, current, mAh used, energy margin).
- A command bar (arm, takeoff, modes, return…) that tells you **why** a command is refused, using PX4's own
  health and arming report.
- In-flight warnings that only fire when a problem holds: off altitude, off track, full throttle and too slow,
  not enough battery to finish and return, GPS, estimator, vibration, link loss.
- Weather from the vehicle (wind, temperature, humidity, pressure) and ADS-B traffic.

### Set up the vehicle
- A summary with a setup checklist and a **Ready to fly** list (Pass / Warn / Fail).
- Sensor calibration, radio calibration with flight modes and switches, battery and failsafes.
- Actuators built from the firmware's own metadata, with a safety-gated motor and servo test.
- Every parameter with search, groups, favourites and `.params` import and export. Edits are staged and written
  together with **Apply**.

### Simulate
- Start **PX4 SITL** from the app, in Docker, on Windows and Linux. Ketras builds the simulator image itself.
- Choose the world (flat or real terrain around any home), simulation speed, wind and PX4 version, and open the
  Gazebo window when you want to watch.

### Extend
- Plugins add commands, station actions and environment providers (traffic, zones, points of interest, weather).
- Vehicle-side ROS 2 systems are reached through rosbridge.
- The libraries work on their own: use the MAVLink stack, the station logic or the map control in your own app.

---

## Getting started

### Requirements
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows 10/11 or a desktop Linux
- For the simulator: [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows) or Docker Engine
  (Linux), and about 15 GB of free disk space for the image

### Build and run

```sh
git clone https://github.com/tahabasegmez/KETRAS.git
cd KETRAS
dotnet run --project src/Ketras.App
```

To build and run the tests:

```sh
dotnet build Ketras.slnx
dotnet test Ketras.slnx
```

---

## Usage

### Connect to a vehicle
1. Open **Vehicle link** in the activity bar on the left.
2. Add a link:
   - **UDP** — leave the host empty to listen on port `14550`, where PX4 sends its ground station link by default.
   - **TCP** — `host:port`.
   - **Serial** — the port and baud rate of your telemetry radio or USB cable.
3. Press **Connect**. The status page shows each step (link open, heartbeat, parameters, metadata) and the link
   log shows what went wrong if it stops.

Every session is recorded as a `.tlog` telemetry log you can replay in common MAVLink tools.

### Try it without hardware (PX4 SITL)
1. Start Docker.
2. Open **Vehicle link → SITL**. The checklist shows what is missing; press **Build environment** once (this takes
   a while the first time).
3. Pick a world and a home, press **Start**. Ketras connects to the simulator by itself.
4. Optionally press **Open Gazebo** to watch the aircraft.

### Plan and fly a mission
1. Open **Planning**. Click on the map to add a takeoff, then waypoints; finish with a landing pattern or return.
2. Fix anything listed in the findings: they point at the item and say what is wrong.
3. Enter your cruise current on **Vehicle setup → Power** to get an energy estimate.
4. **Upload** the mission, check **Vehicle setup → Summary → Ready to fly**, then arm and start the mission from
   the command bar.
5. Follow the flight on the map or in **3D**; expand the telemetry panel for navigation and power.

### Set up a vehicle
Open **Vehicle setup** while connected. Work through the checklist on the Summary page; each item opens its page.
Parameter edits are collected and written when you press **Apply**.

---

## Configuration

| What | Windows | Linux |
| --- | --- | --- |
| Settings and link profiles | `%APPDATA%\Ketras` | `~/.config/Ketras` |
| Logs, telemetry logs, map tiles, terrain, vehicle metadata | `%LOCALAPPDATA%\Ketras` | `~/.local/share/Ketras` |

Deployment options (MAVLink system id, rosbridge, terrain, telemetry logging, zone files, plugins) go in an
`appsettings.json` in the settings folder. See [src/Ketras.App/appsettings.json](src/Ketras.App/appsettings.json)
for the keys and defaults.

---

## Project layout

```
src/
  Ketras.App                 the desktop application (Avalonia, MVVM)
  Ketras.Station             all ground station behaviour, no UI
  Ketras.Mavlink             MAVLink framing, transports, vehicle sessions and protocols
  Ketras.Mavlink.Testing     a fake vehicle for tests
  Ketras.Model               plain records and pure rules (missions, fences, limits)
  Ketras.Terrain             Copernicus DEM tiles
  Ketras.Rosbridge           rosbridge client
  Ketras.Simulation.Px4Sitl  PX4 SITL in Docker
  Ketras.Controls.Map        map control with mission editing
  Ketras.Controls.Scene3D    3D view over terrain (OpenGL)
  Ketras.Controls.Instruments attitude indicator and wind compass
  Ketras.Theme               colours, sizes and control styles
plugins/   Ketras.Plugins.Template — start here for your own plugin
samples/   Ketras.Samples.MapOnly — the map control used on its own
tools/     MAVLink code generator, SITL smoke run
tests/     unit, station end-to-end, headless UI and architecture tests
```

The station logic has no UI dependency, and the controls have no dependency on the station, so each part can be
reused alone. Architecture tests enforce these boundaries.

---

## Built with

[.NET 10](https://dotnet.microsoft.com/) · [Avalonia](https://avaloniaui.net/) ·
[Mapsui](https://mapsui.com/) · [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) ·
[Lucide icons](https://lucide.dev/) · [PX4](https://px4.io/) · [Gazebo](https://gazebosim.org/) ·
[Copernicus DEM](https://dataspace.copernicus.eu/explore-data/data-collections/copernicus-contributing-missions/collections-description/COP-DEM)

## Contributing

There are some issues with the architecture still, after solving these issues I will update this section.

## License

Ketras is free software under the [GNU General Public License v3.0 or later].
