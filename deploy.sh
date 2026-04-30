#!/bin/bash
set -ex
SCRIPT_DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )

RELEASE=false
for arg in "$@"; do
    [[ "$arg" == "--release" ]] && RELEASE=true
done

if $RELEASE; then
    DIST_DIR=$SCRIPT_DIR/MoreTino/dist
    dotnet publish $SCRIPT_DIR/MoreTino/MoreTino.csproj --nologo -v quiet -c Release /p:ModsPath=$DIST_DIR/
    VERSION=$(python3 -c "import json; print(json.load(open('$SCRIPT_DIR/MoreTino/MoreTino.json'))['version'])")
    ARCHIVE=$SCRIPT_DIR/MoreTino-${VERSION}.zip
    rm -f "$ARCHIVE"
    (cd "$DIST_DIR" && zip -r "$ARCHIVE" MoreTino)
    echo "Release archive: $ARCHIVE"
else
    dotnet build $SCRIPT_DIR/MoreTino/MoreTino.csproj --nologo -v quiet
fi
