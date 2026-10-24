# StellarDrive dedicated server in a container: Wine + sdsm. The game folder (with MelonLoader and the mods) is
# mounted at /game (read-only); the server's own folder, world, settings and logs live in /data.
#   docker build -t stellardrive-server .
#   docker run -d --name sd-hub -p 7777:7777/udp -v ~/stellardrive/game:/game:ro -v ~/stellardrive/data:/data \
#              -e SD_NAME=hub -e SD_PORT=7777 stellardrive-server
FROM debian:bookworm-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends wine wine64 xvfb xauth ca-certificates procps tini \
 && rm -rf /var/lib/apt/lists/* \
 && useradd -m -u 1000 stellar

COPY sdsm entrypoint.sh /usr/local/bin/
COPY DedicatedServer-*.dll /usr/local/bin/
RUN chmod +x /usr/local/bin/sdsm /usr/local/bin/entrypoint.sh

USER stellar
ENV WINEDEBUG=-all WINEPREFIX=/home/stellar/.wine SDSM_HOME=/data/sdsm
# Wine's folder made once, in the image
RUN wineboot -i && wineserver -w

VOLUME /data
WORKDIR /data
# tini passes docker stop (SIGTERM) on, also while the server is first being made
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
