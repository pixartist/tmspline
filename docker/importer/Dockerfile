# Runs NadeoImporter.exe under Wine. The importer files (NadeoImporter.exe, libfbxsdk.dll,
# NadeoImporterMaterialLib.txt) are NOT part of the image; mount them at /importer.
FROM debian:bookworm-slim

RUN dpkg --add-architecture i386 \
 && apt-get update \
 && apt-get install -y --no-install-recommends wine wine64 ca-certificates \
 && rm -rf /var/lib/apt/lists/*

ENV WINEDEBUG=-all \
    WINEPREFIX=/wineprefix \
    WINEARCH=win64

# Initialize the Wine prefix at build time so jobs start fast.
RUN wineboot --init && wineserver --wait

WORKDIR /importer
ENTRYPOINT ["wine", "NadeoImporter.exe"]
