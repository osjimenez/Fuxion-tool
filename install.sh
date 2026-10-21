#!/bin/sh
# Installs fx, the tool of Fuxion's spaces, from the releases of Fuxion-tool on GitHub (plan S):
#
#   curl -fsSL https://raw.githubusercontent.com/osjimenez/Fuxion-tool/main/install.sh | sh
#
# With options (a version, another folder):
#
#   curl -fsSL https://raw.githubusercontent.com/osjimenez/Fuxion-tool/main/install.sh | sh -s -- --version 0.7.0 --install-dir ~/bin
#
# It downloads the archive of this platform (linux-x64, linux-arm64, osx-arm64 or osx-x64) and SHA256SUMS, checks the
# archive and puts fx in the folder (~/.local/bin by default). Running it again updates fx. Needs curl and tar.
set -eu

repository=osjimenez/Fuxion-tool
version=""
dir="$HOME/.local/bin"
while [ $# -gt 0 ]; do
	case "$1" in
		--version) version=$2; shift 2 ;;
		--install-dir) dir=$2; shift 2 ;;
		*) echo "Unknown option: $1 (--version <X.Y.Z>, --install-dir <folder>)" >&2; exit 2 ;;
	esac
done

# the platform
case "$(uname -s)" in
	Linux) os=linux ;;
	Darwin) os=osx ;;
	*) echo "fx has no build for $(uname -s)." >&2; exit 1 ;;
esac
case "$(uname -m)" in
	x86_64 | amd64) arch=x64 ;;
	arm64 | aarch64) arch=arm64 ;;
	*) echo "fx has no build for $(uname -m)." >&2; exit 1 ;;
esac
rid="$os-$arch"

# the release: the latest, or the one of the version asked for (its tag is release/<version>)
if [ -n "$version" ]; then
	api="https://api.github.com/repos/$repository/releases/tags/release/$version"
else
	api="https://api.github.com/repos/$repository/releases/latest"
fi
release=$(curl -fsSL -H "Accept: application/vnd.github+json" "$api")
found=$(printf '%s\n' "$release" | sed -n 's/.*"tag_name": *"release\/\([^"]*\)".*/\1/p' | head -n 1)
name="fx-$found-$rid.tar.gz"
url_of() { printf '%s\n' "$release" | sed -n "s/.*\"browser_download_url\": *\"\([^\"]*\/$1\)\".*/\1/p" | head -n 1; }
archive_url=$(url_of "$name")
sums_url=$(url_of SHA256SUMS)
if [ -z "$found" ] || [ -z "$archive_url" ] || [ -z "$sums_url" ]; then
	echo "The release has no $name or SHA256SUMS." >&2; exit 1
fi

temp=$(mktemp -d)
trap 'rm -rf "$temp"' EXIT
echo "Downloading fx $found ($rid)..."
curl -fsSL -o "$temp/$name" "$archive_url"
curl -fsSL -o "$temp/SHA256SUMS" "$sums_url"

# the archive must be the one of SHA256SUMS ("<hash>  <file>" per line)
expected=$(awk -v f="$name" '{ n = $2; sub(/^\*/, "", n); if (n == f) { print $1; exit } }' "$temp/SHA256SUMS")
if command -v sha256sum >/dev/null 2>&1; then
	actual=$(sha256sum "$temp/$name" | awk '{ print $1 }')
else
	actual=$(shasum -a 256 "$temp/$name" | awk '{ print $1 }')
fi
if [ -z "$expected" ] || [ "$actual" != "$expected" ]; then
	echo "The checksum of $name does not match SHA256SUMS: not installed." >&2; exit 1
fi

# only fx goes to the folder (the license stays in the archive: the folder is shared)
mkdir -p "$dir"
tar -xzf "$temp/$name" -C "$temp" fx
mv -f "$temp/fx" "$dir/fx"
chmod +x "$dir/fx"

case ":$PATH:" in
	*":$dir:"*) ;;
	*) echo "$dir is not in the PATH: add it to your shell profile (export PATH=\"$dir:\$PATH\")." ;;
esac
"$dir/fx" --version
echo "fx $found installed in $dir."
