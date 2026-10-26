# ENE DRAM RGB controller (SMBus)

Notes for the two "AUDA0-E6K5-0101" RGB controllers on this PC's DDR5 sticks, written for this project.

## Register access

ENE registers are 16-bit addresses behind an 8-bit SMBus device.

| Operation | SMBus transactions |
|---|---|
| select register R | write word data, command `0x00`, word = R with bytes swapped (`(R << 8 \| R >> 8) & 0xFFFF`) |
| read byte at R | select R, then read byte data, command `0x81` |
| write byte V at R | select R, then write byte data, command `0x01`, value V |
| write up to 3 bytes at R | select R, then write block data, command `0x03`, block length 1..3 |

Block writes longer than 3 bytes are not used.

## Registers

| Register | Size | Meaning |
|---|---|---|
| `0x1000` | 16 | device name, ASCII, NUL padded. This PC: `AUDA0-E6K5-0101` |
| `0x1030` | 16 | if it reads `Micron`, the stick is not this controller type: stop |
| `0x1C00` | 64 | config table. Byte `0x02` = LED count (8 on this PC) |
| `0x8020` | 1 | direct mode: 1 = host-driven colors, 0 = built-in effect |
| `0x8021` | 1 | built-in effect mode (not used) |
| `0x80A0` | 1 | apply: write `0x01`. Value `0xAA` saves to flash: **never write `0xAA`** |
| `0x80F8` | 1 | slot index (address remap) |
| `0x80F9` | 1 | new 7-bit address shifted left by 1 (address remap) |
| `0x8100` | 3 per LED | direct colors for this controller generation. LED i at `0x8100 + 3*i`, byte order **R, B, G** |

## Identity check (read-only)

A device at address A is an ENE controller when all hold:
1. read byte data commands `0xA0`..`0xAF` return `0x00`..`0x0F` in order;
2. register `0x1030` does not read `Micron`;
3. register `0x1000` reads the expected name (config `expectedName`).

## Address remap

After a full power loss every ENE stick answers at `0x77`. They must be moved to distinct addresses before use. A warm reboot keeps the moved addresses.

For this PC the targets are fixed: `0x71`, `0x73` (what OpenRGB chose; `0x70` and `0x72` are taken by other devices).

1. For each target T: if T passes the identity check, it is ready.
2. Walk DIMM slots s = 0..7 with T = the first target still missing:
   - stop if `0x77` does not pass the identity check (nothing left to move);
   - stop if T answers a quick write (address busy);
   - write `0x80F8 = s` and `0x80F9 = T << 1` to device `0x77`; wait 20 ms;
   - if T now passes the identity check, the stick in slot s moved: T is ready, move on to the next target. Otherwise slot s is empty: keep T for the next slot.

Only the stick in slot s reacts to the address write, so an empty slot moves nothing. Dual-channel boards fill slots 1 and 3, so slot 0 is usually empty.

Only `0x77` and the configured targets are ever written. Probing is quick write to those same addresses only.

## Direct color update

Init per stick: read LED count from config table byte `0x02` (clamp 1..8), write `0x8020 = 1`, write `0x80A0 = 1`.
Frame: for each LED whose color changed, write 3 bytes (R, B, G) at `0x8100 + 3*i` as one block write. No apply needed per frame.
