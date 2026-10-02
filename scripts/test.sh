#!/bin/sh
# Ordinary Linux check. Does not run Mihomo and does not touch the host network.
set -eu
cd "$(dirname "$0")/.."
unset AUTOVPN_MIHOMO_PATH
dotnet build AutoVpn.slnx -c Release --nologo
dotnet test AutoVpn.slnx -c Release --nologo
