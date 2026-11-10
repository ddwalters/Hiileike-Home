# Hiileike Home
This website links myself to my other homelab hosted sites.

## Lore Builder

Open **Lore Builder** from the signed-in home page. The codex and its pictures
use the same login. An empty codex offers four linked sample entries.

Lore Builder stores entries and pictures in `App_Data/lore-builder`. In the
current test setup, this directory lives in the container and is reset when
the container is replaced. Set `Lore__DataDirectory` to use a different directory.
