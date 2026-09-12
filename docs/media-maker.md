# The Reproducible Media Maker: a folder becomes one file

A project stores names and SHA1s and never paths (docs/project.md), and miniBox
binds every mounted file to the savestate by hash. Both are the same promise:
anybody holding files matching the hashes can replay the movie. A game that was
dumped as a FOLDER - a PlayStation 3 disc, a floppy's worth of DOS - cannot make
that promise, because a directory has no hash.

So the folder becomes one file. The point is not that it becomes A file but that
it becomes the SAME file for everyone who packs it: the bytes depend on the
contents and the names, and on nothing else. The precedent is
TASEmulators/iso_maker, which drives xorriso to the same end.

**Tools > Reproducible Media Maker**, next to the cache manager. Choose the
folder, the shape, and where to write it; the bar and the current file name show
the work; Cancel stops it and leaves nothing behind.

## What is deliberately left out

Each of these is a way for the packing machine to leak into the output, and each
is closed on purpose. They are listed because forgetting one is invisible - the
image works perfectly and simply hashes differently on the next machine.

| Channel | What is written instead |
| --- | --- |
| Modification times | A fixed date: `(0 << 9) \| (1 << 5) \| 1` in DOS form, in the zip and in the FAT12 directory alike |
| Mode bits | A constant external-attributes word (`0100644`) in the zip; no Rock Ridge in the ISO, so the format cannot record a mode at all |
| Directory order | Entries sorted by the BYTES of the relative path, never by the locale's collation |
| Invented identifiers | FAT12's volume serial is zero, where DOS wrote the moment of formatting |
| Symlinks and specials | Skipped, collected with `symlink_status` rather than followed - what a link points at is not in the folder |

`mediaCollect` walks the tree and returns that sorted list; every writer takes it
already sorted and adds nothing of its own.

## The three shapes

**Stored zip (.zip).** No compression, so a core reads a member by seeking to it
exactly as it would read the file loose, at the same size on disk as the folder.
Zip64 is used per member and only where a member needs it. This writer patches
each CRC behind itself by seeking back, so it cannot hash as it writes: it sets
`hashing = false` and the digest is taken from the finished file. Hashing bytes
that are later overwritten would not be slower, it would be wrong.

**ISO 9660 + Joliet (.iso).** What the disc cores want. Two directory trees are
built over one node tree: the primary one with its short names, and the Joliet
one carrying the real names as UCS-2. That is exactly what rpcs3's
`Loader/ISO.cpp` reads - it scans descriptors in 2048-byte steps for type 1 and
type 2, decodes UCS-2 from the second, and strips a trailing `;1` or `.` from
either. Joliet's name limit is enforced by deterministic truncation, and
uniqueness settled deterministically after it.

A single file of 4 GiB or more is REFUSED by name, with the sentence saying
which file: an ISO 9660 directory record cannot describe one, and an image that
silently dropped or truncated it would be a worse outcome than not being built.
A 15 GB game made of ordinary-sized files is fine, and that is the case this was
built for.

**FAT12 floppy (.img).** 2880 sectors of 512 bytes, two copies of a 12-bit
allocation table of 9 sectors each, a root directory of exactly 224 entries, one
sector per cluster, data from sector 33. Names are 8.3, made deterministically
with `~n` counters where they collide. What does not fit is refused rather than
quietly left out.

## The PlayStation 3 exception

Sectors 0 to 15 are reserved and mean nothing to ISO 9660. A PlayStation 3 disc
keeps its encryption region table there, and rpcs3 does not treat it as
optional: it reads a big-endian region count from the first four bytes, refuses
a count below 1 as not a PS3 ISO, and the EBOOT is then never found - the user
sees only "Invalid file or folder".

An image built from a decrypted dump has exactly one region and it is not
encrypted, so that is what is written: count 1, one region ending at the last
sector, the region end at offset `12 + i * 4`. Only for an image that looks like
a PS3 disc, which is `PS3_DISC.SFB` at the root; any other console's image keeps
the zeros it has always had, because the table would mean nothing there.

## Where it lives

The packing is the engine's, because every row of that table is a correctness
question and correctness belongs where the tests are:

- `media_maker.hpp` - `MediaFormat`, `MediaEntry`, `MediaProgress`,
  `mediaCollect`, `mediaMake`, and one `mediaWrite*` per shape.
- `media_maker.cpp` - the walk, the sort, the zip writer, and the C ABI:
  `ce_media_make`, `ce_media_last_sha1`, `ce_media_last_error`.
- `media_iso.cpp`, `media_fat12.cpp` - a shape each.
- `sha1.hpp` - `Sha1Stream`, so the digest is taken as the bytes go out rather
  than by reading a 15 GB file back.

`MediaProgress` returns false to cancel, and a cancelled or failed run leaves no
output file: a half-written image that still looks like an image is worse than
no image at all. The error is a sentence; `"cancelled"` is the one the window
recognises and reports as "Cancelled; nothing was written."

The window (`tools/MediaMakerForm.cs`) chooses, shows and cancels, and decides
nothing about the bytes. The bar is repainted from the packing thread at most
every 50 ms, because a disc is millions of callbacks and a window that paints
them all is a window that stops the work.

## The proof

`test_media.cpp` holds what the format promises rather than what the code does:
the same tree packs to the same SHA1 after its timestamps, mode bits and read
order have all been disturbed; a zip round-trips; a cancel leaves nothing on
disk; progress is monotonic; an empty folder is refused; a small ISO is read
back by a Joliet reader written for the test; the PS3 region table is present
for a PS3-shaped tree and absent otherwise; and the FAT12 cases, including what
does not fit.

`MediaMakerFormTests` holds the window's rules, including a geometric check that
no label overlaps a field - the greenzone budgets dialog shipped with exactly
that bug and no test of behaviour could have seen it.
