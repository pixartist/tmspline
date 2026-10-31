# TMSpline: editor + export server in one image. NadeoImporter runs under Wine.
# NadeoImporter is NOT included; mount the unzipped NadeoImporter_2022_07_12 folder at /opt/nadeo-importer.
#
#   docker build -t tmspline .
#   docker run -p 8080:8080 -v /path/to/NadeoImporter:/opt/nadeo-importer:ro tmspline

# ---- .NET: server + WebAssembly core -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dotnet
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/TmSpline.Wasm -c Release -o /out/wasm \
 && dotnet publish src/TmSpline.Server -c Release -o /out/server

# ---- Web editor ----------------------------------------------------------------------------------
FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
COPY --from=dotnet /out/wasm/wwwroot/_framework public/_framework
RUN npx vite build

# ---- Runtime: ASP.NET + Wine ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
RUN dpkg --add-architecture i386 \
 && apt-get update \
 && apt-get install -y --no-install-recommends wine wine64 \
 && rm -rf /var/lib/apt/lists/*

ENV WINEDEBUG=-all \
    WINEPREFIX=/home/app/.wine \
    WINEARCH=win64 \
    ASPNETCORE_URLS=http://+:8080 \
    Export__ImporterMode=Wine \
    Export__ImporterDir=/opt/nadeo-importer \
    Export__WorkDir=/tmp/tmspline-jobs

RUN mkdir -p /home/app && chown app:app /home/app
USER app
# Initialize the Wine prefix at build time so the first export is fast.
RUN wineboot --init && wineserver --wait

WORKDIR /app
COPY --from=dotnet /out/server ./
COPY --from=web /web/dist ./wwwroot
EXPOSE 8080
ENTRYPOINT ["dotnet", "TmSpline.Server.dll"]
