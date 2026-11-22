#!/bin/bash
# 2100 small objects in one folder with a space and an umlaut: S3 / WebDAV paging + key encoding
d="$HOME/ba-s3/ba-test/many/ä b"; mkdir -p "$d"
for i in $(seq 1 2100); do printf x > "$d/f$i.txt"; done
cp -a "$HOME/ba-s3/ba-test/many" "$HOME/ba-remote-data/"
echo made $(ls "$d" | wc -l)
# SSH key logins: ed25519 without passphrase, RSA PEM with passphrase pp-test (copies left in ~/ba-keys)
mkdir -p ~/ba-keys && cd ~/ba-keys && rm -f ba_key ba_key.pub ba_key2 ba_key2.pub
ssh-keygen -q -t ed25519 -N "" -f ba_key && ssh-keygen -q -t rsa -b 3072 -N pp-test -m PEM -f ba_key2
cat ba_key.pub ba_key2.pub | docker exec -i ba-test-ssh bash -c "mkdir -p /home/ba/.ssh && cat > /home/ba/.ssh/authorized_keys && chown -R ba /home/ba/.ssh && chmod 700 /home/ba/.ssh && chmod 600 /home/ba/.ssh/authorized_keys"
[ -n "$1" ] && cp ba_key ba_key2 "$1"/
echo keys ok
