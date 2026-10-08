# The PX4 SITL environment: Ubuntu 22.04, PX4 at a release tag built for SITL (px4_sitl_default), Gazebo
# Harmonic (installed by PX4's own Tools/setup/ubuntu.sh), and the Python packages the terrain worlds need.
# The station's scripts are not baked in: they are mounted at /opt/ketras when a container starts, so a script
# change never needs a rebuild. Built by the station (SITL page, "Build environment") as
# ketras/px4-sitl:<PX4 version>:  docker build -t ketras/px4-sitl:v1.17.0 --build-arg PX4_VERSION=v1.17.0 - < Dockerfile
FROM ubuntu:22.04

ARG PX4_VERSION=v1.17.0
ENV DEBIAN_FRONTEND=noninteractive LANG=C.UTF-8 LC_ALL=C.UTF-8

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git sudo lsb-release wget curl gnupg tzdata \
      python3 python3-pip python3-numpy python3-pil \
 && rm -rf /var/lib/apt/lists/*

# A shallow clone at the tag; submodules shallow where their commit allows it, in full otherwise.
RUN git clone --depth 1 --branch "${PX4_VERSION}" https://github.com/PX4/PX4-Autopilot.git /opt/PX4-Autopilot \
 && cd /opt/PX4-Autopilot \
 && (git submodule update --init --recursive --depth 1 --jobs 8 || git submodule update --init --recursive --jobs 8)

# Gazebo Harmonic and the SITL build dependencies; no NuttX toolchain (no hardware builds here).
RUN bash /opt/PX4-Autopilot/Tools/setup/ubuntu.sh --no-nuttx \
 && rm -rf /var/lib/apt/lists/*

# Compile jobs sized to the memory (about 1.5 GB each, Gazebo's plugins need it), not to the cores: Docker's VM on
# Windows gets half the RAM, and running out of it hangs the whole engine.
RUN jobs=$(awk '/MemAvailable/ { print int($2 / 1500000) }' /proc/meminfo) \
 && jobs=$(( jobs < 1 ? 1 : jobs > $(nproc) ? $(nproc) : jobs )) \
 && echo "Compiling with $jobs jobs" \
 && make -C /opt/PX4-Autopilot px4_sitl_default -j"$jobs" \
 && rm -rf /root/.ccache

ENV KETRAS_PX4_BUILD=/opt/PX4-Autopilot/build/px4_sitl_default KETRAS_SITL_DATA=/data HOME=/tmp
LABEL org.ketras.px4-version="${PX4_VERSION}"
WORKDIR /data
