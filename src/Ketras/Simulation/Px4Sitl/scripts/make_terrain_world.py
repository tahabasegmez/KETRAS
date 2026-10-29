#!/usr/bin/env python3
"""Builds a low-resolution Gazebo (Harmonic) terrain world around a point from Copernicus DEM GLO-30.

Output (in --out/<name>_<quality>/): terrain.obj/.mtl (one mesh for collision and visual), texture.png
(height tint + hillshade), <name>.sdf. The world origin is the given point, and its geodetic elevation is the DEM height there,
so the vehicle's GPS altitude agrees with the terrain data the host checks against. A strip along
--heading is flattened to the origin height so a fixed-wing can take off from the spawn point.

The DEM cells are read from --dem-dir (Ketras.Terrain's cache, same names) and downloaded there
when missing. Heights: metres above the EGM2008 geoid, a surface model (buildings and trees
included). Attribution required by the data licence: "produced using Copernicus WorldDEM-30 (c) DLR
e.V. 2010-2014 and (c) Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the
European Union and ESA; all rights reserved".
"""
import argparse
import math
import os
import struct
import sys
import urllib.error
import urllib.request
import zlib

import numpy as np
from PIL import Image

BUCKET = "https://copernicus-dem-30m.s3.amazonaws.com"
METERS_PER_DEGREE = 111195.0

# Heightmap side (2^n + 1) and texture side per quality; over the default 10 km that is 78 / 39 / 20 m spacing.
QUALITY = {"low": (129, 1024), "medium": (257, 2048), "high": (513, 2048)}


def object_name(lat, lon):
    return "Copernicus_DSM_COG_10_{}{:02d}_00_{}{:03d}_00_DEM".format(
        "N" if lat >= 0 else "S", abs(lat), "E" if lon >= 0 else "W", abs(lon))


class Cell:
    """One Copernicus GeoTIFF: classic little-endian TIFF, float32, DEFLATE + predictor 3, internal tiles."""

    def __init__(self, path):
        self.data = open(path, "rb").read()
        if self.data[:4] != b"II*\x00":
            raise ValueError(f"{path}: not a little-endian classic TIFF")
        tags = self._ifd(struct.unpack("<I", self.data[4:8])[0])
        self.width, self.height = tags[256][0], tags[257][0]
        self.tile_w, self.tile_h = tags[322][0], tags[323][0]
        if tags[258][0] != 32 or tags[339][0] != 3 or tags[259][0] not in (8, 32946) or tags.get(317, [1])[0] != 3:
            raise ValueError(f"{path}: unexpected sample format or compression")
        self.offsets, self.counts = tags[324], tags[325]
        self.lon_step, self.lat_step = tags[33550][0], tags[33550][1]
        tie = tags[33922]
        # Copernicus files are PixelIsPoint: the tiepoint is the centre of pixel (0,0).
        self.lon0 = tie[3] - tie[0] * self.lon_step
        self.lat0 = tie[4] + tie[1] * self.lat_step
        self.tiles = {}

    def _ifd(self, offset):
        count = struct.unpack("<H", self.data[offset:offset + 2])[0]
        tags = {}
        for i in range(count):
            entry = offset + 2 + i * 12
            tag, typ, n = struct.unpack("<HHI", self.data[entry:entry + 8])
            fmt = {3: ("H", 2), 4: ("I", 4), 12: ("d", 8), 16: ("Q", 8)}.get(typ)
            if fmt is None:
                continue
            size = fmt[1] * n
            at = entry + 8 if size <= 4 else struct.unpack("<I", self.data[entry + 8:entry + 12])[0]
            tags[tag] = struct.unpack("<" + fmt[0] * n, self.data[at:at + size])
        return tags

    def _tile(self, index):
        if index not in self.tiles:
            raw = zlib.decompress(self.data[self.offsets[index]:self.offsets[index] + self.counts[index]])
            rows = np.frombuffer(raw, dtype=np.uint8).reshape(self.tile_h, 4 * self.tile_w)
            rows = np.cumsum(rows, axis=1, dtype=np.uint8)  # undo horizontal differencing (mod 256)
            planes = rows.reshape(self.tile_h, 4, self.tile_w)  # byte planes, most significant first
            little = np.stack([planes[:, 3], planes[:, 2], planes[:, 1], planes[:, 0]], axis=-1)
            self.tiles[index] = np.ascontiguousarray(little).view("<f4").reshape(self.tile_h, self.tile_w)
        return self.tiles[index]

    def pixels(self, rows, cols):
        rows = np.clip(rows, 0, self.height - 1)
        cols = np.clip(cols, 0, self.width - 1)
        across = (self.width + self.tile_w - 1) // self.tile_w
        index = (rows // self.tile_h) * across + cols // self.tile_w
        out = np.empty(rows.shape, dtype=np.float64)
        for tile in np.unique(index):
            mask = index == tile
            out[mask] = self._tile(int(tile))[rows[mask] % self.tile_h, cols[mask] % self.tile_w]
        return out

    def heights(self, lat, lon):
        y = (self.lat0 - lat) / self.lat_step
        x = (lon - self.lon0) / self.lon_step
        r0 = np.floor(y).astype(np.int64)
        c0 = np.floor(x).astype(np.int64)
        fy, fx = y - r0, x - c0
        h00, h01 = self.pixels(r0, c0), self.pixels(r0, c0 + 1)
        h10, h11 = self.pixels(r0 + 1, c0), self.pixels(r0 + 1, c0 + 1)
        return (h00 * (1 - fx) + h01 * fx) * (1 - fy) + (h10 * (1 - fx) + h11 * fx) * fy


def load_cell(dem_dir, lat, lon):
    name = object_name(lat, lon)
    path = os.path.join(dem_dir, name + ".tif")
    if not os.path.exists(path):
        print(f"downloading {name} (~40 MB)")
        try:
            with urllib.request.urlopen(f"{BUCKET}/{name}/{name}.tif", timeout=120) as response, open(path + ".part", "wb") as out:
                out.write(response.read())
        except urllib.error.HTTPError as error:
            sys.exit(f"{name}: HTTP {error.code} (not published: open water or a country missing from the release)")
        os.replace(path + ".part", path)
    return Cell(path)


def sample(dem_dir, lat, lon):
    """Bilinear DEM heights for arrays of points, from as many cells as they touch."""
    out = np.full(lat.shape, np.nan)
    cell_lat = np.ceil(lat).astype(np.int64) - 1  # a cell's file holds its north edge, not its south edge
    cell_lon = np.floor(lon).astype(np.int64)
    for la, lo in set(zip(cell_lat.ravel().tolist(), cell_lon.ravel().tolist())):
        mask = (cell_lat == la) & (cell_lon == lo)
        out[mask] = load_cell(dem_dir, la, lo).heights(lat[mask], lon[mask])
    return out


def write_mesh(path, east, north, height, size):
    """Triangle mesh of the height grid (Wavefront OBJ + MTL with texture.png), in world coordinates."""
    n = height.shape[0]
    step = size / (n - 1)
    gy, gx = np.gradient(height, step)  # rows run north to south, so gy is the southward slope
    normals = np.dstack([-gx, gy, np.ones_like(height)])
    normals /= np.linalg.norm(normals, axis=2, keepdims=True)
    rows, cols = np.meshgrid(np.arange(n), np.arange(n), indexing="ij")
    u = cols / (n - 1)
    v = 1 - rows / (n - 1)  # image row 0 (north) is v = 1
    mtl = os.path.splitext(path)[0] + ".mtl"
    with open(mtl, "w") as f:
        f.write("newmtl terrain\nKa 1 1 1\nKd 1 1 1\nKs 0 0 0\nmap_Kd texture.png\n")
    with open(path, "w") as f:
        f.write(f"mtllib {os.path.basename(mtl)}\nusemtl terrain\n")
        f.write("".join(f"v {x:.2f} {y:.2f} {z:.2f}\n" for x, y, z in zip(east.ravel(), north.ravel(), height.ravel())))
        f.write("".join(f"vt {a:.5f} {b:.5f}\n" for a, b in zip(u.ravel(), v.ravel())))
        f.write("".join(f"vn {a:.4f} {b:.4f} {c:.4f}\n" for a, b, c in normals.reshape(-1, 3)))
        faces = []
        for r in range(n - 1):
            for c in range(n - 1):
                a, b, d, e = r * n + c + 1, (r + 1) * n + c + 1, r * n + c + 2, (r + 1) * n + c + 2
                # counter-clockwise seen from above: north-west, south-west, north-east / south-west, south-east, north-east
                faces.append(f"f {a}/{a}/{a} {b}/{b}/{b} {d}/{d}/{d}\nf {b}/{b}/{b} {e}/{e}/{e} {d}/{d}/{d}\n")
        f.write("".join(faces))


def runway_weight(east, north, args):
    """1 on the flattened runway strip, 0 away from it, blended over one runway width on each side."""
    h = math.radians(args.heading)
    along = east * math.sin(h) + north * math.cos(h)
    across = np.abs(east * math.cos(h) - north * math.sin(h))
    blend = args.runway_width
    inside_along = np.clip(np.minimum(along + 150, args.runway - along) / blend, 0, 1)
    inside_across = np.clip((args.runway_width / 2 + blend - across) / blend, 0, 1)
    return inside_along * inside_across


def write_texture(path, args, t, origin):
    """
    Everything that shows relief is baked into one image, so the GPU draws a single textured mesh:
    - heights sampled from the DEM at the texture's own resolution (finer than the mesh grid);
    - hillshade from a low north-west sun with exaggerated slopes, plus darkening of steep ground;
    - contour lines every 20 m (thin) and 100 m (strong), their width set from the local slope;
    - a faint 250 m grid, which shows distance and scale when flying low.
    """
    half = args.size / 2
    step = args.size / t
    coords = np.linspace(-half + step / 2, half - step / 2, t)
    east, north = np.meshgrid(coords, coords[::-1])
    lat = args.lat + north / METERS_PER_DEGREE
    lon = args.lon + east / (METERS_PER_DEGREE * math.cos(math.radians(args.lat)))
    weight = runway_weight(east, north, args)
    height = (sample(args.dem_dir, lat, lon) - origin) * (1 - weight)

    gy, gx = np.gradient(height, step)
    dz_east, dz_north = gx, -gy  # rows run north to south
    exaggeration = 3.0
    slope = np.arctan(exaggeration * np.hypot(dz_east, dz_north))
    aspect = np.arctan2(-dz_east, -dz_north)  # direction the slope faces
    sun_az, sun_alt = math.radians(315), math.radians(35)
    shade = np.cos(math.pi / 2 - sun_alt) * np.cos(slope) + np.sin(math.pi / 2 - sun_alt) * np.sin(slope) * np.cos(sun_az - aspect)
    shade = 0.35 + 0.75 * np.clip(shade, 0, 1)
    shade *= 1 - 0.25 * np.clip(np.degrees(np.arctan(np.hypot(dz_east, dz_north))) / 30, 0, 1)

    level = np.clip((height - height.min()) / max(height.max() - height.min(), 1), 0, 1)[..., None]
    low_col, mid_col, top_col = np.array([72, 128, 58]), np.array([160, 140, 95]), np.array([235, 232, 220])
    color = np.where(level < 0.5, low_col + (mid_col - low_col) * level * 2, mid_col + (top_col - mid_col) * (level - 0.5) * 2)
    # Light grey: PX4 points the GUI camera at the plane from 2 m away, so on the ground the runway fills the view.
    color = color * (1 - weight[..., None]) + np.array([175, 175, 170]) * weight[..., None]
    color = color * shade[..., None]

    # Contours: distance to the nearest contour height, in pixels, via the local slope.
    rise_per_px = np.hypot(gx, gy) * step + 1e-6
    for interval, darkness, width in ((20, 0.18, 0.6), (100, 0.40, 1.0)):
        off = np.abs((height / interval + 0.5) % 1 - 0.5) * interval / rise_per_px
        line = np.clip(1 - off / width, 0, 1) * (1 - weight)
        color *= (1 - darkness * line)[..., None]

    grid = 250
    gx_px = np.abs(((east + half) / grid + 0.5) % 1 - 0.5) * grid / step
    gy_px = np.abs(((north + half) / grid + 0.5) % 1 - 0.5) * grid / step
    grid_line = np.clip(1 - np.minimum(gx_px, gy_px) / 0.6, 0, 1)
    color = color * (1 - 0.22 * grid_line)[..., None] + 255 * 0.08 * grid_line[..., None]

    Image.fromarray(np.clip(color, 0, 255).astype(np.uint8)).save(path, optimize=True)


def resize(grid, size):
    """Bilinear resize of a square array to size x size."""
    src = np.linspace(0, grid.shape[0] - 1, size)
    i0 = np.clip(np.floor(src).astype(np.int64), 0, grid.shape[0] - 2)
    f = src - i0
    rows = grid[i0] * (1 - f)[:, None] + grid[i0 + 1] * f[:, None]
    return rows[:, i0] * (1 - f)[None, :] + rows[:, i0 + 1] * f[None, :]


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--lat", type=float, default=47.397971057728974)
    parser.add_argument("--lon", type=float, default=8.546163739800146)
    parser.add_argument("--quality", choices=sorted(QUALITY), default="medium",
                        help="heightmap and texture resolution preset: " + ", ".join(
                            f"{q} {p}x{p} heights / {t} px texture" for q, (p, t) in QUALITY.items()))
    parser.add_argument("--size", type=float, default=10000, help="side of the square terrain in metres")
    parser.add_argument("--pixels", type=int, help="heightmap side, 2^n + 1 (overrides --quality)")
    parser.add_argument("--heading", type=float, default=90, help="runway heading in degrees from north")
    parser.add_argument("--runway", type=float, default=800, help="flattened length ahead of the spawn point (m)")
    parser.add_argument("--runway-width", type=float, default=60)
    parser.add_argument("--dem-dir", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--name", default="ketras_terrain", help="world name; the folder is <name>_<quality>")
    args = parser.parse_args()

    preset_pixels, texture_pixels = QUALITY[args.quality]
    args.pixels = args.pixels or preset_pixels
    if (args.pixels - 1) & (args.pixels - 2) != 0:
        sys.exit("--pixels must be 2^n + 1")

    os.makedirs(args.dem_dir, exist_ok=True)
    folder = os.path.join(args.out, f"{args.name}_{args.quality}")
    os.makedirs(folder, exist_ok=True)

    n, half = args.pixels, args.size / 2
    coords = np.linspace(-half, half, n)
    east, north = np.meshgrid(coords, coords[::-1])  # image row 0 is the north edge
    lat = args.lat + north / METERS_PER_DEGREE
    lon = args.lon + east / (METERS_PER_DEGREE * math.cos(math.radians(args.lat)))
    dem = sample(args.dem_dir, lat, lon)
    if np.isnan(dem).any():
        sys.exit("DEM has no data for part of the area")

    origin = float(sample(args.dem_dir, np.array([args.lat]), np.array([args.lon]))[0])
    height = dem - origin

    # Runway: flat at the origin height, blended into the terrain over one runway width on each side.
    height = height * (1 - runway_weight(east, north, args))

    low, high = float(height.min()), float(height.max())

    write_texture(os.path.join(folder, "texture.png"), args, texture_pixels, origin)
    write_mesh(os.path.join(folder, "terrain.obj"), east, north, height, args.size)

    # One mesh for collision and visual, so the ground the plane touches is the ground its lidar sees. Gazebo
    # heightmaps did not give that: the physics ignored <pos> z (dropped spheres rested |low| metres too high),
    # the rendering ignored the model pose (the lidar, which ray-casts the rendered scene, read the ground
    # |low| metres too high), and even aligned, the two surfaces differed by up to a metre on slopes, enough
    # to put the lidar under the rendered ground. The lidar feeds PX4's terrain estimate and landing.
    sdf = f"""<?xml version="1.0" encoding="UTF-8"?>
<!-- Generated by make_terrain_world.py: {args.size / 1000:.0f} km x {args.size / 1000:.0f} km around
     {args.lat:.6f}, {args.lon:.6f}, {n}x{n} heights ({args.size / (n - 1):.0f} m spacing), heights {origin + low:.0f}-{origin + high:.0f} m AMSL,
     origin at {origin:.1f} m. Produced using Copernicus WorldDEM-30 (c) DLR e.V. 2010-2014 and (c) Airbus Defence and
     Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.
     Physics, scene, light and coordinates follow PX4's Tools/simulation/gz/worlds/default.sdf. -->
<sdf version="1.9">
  <world name="{args.name}">
    <physics type="ode">
      <max_step_size>0.004</max_step_size>
      <real_time_factor>1.0</real_time_factor>
      <real_time_update_rate>250</real_time_update_rate>
    </physics>
    <gravity>0 0 -9.8</gravity>
    <magnetic_field>6e-06 2.3e-05 -4.2e-05</magnetic_field>
    <atmosphere type="adiabatic"/>
    <scene>
      <grid>false</grid>
      <ambient>0.4 0.4 0.4 1</ambient>
      <background>0.7 0.8 0.9 1</background>
      <shadows>false</shadows>
    </scene>
    <model name="terrain">
      <static>true</static>
      <link name="link">
        <collision name="collision">
          <geometry>
            <mesh><uri>terrain.obj</uri></mesh>
          </geometry>
        </collision>
        <visual name="visual">
          <geometry>
            <mesh><uri>terrain.obj</uri></mesh>
          </geometry>
        </visual>
      </link>
    </model>
    <!-- Beyond the terrain edge: a floor at the lowest terrain height, so nothing falls forever. -->
    <model name="floor">
      <static>true</static>
      <pose>0 0 {low - 0.5:.2f} 0 0 0</pose>
      <link name="link">
        <collision name="collision">
          <geometry><plane><normal>0 0 1</normal><size>100000 100000</size></plane></geometry>
        </collision>
        <visual name="visual">
          <geometry><plane><normal>0 0 1</normal><size>100000 100000</size></plane></geometry>
          <material><ambient>0.3 0.4 0.3 1</ambient><diffuse>0.3 0.4 0.3 1</diffuse></material>
        </visual>
      </link>
    </model>
    <light name="sunUTC" type="directional">
      <pose>0 0 500 0 -0 0</pose>
      <cast_shadows>false</cast_shadows>
      <intensity>1</intensity>
      <direction>0.001 0.625 -0.78</direction>
      <diffuse>0.904 0.904 0.904 1</diffuse>
      <specular>0.271 0.271 0.271 1</specular>
    </light>
    <spherical_coordinates>
      <surface_model>EARTH_WGS84</surface_model>
      <world_frame_orientation>ENU</world_frame_orientation>
      <latitude_deg>{args.lat}</latitude_deg>
      <longitude_deg>{args.lon}</longitude_deg>
      <elevation>{origin:.2f}</elevation>
    </spherical_coordinates>
  </world>
</sdf>
"""
    with open(os.path.join(folder, args.name + ".sdf"), "w", encoding="utf-8") as f:
        f.write(sdf)

    print(f"world: {os.path.join(folder, args.name + '.sdf')}")
    print(f"origin {args.lat:.6f}, {args.lon:.6f} at {origin:.1f} m AMSL; terrain {origin + low:.0f}-{origin + high:.0f} m; "
          f"{n}x{n} heights, {args.size / (n - 1):.0f} m spacing; runway {args.runway:.0f} m at {args.heading:.0f} deg")


if __name__ == "__main__":
    main()
