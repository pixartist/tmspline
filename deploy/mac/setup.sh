#!/bin/sh
# One-time setup of TMSpline's hosted service on the home Mac. Run from the folder deploy.ps1 copied over
# (~/tmspline-deploy, with app/ and the plist next to this script):  sudo sh ~/tmspline-deploy/setup.sh
#
# - service account _tmspline (hidden, no shell, no home)
# - /opt/tmspline/app and /opt/tmspline/releases owned by the admin user (updates need no sudo),
#   /opt/tmspline/data (database, login keys) and /opt/tmspline/logs owned by _tmspline
# - LaunchDaemon de.pixartist.tmspline, and a sudo rule that lets the admin user restart exactly that service
#   without a password (what deploy.ps1 does after an update)
set -eu

SRC="$(cd "$(dirname "$0")" && pwd)"
ADMIN="${SUDO_USER:-finn}"
LABEL=de.pixartist.tmspline

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo." >&2; exit 1; }
[ -x "$SRC/app/TmSpline.Cloud" ] || chmod +x "$SRC/app/TmSpline.Cloud"

if ! dscl . -read /Users/_tmspline >/dev/null 2>&1; then
  uid=452
  while dscl . -list /Users UniqueID | awk '{print $2}' | grep -qx "$uid"; do uid=$((uid + 1)); done
  dscl . -create /Users/_tmspline
  dscl . -create /Users/_tmspline UniqueID "$uid"
  dscl . -create /Users/_tmspline PrimaryGroupID 20
  dscl . -create /Users/_tmspline UserShell /usr/bin/false
  dscl . -create /Users/_tmspline NFSHomeDirectory /var/empty
  dscl . -create /Users/_tmspline RealName "TMSpline service"
  dscl . -create /Users/_tmspline IsHidden 1
  echo "Created service account _tmspline (uid $uid)"
fi

mkdir -p /opt/tmspline/app /opt/tmspline/releases /opt/tmspline/data /opt/tmspline/logs
chown "$ADMIN":staff /opt/tmspline /opt/tmspline/app /opt/tmspline/releases
chown _tmspline:staff /opt/tmspline/data /opt/tmspline/logs
chmod 755 /opt/tmspline /opt/tmspline/app /opt/tmspline/releases /opt/tmspline/logs
chmod 700 /opt/tmspline/data

rsync -a --delete "$SRC/app/" /opt/tmspline/app/
chown -R "$ADMIN":staff /opt/tmspline/app
chmod +x /opt/tmspline/app/TmSpline.Cloud
# Apple Silicon only runs signed executables: an ad-hoc signature is enough.
codesign --force -s - /opt/tmspline/app/TmSpline.Cloud

install -m 644 -o root -g wheel "$SRC/$LABEL.plist" /Library/LaunchDaemons/$LABEL.plist

RULE=/etc/sudoers.d/tmspline
echo "$ADMIN ALL=(root) NOPASSWD: /bin/launchctl kickstart -k system/$LABEL" > "$RULE.tmp"
chmod 440 "$RULE.tmp"
visudo -cf "$RULE.tmp" >/dev/null && mv "$RULE.tmp" "$RULE"

launchctl bootout system/$LABEL 2>/dev/null || true
launchctl bootstrap system /Library/LaunchDaemons/$LABEL.plist
sleep 3
if curl -fsS -o /dev/null http://127.0.0.1:5090/site.css; then
  echo "TMSpline is running on :5090."
else
  echo "TMSpline did not answer yet; see /opt/tmspline/logs/tmspline.log" >&2
fi
