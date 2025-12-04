#!/usr/bin/bash

for f in $(find * -type f -name '*.dds'); do
    cat <<EOM
KSPTextureLoaderTest
{
    path = $f
    kopernicus = true
    parallax = true
}
EOM
done
