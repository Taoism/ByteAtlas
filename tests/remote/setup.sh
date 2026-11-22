#!/bin/bash
# Test servers for ByteAtlas remote sources (run in WSL with Docker):
#   SSH 127.0.0.1:2222 (user ba / pass ba-test), rclone S3 127.0.0.1:9300 (bakey / ba-secret-key, bucket ba-test),
#   Azurite 127.0.0.1:10000 (container ba-test), WebDAV 127.0.0.1:8090 (ba / ba-test).
# All serve the same tree under ~/ba-remote-data; the containers restart with the WSL VM. ./setup.sh down removes everything.
set -e
D=$HOME/ba-remote-data
if [ "$1" = down ]; then
  docker rm -f ba-test-ssh ba-test-azurite >/dev/null 2>&1 || true
  docker rm -f ba-test-webdav ba-test-s3 >/dev/null 2>&1 || true
  exit 0
fi
rm -rf "$D"; mkdir -p "$D/docs/sub" "$D/media" "$D/empty" "$D/with space"
head -c 1000 /dev/zero > "$D/docs/a.txt"
head -c 2500 /dev/zero > "$D/docs/sub/b.log"
head -c 1048576 /dev/zero > "$D/media/big.bin"
head -c 10 /dev/zero > "$D/with space/ü n.txt"
head -c 7 /dev/zero > "$D/root.txt"
touch -d '2020-01-02 03:04:05' "$D/docs/a.txt"

if ! docker image inspect ba-test-sshd >/dev/null 2>&1; then
  docker build -t ba-test-sshd - <<'DF'
FROM debian:stable-slim
RUN apt-get update && apt-get install -y --no-install-recommends openssh-server && rm -rf /var/lib/apt/lists/* \
 && useradd -m -s /bin/bash ba && echo 'ba:ba-test' | chpasswd && mkdir -p /run/sshd && ssh-keygen -A
CMD ["/usr/sbin/sshd","-D","-e"]
DF
fi
docker rm -f ba-test-ssh ba-test-azurite >/dev/null 2>&1 || true
docker run -d --restart unless-stopped --name ba-test-ssh -p 2222:22 -v "$D:/data:ro" ba-test-sshd >/dev/null
docker run -d --restart unless-stopped --name ba-test-azurite -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0 --loose >/dev/null
docker rm -f ba-test-webdav >/dev/null 2>&1 || true
docker run -d --restart unless-stopped --name ba-test-webdav -p 127.0.0.1:8090:8080 -v "$D:/data:ro" rclone/rclone serve webdav /data --addr :8080 --user ba --pass ba-test --dir-cache-time 1s >/dev/null
docker rm -f ba-test-s3 >/dev/null 2>&1 || true
rm -rf $HOME/ba-s3; mkdir -p $HOME/ba-s3; cp -a "$D" $HOME/ba-s3/ba-test
docker run -d --restart unless-stopped --name ba-test-s3 -p 9300:9000 -v $HOME/ba-s3:/data rclone/rclone serve s3 /data --addr :9000 --dir-cache-time 1s --auth-key bakey,ba-secret-key >/dev/null
sleep 6
export RCLONE_CONFIG_BAAZ_TYPE=azureblob RCLONE_CONFIG_BAAZ_USE_EMULATOR=true RCLONE_CONFIG_BAAZ_ENDPOINT=http://127.0.0.1:10000/devstoreaccount1
rclone mkdir baaz:ba-test && rclone copy "$D" baaz:ba-test
echo ready
