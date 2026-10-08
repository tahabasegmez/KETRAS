#!/bin/bash
# PX4 SITL fixed-wing (airframe 4008 gz_advanced_plane), run inside the ketras/px4-sitl container; the pxh> console
# reads this script's stdin, so the station can send it "shutdown".
# Every start is clean: PX4 runs from the image's build in a working directory that is wiped first, so parameters,
# the stored mission and old logs never carry over.
# `px4.sh` starts PX4's flat default world; `px4.sh terrain <low|medium|high> <name>` starts the terrain world <name> built by
# make_terrain_world.py into $KETRAS_SITL_DATA/worlds. Either way the world is copied first with the wind added
# (KETRAS_WIND_SPEED m/s from KETRAS_WIND_DIRECTION degrees, where it blows from), the Gazebo server starts headless
# with it, and PX4 attaches to the running world.
# Environment: KETRAS_PX4_BUILD and KETRAS_SITL_DATA (set by the image), KETRAS_HOME_LAT / _LON / _ALT (flat world),
# KETRAS_WIND_SPEED / KETRAS_WIND_DIRECTION, PX4_SIM_SPEED_FACTOR (read by PX4 itself).
data="${KETRAS_SITL_DATA:-/data}"
build="${KETRAS_PX4_BUILD:-/opt/PX4-Autopilot/build/px4_sitl_default}"
work="$data/rootfs"
logs="$data/logs"
stamp="$(date +%Y%m%d-%H%M%S)"
mkdir -p "$logs"

if [ ! -x "$build/bin/px4" ]; then
  echo "ERROR [ketras] No PX4 SITL build at $build: rebuild the environment."
  exit 1
fi

rm -rf "$work"
mkdir -p "$work"
# px4-rc.gzsim and the model spawn read the gz environment (models, worlds, plugins, server config) from here.
cp "$build/rootfs/gz_env.sh" "$work/"
source "$work/gz_env.sh"
export HEADLESS=1
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export PX4_SIM_MODEL=gz_advanced_plane
export GZ_IP=127.0.0.1

if [ "$1" = "terrain" ]; then
  quality="${2:-medium}"
  name="${3:-ketras_terrain}"
  source_world="$data/worlds/${name}_$quality/$name.sdf"
  if [ ! -f "$source_world" ]; then
    echo "ERROR [ketras] No $quality terrain world at $source_world."
    exit 1
  fi
  # The terrain world carries its own geodetic origin (the DEM height at the spawn point); its copy stays next to
  # it so the mesh and texture paths still resolve.
  world="$(dirname "$source_world")/ketras_run.sdf"
else
  source_world="$PX4_GZ_WORLDS/default.sdf"
  world="$work/ketras_flat.sdf"
  # The flat world sits at elevation 0; the station passes the DEM height at the chosen home.
  export PX4_HOME_LAT=${KETRAS_HOME_LAT:-47.397971057728974}
  export PX4_HOME_LON=${KETRAS_HOME_LON:-8.546163739800146}
  export PX4_HOME_ALT=${KETRAS_HOME_ALT:-490.4}
fi

# Wind: Gazebo's world <wind> velocity (ENU, where the air moves to) and the WindEffects system that applies it.
speed="${KETRAS_WIND_SPEED:-0}"
direction="${KETRAS_WIND_DIRECTION:-0}"
read -r wind_east wind_north < <(awk -v s="$speed" -v d="$direction" 'BEGIN { r = d * atan2(0, -1) / 180; printf "%.3f %.3f\n", -s * sin(r), -s * cos(r) }')
wind="<wind><linear_velocity>$wind_east $wind_north 0</linear_velocity></wind>"
wind+="<plugin filename=\"gz-sim-wind-effects-system\" name=\"gz::sim::systems::WindEffects\">"
wind+="<horizontal><magnitude><time_for_rise>1</time_for_rise><sin><amplitude_percent>0</amplitude_percent><period>60</period></sin>"
wind+="<noise type=\"gaussian\"><mean>0</mean><stddev>0</stddev></noise></magnitude>"
wind+="<direction><time_for_rise>1</time_for_rise><sin><amplitude>0</amplitude><period>60</period></sin>"
wind+="<noise type=\"gaussian\"><mean>0</mean><stddev>0</stddev></noise></direction></horizontal>"
wind+="<vertical><noise type=\"gaussian\"><mean>0</mean><stddev>0</stddev></noise></vertical></plugin>"
# Gazebo loads the server config's systems (physics, scene broadcaster, sensors...) only into a world that declares
# no plugins itself; once WindEffects is in the world they have to be in it too.
if ! grep -q "<plugin" "$source_world"; then
  wind+="$(sed -n '/<plugins>/,/<\/plugins>/p' "$GZ_SIM_SERVER_CONFIG_PATH" | sed -e '/<\/\?plugins>/d' -e '/<!--.*-->/d' \
    -e 's/ entity_name="[^"]*"//' -e 's/ entity_type="[^"]*"//' | tr -d '\n')"
fi
sed -e '/<wind>/,/<\/wind>/d' -e "s|</world>|$wind</world>|" "$source_world" > "$world"
echo "INFO  [ketras] Wind $speed m/s from $direction deg (east $wind_east, north $wind_north m/s)"

name="$(grep -o -m 1 "<world name=['\"][^'\"]*" "$world" | sed "s/<world name=['\"]//")"
echo "INFO  [ketras] Starting Gazebo with $world"
stdbuf -oL -eL gz sim -r -s --verbose=1 "$world" > "$logs/gz-$stamp.log" 2>&1 &
for _ in $(seq 1 120); do
  gz topic -l 2>/dev/null | grep -q "^/world/$name/clock" && break
  sleep 1
done
if ! gz topic -l 2>/dev/null | grep -q "^/world/$name/clock"; then
  echo "ERROR [ketras] Gazebo did not start; see $logs/gz-$stamp.log"
  tail -n 20 "$logs/gz-$stamp.log"
  exit 1
fi

cd "$work" || exit 1
stdbuf -oL -eL "$build/bin/px4" -w "$work" "$build/etc" 2>&1 | tee "$logs/px4-$stamp.log"
exit 0
