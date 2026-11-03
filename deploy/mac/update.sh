#!/bin/sh
# Updates TMSpline's hosted service on the home Mac from the archive deploy.ps1 copied to /tmp: syncs the app into
# /opt/tmspline/app, signs it and restarts the service (the sudo rule from setup.sh allows exactly that restart).
# Data, releases and site.json are left alone.
set -eu

ARCHIVE=/tmp/tmspline-app.tar.gz
WORK=/tmp/tmspline-update

rm -rf "$WORK"
mkdir "$WORK"
tar -xzf "$ARCHIVE" -C "$WORK"
rsync -a --delete "$WORK/app/" /opt/tmspline/app/
chmod +x /opt/tmspline/app/TmSpline.Cloud
# Apple Silicon only runs signed executables: an ad-hoc signature is enough.
codesign --force -s - /opt/tmspline/app/TmSpline.Cloud
rm -rf "$WORK" "$ARCHIVE"

sudo -n /bin/launchctl kickstart -k system/de.pixartist.tmspline
for i in 1 2 3 4 5 6 7 8 9 10; do
  if curl -fsS -o /dev/null http://127.0.0.1:5090/site.css; then
    echo "TMSpline restarted and answering."
    exit 0
  fi
  sleep 1
done
echo "TMSpline did not answer after the restart; see /opt/tmspline/logs/tmspline.log" >&2
exit 1
