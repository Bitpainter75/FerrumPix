#!/bin/sh
set -eu

app_bundle="${1:-}"
if [ -z "$app_bundle" ]; then
    echo "usage: $0 /path/to/FerrumPix.app" >&2
    exit 2
fi

info_plist="$app_bundle/Contents/Info.plist"
if [ ! -f "$info_plist" ]; then
    echo "FAIL: missing $info_plist" >&2
    exit 1
fi

icon_name=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIconFile" "$info_plist" 2>/dev/null || true)
if [ -z "$icon_name" ]; then
    echo "FAIL: CFBundleIconFile is missing from $info_plist" >&2
    exit 1
fi

case "$icon_name" in
    *.icns) icon_file="$icon_name" ;;
    *) icon_file="$icon_name.icns" ;;
esac

icon_path="$app_bundle/Contents/Resources/$icon_file"
if [ ! -f "$icon_path" ]; then
    echo "FAIL: declared app icon is missing: $icon_path" >&2
    exit 1
fi

icon_format=$(sips -g format "$icon_path" 2>/dev/null | awk '/format:/ { print $2 }')
if [ "$icon_format" != "icns" ]; then
    echo "FAIL: declared app icon is not a valid ICNS file: $icon_path" >&2
    exit 1
fi

if ! codesign --verify --deep --strict "$app_bundle"; then
    echo "FAIL: app bundle code signature is invalid: $app_bundle" >&2
    exit 1
fi

echo "PASS: $app_bundle declares and contains $icon_file and has a valid signature"
