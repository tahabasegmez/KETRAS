#!/bin/bash
# The Gazebo GUI, attached to the server px4.sh started in the same container. It renders in software (Mesa
# llvmpipe): the display comes from the host (X11 on Linux, Docker Desktop's WSLg on Windows), and GPU access through
# it is not reliable. Closing the window leaves the simulation running.
export LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe GZ_IP=127.0.0.1
if [ -f "${KETRAS_SITL_DATA:-/data}/rootfs/gz_env.sh" ]; then
  source "${KETRAS_SITL_DATA:-/data}/rootfs/gz_env.sh"
fi
if ! gz topic -l 2>/dev/null | grep -qE "^/world/[^/]+/clock"; then
  echo "ERROR [ketras] The Gazebo server is not running."
  exit 1
fi
echo "INFO  [ketras] Opening the Gazebo window (software rendering)"
exec stdbuf -oL -eL gz sim -g --verbose=1
