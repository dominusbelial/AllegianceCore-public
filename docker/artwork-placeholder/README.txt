Artwork placeholder for docker-compose local trial
==================================================

The server requires a real artwork corpus at runtime (its config's `artPath`
must exist at boot, and must contain the static core and map files the config
names). The corpus is a large, separate, external checkout that is deliberately
not in the git repo and never baked into the image.

Two ways to use this directory for `docker-compose up`:

1. Drop a small representative subset of your real Artwork here, containing at
   least:
     - PCore001.igc          (the static core named in docker/compose.server.json)
     - XXV_simple.igc        (the map named in docker/compose.server.json)
   Docker bind-mounts this directory read-only into the container at /artwork.

2. Better for a real trial: leave this placeholder empty and edit
   docker-compose.yml to point the `server` volume at your real Artwork path,
   e.g.
       - /home/you/Allegiance-Artwork:/artwork:ro
   (docker-compose.yml deliberately uses the placeholder, not a hardcoded
   personal path — see P175.)

Either way, `docker/compose.server.json` sets `artPath` to `/artwork`, which is
where the container sees the mount.
