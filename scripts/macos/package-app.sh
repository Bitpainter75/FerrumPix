#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)

requested_arch="${1:-$(uname -m)}"
case "$requested_arch" in
    arm64) runtime_id="osx-arm64" ;;
    x64|x86_64) runtime_id="osx-x64" ;;
    *)
        echo "unsupported macOS architecture: $requested_arch (expected arm64 or x64)" >&2
        exit 2
        ;;
esac

configuration="${CONFIGURATION:-Release}"
output_dir="${OUTPUT_DIR:-$repo_root/artifacts/macos-$requested_arch}"
app_bundle="$output_dir/FerrumPix.app"
publish_dir=$(mktemp -d "${TMPDIR:-/tmp}/ferrumpix-publish.XXXXXX")

cleanup() {
    rm -rf "$publish_dir"
}
trap cleanup EXIT HUP INT TERM

version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$repo_root/FerrumPix.vbproj" | head -n 1)
if [ -z "$version" ]; then
    echo "could not read Version from FerrumPix.vbproj" >&2
    exit 1
fi

dotnet publish "$repo_root/FerrumPix.vbproj" \
    --configuration "$configuration" \
    --runtime "$runtime_id" \
    --self-contained true \
    -p:PublishSingleFile=true \
    --output "$publish_dir"

rm -rf "$app_bundle"
mkdir -p "$app_bundle/Contents/MacOS" "$app_bundle/Contents/Resources"
cp -R "$publish_dir/." "$app_bundle/Contents/MacOS/"
cp "$script_dir/Info.plist" "$app_bundle/Contents/Info.plist"
cp "$script_dir/FerrumPix.icns" "$app_bundle/Contents/Resources/FerrumPix.icns"

/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app_bundle/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$app_bundle/Contents/Info.plist"

# FerrumPix localizes through .NET resources rather than .lproj directories.
# Advertise exactly the cultures present in Resources, so an independently
# merged localization PR is picked up automatically.
/usr/libexec/PlistBuddy -c "Add :CFBundleLocalizations array" "$app_bundle/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleLocalizations:0 string en" "$app_bundle/Contents/Info.plist"
localization_index=1
for resource_file in "$repo_root"/Resources/Strings.*.resx; do
    [ -f "$resource_file" ] || continue
    culture=$(basename "$resource_file")
    culture=${culture#Strings.}
    culture=${culture%.resx}
    case "$culture" in
        zh-CN) bundle_localization="zh-Hans" ;;
        zh-TW) bundle_localization="zh-Hant" ;;
        *) bundle_localization="$culture" ;;
    esac
    /usr/libexec/PlistBuddy \
        -c "Add :CFBundleLocalizations:$localization_index string $bundle_localization" \
        "$app_bundle/Contents/Info.plist"
    localization_index=$((localization_index + 1))
done

chmod +x "$app_bundle/Contents/MacOS/FerrumPix"

codesign --force --deep --sign - "$app_bundle"
"$script_dir/verify-app-bundle.sh" "$app_bundle"
echo "Created $app_bundle"
