/* chimera/engine.h - the engine's C ABI.
 *
 * This header is a frozen spec in the same sense as the waterbox boundary: the
 * C# frontend, jaffarPlus, and anything else that links the engine compiles
 * against exactly this. Rules:
 *
 *   - Additive changes only. Removing or reshaping anything here bumps
 *     CE_ABI_VERSION, and callers check ce_abi_version() at load.
 *   - Bulk data crosses as buffers, never a call per byte.
 *   - A returned const char* is BORROWED unless the declaration says otherwise;
 *     each declaration names the call that invalidates it.
 *   - UTF-8 everywhere. NUL-terminated unless a length is passed alongside.
 */

#ifndef CHIMERA_ENGINE_H
#define CHIMERA_ENGINE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Windows: the engine's API is exported EXPLICITLY. Relying on mingw's
 * export-everything default broke the moment a vendored library declared its
 * own dllexports (cJSON did), which silently un-exported the entire ce_ ABI.
 * Explicit beats implicit here, permanently. */
#if defined(_WIN32) && defined(CHIMERA_BUILDING)
#define CE_API __declspec(dllexport)
#else
#define CE_API
#endif

#define CE_ABI_VERSION 1u

/* The ABI version this library was built as. A caller compiled against a
 * different major refuses to run rather than misbehave. */
CE_API uint32_t ce_abi_version(void);

/* Build provenance as JSON, same convention as miniBox's mb_build_info():
 * a function of the inputs only, shown by the frontend and cited by movies.
 * Static string, never invalidated. */
CE_API const char *ce_build_info(void);

/* ---- movie input log ----
 *
 * The [Input] lump of a movie: an ordered list of frame entries (the "|..|"
 * mnemonic lines), the LogKey describing the controller layout, and - when the
 * text came from a savestate - the frame number the state was taken at.
 *
 * This models the DATA. Policy (truncation on load, rerecord counting, movie
 * mode transitions) stays with the caller: it depends on session settings the
 * engine has no business knowing at this stage of the migration.
 */

typedef struct ce_movie_log ce_movie_log;

CE_API ce_movie_log *ce_movie_log_new(void);
CE_API void ce_movie_log_free(ce_movie_log *log);

/* Parses input-log text (an "Input Log.txt" lump, or the input block of a
 * savestate). Replaces the log's previous contents. Accepts LF, CRLF and CR
 * line ends. Returns 0 on success, nonzero on error (see _last_error).
 *
 * Quirk, preserved from the C# it replaces: a "Frame N" line whose N does not
 * parse as a 32-bit integer is a hard error; a text with no "Frame" line at
 * all is NOT an error - the state frame is simply absent. A "LogKey:" line
 * takes effect even mid-log, last one wins. */
CE_API int32_t ce_movie_log_parse(ce_movie_log *log, const char *text, uint64_t len);

/* The error text of the last failed call on this log, or "" if none.
 * Invalidated by the next call on the same log. */
CE_API const char *ce_movie_log_last_error(ce_movie_log *log);

CE_API int64_t ce_movie_log_count(const ce_movie_log *log);

/* The entry for one frame, exactly as it appears in the file.
 * NULL when index is out of range. Invalidated by any mutating call. */
CE_API const char *ce_movie_log_entry(const ce_movie_log *log, int64_t index);

CE_API void ce_movie_log_add(ce_movie_log *log, const char *entry);
CE_API void ce_movie_log_clear(ce_movie_log *log);
/* Drops entries at and after count, keeping the first count. */
CE_API void ce_movie_log_truncate(ce_movie_log *log, int64_t count);
/* Out-of-range indices are ignored: the log's length never changes by set. */
CE_API void ce_movie_log_set(ce_movie_log *log, int64_t index, const char *entry);
/* index may equal the count (an append); beyond that is ignored. */
CE_API void ce_movie_log_insert(ce_movie_log *log, int64_t index, const char *entry);
/* Removes [index, index+count), clamped to the log's bounds. */
CE_API void ce_movie_log_remove_range(ce_movie_log *log, int64_t index, int64_t count);
/* Replaces dst's entries and LogKey with src's. */
CE_API void ce_movie_log_assign(ce_movie_log *dst, const ce_movie_log *src);

/* Nonzero when the parsed text carried a "Frame N" line. */
CE_API int32_t ce_movie_log_has_state_frame(const ce_movie_log *log);
CE_API int32_t ce_movie_log_state_frame(const ce_movie_log *log);

/* The LogKey, or NULL when none was parsed or set.
 * Invalidated by ce_movie_log_parse and ce_movie_log_set_key. */
CE_API const char *ce_movie_log_key(const ce_movie_log *log);
CE_API void ce_movie_log_set_key(ce_movie_log *log, const char *key);

/* First frame where the two logs differ; length of the shorter one when one
 * is a prefix of the other; -1 when identical. */
CE_API int64_t ce_movie_log_divergent_point(const ce_movie_log *a, const ce_movie_log *b);

/* Renders the whole [Input] block: header line, LogKey line, one line per
 * frame, terminator line. crlf selects the line end (the format is EOL-
 * tolerant on read; writers historically used the platform's convention).
 * The LogKey line always appears; pass the generated fallback via _set_key
 * when the log has none. Returns the text and stores its byte length in
 * *len_out (may be NULL). Invalidated by any other call on the same log. */
CE_API const char *ce_movie_log_serialize(ce_movie_log *log, int32_t crlf, uint64_t *len_out);

/* ---- movie header ----
 *
 * The Header.txt lump: "Key Value" per line. Parsing keeps the FIRST
 * occurrence of a key (that is what the C# it replaces did); writing keeps
 * insertion order, which stabilises what the old Dictionary-backed writer
 * left unspecified. Values may contain spaces; keys may not.
 */

typedef struct ce_movie_header ce_movie_header;

CE_API ce_movie_header *ce_movie_header_new(void);
CE_API void ce_movie_header_free(ce_movie_header *header);

/* Replaces contents. Whitespace-only lines and lines with no value are
 * skipped; "Key  Value" parses with the separator run eaten, trailing
 * whitespace kept - the exact net48 Split(' ', 2, RemoveEmptyEntries) rules.
 * Never fails. */
CE_API void ce_movie_header_parse(ce_movie_header *header, const char *text, uint64_t len);

CE_API int64_t ce_movie_header_count(const ce_movie_header *header);

/* Borrowed; invalidated by parse/set/free. NULL when index is out of range. */
CE_API const char *ce_movie_header_key_at(const ce_movie_header *header, int64_t index);
CE_API const char *ce_movie_header_value_at(const ce_movie_header *header, int64_t index);

/* Overwrites in place when the key exists, appends otherwise. */
CE_API void ce_movie_header_set(ce_movie_header *header, const char *key, const char *value);

/* "Key Value" per line, then a closing blank line - the old writer wrapped
 * ToString() in WriteLine(), and that extra EOL is part of the format now.
 * Invalidated by any other call on the same header. */
CE_API const char *ce_movie_header_serialize(ce_movie_header *header, int32_t crlf, uint64_t *len_out);

/* ---- plain line lumps (Comments.txt and friends) ----
 *
 * A lump that is just lines: parsing keeps every line that has a non-
 * whitespace character, in order, duplicates included; serializing writes
 * each line then the same closing blank line as every other text lump.
 */

typedef struct ce_text_lines ce_text_lines;

CE_API ce_text_lines *ce_text_lines_new(void);
CE_API void ce_text_lines_free(ce_text_lines *lines);
CE_API void ce_text_lines_parse(ce_text_lines *lines, const char *text, uint64_t len);
CE_API int64_t ce_text_lines_count(const ce_text_lines *lines);
/* Borrowed; invalidated by parse/add/free. NULL when out of range. */
CE_API const char *ce_text_lines_at(const ce_text_lines *lines, int64_t index);
CE_API void ce_text_lines_add(ce_text_lines *lines, const char *line);
/* Invalidated by any other call on the same object. */
CE_API const char *ce_text_lines_serialize(ce_text_lines *lines, int32_t crlf, uint64_t *len_out);

/* ---- subtitle lines ----
 *
 * One Subtitles.txt line: "subtitle FRAME X Y DURATION RRGGBBAA message...".
 * The leading word is ignored on parse (it always was), the message is the
 * space-joined tail with surrounding whitespace trimmed, and a line that does
 * not parse is reported, not fatal - the old loader skipped it silently.
 */

typedef struct ce_subtitle_fields
{
	int32_t frame;
	int32_t x;
	int32_t y;
	int32_t duration;
	uint32_t color;
} ce_subtitle_fields;

/* Returns the message's byte length on success (message copied into
 * message_buf, NUL-terminated, when cap allows; a message can never be longer
 * than its line), or -1 when the line does not parse. */
CE_API int64_t ce_subtitle_parse_line(
	const char *line, ce_subtitle_fields *fields, char *message_buf, uint64_t cap);

/* Renders the line (no EOL). Returns its byte length; writes NUL-terminated
 * into buf when cap allows, so call with cap 0 to size first if needed. */
CE_API int64_t ce_subtitle_format_line(
	const ce_subtitle_fields *fields, const char *message, char *buf, uint64_t cap);

/* ---- the savestate/movie container ----
 *
 * Savestates and movies share one file shape: a zip of named lumps, each
 * optionally zstd-compressed (marked by a ".zst" suffix and stored, not
 * deflated), with a "ChimeraState 1.0" version lump. The engine owns the FORMAT;
 * file I/O stays with the caller - a writer produces the finished archive as
 * one buffer, a reader takes one.
 *
 * Lumps are addressed as the C# always did: by base name, meaning the entry's
 * path up to its first '.' ("Input Log.txt" is "Input Log" - and the version
 * lump "ChimeraState 1.0" is "ChimeraState 1"). zstd needs libzstd beside this
 * library; it is loaded on first use and its absence is a put/get error.
 */

typedef struct ce_state_writer ce_state_writer;

/* compression_level is the config's 0-9: 0 stores, otherwise deflate for
 * plain lumps and zstd level 2n+1 for zstd lumps - the mapping the C# writer
 * used. emu_version fills the ChimeraVersion lump; both version lumps are written
 * here. */
CE_API ce_state_writer *ce_state_writer_new(int32_t compression_level, const char *emu_version);
CE_API void ce_state_writer_free(ce_state_writer *w);

/* ext may be NULL for extensionless lumps ("GreenZone"). Returns 0 on
 * success; on failure see _last_error, and the writer is poisoned - finish
 * will fail too, matching the old writer's collect-errors-until-close. */
CE_API int32_t ce_state_writer_put_lump(
	ce_state_writer *w, const char *name, const char *ext, int32_t zstd,
	const uint8_t *data, uint64_t len);

/* The finished archive. NULL on failure (see _last_error).
 * Valid until the writer is freed. */
CE_API const uint8_t *ce_state_writer_finish(ce_state_writer *w, uint64_t *len_out);

/* "" when no error. Invalidated by the next call on the same writer. */
CE_API const char *ce_state_writer_last_error(ce_state_writer *w);

typedef struct ce_state_reader ce_state_reader;

/* Returns NULL when data is not a readable container. That is two different
 * stories, told through error_out: NULL for the quiet cases the old loader
 * returned null for (not a zip; a non-movie with no version lump), a message
 * for real corruption (duplicate lump names). error_out may be NULL if the
 * caller does not care; the message is a static buffer, valid until the next
 * failed open. is_movie relaxes the missing-version-lump case to version
 * 1.0.0, as movie loading always has. */
CE_API ce_state_reader *ce_state_reader_open(
	const uint8_t *data, uint64_t len, int32_t is_movie, const char **error_out);
CE_API void ce_state_reader_free(ce_state_reader *r);

/* The sub version from the "ChimeraState 1.0" lump (1.0.N). */
CE_API int32_t ce_state_reader_version(const ce_state_reader *r);

/* The lump's bytes, zstd-decompressed when it was stored compressed - length
 * is the DECOMPRESSED length. NULL when absent or undecompressable (see
 * _last_error to tell which; absent is ""). ext participates only in the
 * version-1.0.2 quirk, where compression was inferred from the extension
 * instead of marked. Invalidated by the next lump read on the same reader. */
CE_API const uint8_t *ce_state_reader_lump(
	ce_state_reader *r, const char *name, const char *ext, uint64_t *len_out);

/* "" when the last lump miss was a plain absence. Invalidated by the next
 * call on the same reader. */
CE_API const char *ce_state_reader_last_error(ce_state_reader *r);

/* ---- progress ----
 *
 * How far along a slow call is, for a frontend that shows a bar. The engine
 * reports as it works: hashing a file (bytes of its length), compressing or
 * reading a state lump (bytes), a boot's stages (no total), the compiled
 * objects a boot fetches (a count, no total). The callback runs on the thread
 * doing the work - the caller's own - so a frontend may pump its message loop
 * from it; it must not call back into the engine. Pass a null fn to stop. */
typedef void (*ce_progress_fn)(const char *stage, uint64_t done, uint64_t total, void *user);
CE_API void ce_progress_set(ce_progress_fn fn, void *user);

/* ---- identity hashing ----
 *
 * The hash the frontend identifies files by: roms, firmware.
 * Writes 40 uppercase hex characters plus a NUL into out41. */
CE_API void ce_sha1_hex(const uint8_t *data, uint64_t len, char *out41);

/* The same hash of a file, taken by reading it rather than by holding it: a
 * firmware may be a disk image, and one that does not fit in memory still has
 * an identity. Writes 40 uppercase hex characters plus a NUL into out41, and
 * the length hashed into lenOut when it is given. Answers repeat calls from a
 * cache keyed by path, size and mtime, so hashing the same file twice in a
 * session costs one read. Returns 1 when the file was read, 0 when it was not. */
CE_API int ce_sha1_file(const char *utf8Path, char *out41, uint64_t *lenOut);

/* ---- firmware ----
 *
 * Whether a provided file is the firmware a core asked for. declared_size 0
 * means the core pinned no size; expected_sha1s is the core's accepted hash
 * list, one per '\n'-separated line, empty for "anything". Returns 0 (wrong
 * size, refused), 1 (unrecognised hash, used anyway) or 2 (a pinned match). */
CE_API int32_t ce_firmware_state(
	int64_t declared_size, const char *expected_sha1s,
	int64_t actual_size, const char *actual_sha1);

/* The canonical firmware line a movie records: "id=SHA1" pairs, one per
 * '\n'-separated line in, sorted by id and space-joined out. Thread-local
 * buffer, invalidated by the next call. */
CE_API const char *ce_firmware_record_line(const char *pairs, uint64_t *len_out);

/* The firmware decision tree (docs/project.md): which of a core's declared
 * firmware files the project's decisions call for. decl_json is the
 * package's "firmware" array, where an entry may carry "requiredWhen" - a
 * condition over the slot map and the EFFECTIVE settings:
 *   {"slot": id}                    the slot holds at least one file
 *   {"slot": id, "extension": e}    ...a file with that extension
 *   {"setting": name, "is": v}      the setting has that value
 *   {"setting": name, "in": [v..]}  ...one of those values
 *   {"all": [c..]} {"any": [c..]} {"not": c}
 * Every applying entry is REQUIRED - the decisions nail each requirement
 * to one exact file or to nothing; variants of one id are separate
 * entries with disjoint conditions (a sync setting picks between them),
 * and optional firmware does not exist. An entry without a condition
 * always applies. Malformed conditions evaluate false. Returns a JSON
 * array [{"id":..,"index":n}] in declaration order, n being the
 * position in decl_json (thread-local; invalidated by the next call). */
CE_API const char *ce_firmware_evaluate(
	const char *decl_json, uint64_t decl_len,
	const char *slots_json, uint64_t slots_len,
	const char *settings_json, uint64_t settings_len,
	uint64_t *len_out);

/* The same decision tree, gating SYNC SETTINGS (docs/project.md): the game
 * files chosen decide which settings are exposed at all (a Game Gear cart
 * exposes its sound chip, a Genesis cart its own), and settings may gate
 * further settings. decl_json is the package's "settings" array; an entry
 * may carry "exposedWhen" with the same condition language. An entry
 * without a condition is always exposed; variants of one name are separate
 * entries with disjoint conditions. Returns [{"name":..,"index":n}] in
 * declaration order (thread-local; invalidated by the next call). */
CE_API const char *ce_settings_evaluate(
	const char *decl_json, uint64_t decl_len,
	const char *slots_json, uint64_t slots_len,
	const char *settings_json, uint64_t settings_len,
	uint64_t *len_out);

/* And gating the SLOTS themselves, within the file form (docs/project.md):
 * a slot in file_slots.json may carry "exposedWhen" over the CURRENT slot
 * map, so filling one slot can make another unavailable (a Famicom disk
 * rules out a cartridge and vice versa) until it is unloaded. decl_json is
 * the whole file_slots.json. Returns the exposed slot ids as a JSON array
 * of strings, declaration order (thread-local; invalidated by the next
 * call). ce_project_validate enforces the same rule: files in an unexposed
 * slot are structurally invalid. */
CE_API const char *ce_slots_evaluate(
	const char *decl_json, uint64_t decl_len,
	const char *slots_json, uint64_t slots_len,
	const char *settings_json, uint64_t settings_len,
	uint64_t *len_out);

/* The files a cue sheet references (its FILE lines, quoted or bare), as a
 * JSON array of names in cue order - what ce_project_file_add will pull in
 * beside the cue as "support" files. Lets a form show "disc.cue (+ N
 * tracks)" and complain about a missing track at pick time rather than at
 * create time. Returns thread-local storage, invalidated by the next
 * call; not-a-cue or empty input returns []. */
CE_API const char *ce_cue_references(
	const char *cue_bytes, uint64_t cue_len,
	uint64_t *len_out);

/* ---- multi-file games ----
 *
 * A .chimeraMultiFile descriptor names the files a game is made of: bare
 * names (every file lives in the descriptor's own folder), each with the
 * SHA1 recorded at creation, in an order that matters (images load as rom,
 * rom2..romN - which IS the disc/floppy swap order). Roles: "image" (an
 * ordered, mountable, swappable item), "support" (present and hashed, but
 * only referenced by another file - a cue's bin), "savedata" (at most one:
 * a previously exported save, mounted under the fixed name "savedata" for
 * the core to consume). The engine owns the format: parsing, the rules,
 * hashing, the canonical movie line.
 *
 * ce_multifile_open enforces the STRUCTURAL rules (valid JSON, bare unique
 * names, known roles, at least one image, at most one savedata, and cue
 * closure: every file a listed cue references must itself be listed) and
 * loads + hashes the files. A missing or hash-mismatched file does NOT fail
 * the open: per-file status lets the caller refuse or knowingly proceed -
 * the movie line always records what was ACTUALLY loaded. */
typedef struct ce_multifile ce_multifile;

/* NULL with *error_out (static per-thread) on structural failure. */
CE_API ce_multifile *ce_multifile_open(const char *descriptor_path, const char **error_out);
CE_API void ce_multifile_free(ce_multifile *m);

CE_API int32_t ce_multifile_count(const ce_multifile *m);
CE_API const char *ce_multifile_name(const ce_multifile *m, int32_t index);
CE_API const char *ce_multifile_role(const ce_multifile *m, int32_t index);
/* the descriptor's recorded hash (40 uppercase hex) */
CE_API const char *ce_multifile_sha1(const ce_multifile *m, int32_t index);
/* the hash of the bytes actually on disk; "" while the file is missing */
CE_API const char *ce_multifile_actual_sha1(const ce_multifile *m, int32_t index);
/* 0 = ok, 1 = missing, 2 = hash mismatch */
CE_API int32_t ce_multifile_status(const ce_multifile *m, int32_t index);
/* 1 when every file is present with a matching hash */
CE_API int32_t ce_multifile_ok(const ce_multifile *m);
/* the loaded bytes (NULL while missing); borrowed, live as long as m */
CE_API const uint8_t *ce_multifile_data(const ce_multifile *m, int32_t index, uint64_t *len_out);

CE_API int32_t ce_multifile_image_count(const ce_multifile *m);
/* entry index of the nth image (0-based), -1 out of range */
CE_API int32_t ce_multifile_image_index(const ce_multifile *m, int32_t nth);
/* entry index of the savedata file, -1 when the descriptor has none */
CE_API int32_t ce_multifile_savedata_index(const ce_multifile *m);

/* The canonical movie line ("GameFiles" in a movie header): the entries in
 * descriptor order as name=SHA1 (images) or name=SHA1:role (the rest),
 * space-joined, with '%', '=', ':' and bytes outside 0x21..0x7E percent-
 * encoded in names. The hashes are the ACTUAL ones - a movie records what
 * was loaded, not what the descriptor claimed. NULL while any file is
 * missing. Borrowed; invalidated by ce_multifile_free. */
CE_API const char *ce_multifile_record_line(const ce_multifile *m, uint64_t *len_out);

/* Creation: names (bare, in order) + parallel roles; the files must already
 * sit in the descriptor's folder. Hashes them, enforces every structural
 * rule (including cue closure), writes the descriptor JSON. 0 on success,
 * nonzero with *error_out (static per-thread) otherwise. */
CE_API int32_t ce_multifile_save(
	const char *descriptor_path,
	const char *const *names, const char *const *roles, int32_t count,
	const char **error_out);

/* ---- the project ----
 *
 * A .chimeraProject is chimera's entry point and its movie in one file: a
 * JSON document holding everything required to reproduce the work except
 * the data bytes, which are named by SHA1 (docs/project.md). Identity
 * (title, description), the pinned core (name, version, package SHA1), the
 * file manifest (bare canonical names + SHA1 + the core-defined slot each
 * file fills, order within a slot = swap order), firmware pins, the sync
 * settings, and the TAS work itself: input log, markers, branches.
 *
 * No paths are ever stored. Files are resolved per session - the caller
 * says where each one is (ce_project_file_resolve / ce_project_resolve_dir)
 * and the engine hashes what it finds; a file whose on-disk NAME differs is
 * fine, the canonical name is the label and the hash is the identity.
 * Saving records the ACTUAL hash of every resolved file, so a knowing
 * override leaves a truthful record.
 *
 * ce_project_open enforces the structural rules (valid JSON, known keys,
 * bare unique names, well-formed hashes and slot ids) and leaves every file
 * unresolved. ce_project_validate checks the manifest against a core's
 * file_slots.json declaration: known slots, cardinality, formats, and the
 * declaration's atLeastOneOf groups. */
typedef struct ce_project ce_project;

/* a fresh, empty project (for the creation wizard) */
CE_API ce_project *ce_project_new(void);
/* NULL with *error_out (static per-thread) on structural failure */
CE_API ce_project *ce_project_open(const char *path, const char **error_out);
/* 0 on success; resolved files are written with their ACTUAL hash */
CE_API int32_t ce_project_save(ce_project *p, const char *path, const char **error_out);
CE_API void ce_project_free(ce_project *p);

/* This project's identity, and no other's: minted when it is created, carried
 * in the file, and stable across the file being renamed, moved or synced to
 * another machine. The frontend keeps everything REGENERABLE that belongs to a
 * project - the state history, and where this machine last found the project's
 * files - in a per-user cache keyed by this, so the .chimeraProject stays the
 * one file that has to travel (docs/state-manager.md). A project written
 * before ids existed is given one when it is opened, and keeps it from its
 * next save. Borrowed; valid until the project is freed. */
CE_API const char *ce_project_id(const ce_project *p);
CE_API const char *ce_project_title(const ce_project *p);
CE_API void ce_project_set_title(ce_project *p, const char *title);
CE_API const char *ce_project_description(const ce_project *p);
CE_API void ce_project_set_description(ce_project *p, const char *description);

/* the pinned core: package name, version, package zip SHA1 */
CE_API const char *ce_project_core_name(const ce_project *p);
CE_API const char *ce_project_core_version(const ce_project *p);
CE_API const char *ce_project_core_sha1(const ce_project *p);
CE_API void ce_project_set_core(ce_project *p, const char *name, const char *version, const char *sha1);

CE_API uint64_t ce_project_rerecords(const ce_project *p);
CE_API void ce_project_set_rerecords(ce_project *p, uint64_t count);

/* The sync settings, as the JSON object the session's settings channel
 * takes. Borrowed; invalidated by the next settings call on p. The setter
 * rejects anything that does not parse as a JSON object. */
CE_API const char *ce_project_settings_text(ce_project *p, uint64_t *len_out);
CE_API int32_t ce_project_set_settings_text(ce_project *p, const char *json, const char **error_out);

/* The firmware pins, as a JSON array carried verbatim (the firmware
 * channel's record: id + SHA1 entries). Borrowed as above; the setter
 * rejects anything that does not parse as a JSON array. */
/* What the core compiled for this game: a JSON array of {name, sha1}, one per
 * cached object (docs/compile-cache.md). Regenerable from the same rom and
 * package, recorded so a later open can check it is the same compiled code. */
CE_API const char *ce_project_core_cache_text(ce_project *p, uint64_t *len_out);
CE_API int32_t ce_project_set_core_cache_text(ce_project *p, const char *json, const char **error_out);
CE_API const char *ce_project_firmware_text(ce_project *p, uint64_t *len_out);
CE_API int32_t ce_project_set_firmware_text(ce_project *p, const char *json, const char **error_out);

/* The input log lump, exactly what ce_movie_log_parse/_serialize exchange
 * (LogKey line + entries). "" for a project with no frames yet. */
CE_API const char *ce_project_log_text(const ce_project *p, uint64_t *len_out);
CE_API void ce_project_set_log_text(ce_project *p, const char *text, uint64_t len);

/* Markers, kept sorted by frame. keep_state is the user's keep-a-state-here
 * choice (default true; serialized only when false). Add returns the index
 * the marker landed at. */
CE_API int32_t ce_project_marker_count(const ce_project *p);
CE_API int64_t ce_project_marker_frame(const ce_project *p, int32_t index);
CE_API const char *ce_project_marker_text(const ce_project *p, int32_t index);
CE_API int32_t ce_project_marker_keep_state(const ce_project *p, int32_t index);
CE_API int32_t ce_project_marker_add(ce_project *p, int64_t frame, const char *text, int32_t keep_state);
CE_API void ce_project_marker_remove(ce_project *p, int32_t index);

/* Branches: a named alternative input log at a frame, in creation order,
 * with an optional timestamp text (carried verbatim) and its own markers -
 * a branch is work, so everything about it except the regenerable state
 * lives in the project. */
CE_API int32_t ce_project_branch_count(const ce_project *p);
CE_API const char *ce_project_branch_name(const ce_project *p, int32_t index);
CE_API int64_t ce_project_branch_frame(const ce_project *p, int32_t index);
CE_API const char *ce_project_branch_time(const ce_project *p, int32_t index);
CE_API const char *ce_project_branch_log_text(const ce_project *p, int32_t index, uint64_t *len_out);
CE_API void ce_project_branch_add(ce_project *p, const char *name, int64_t frame, const char *time, const char *log_text, uint64_t len);
CE_API void ce_project_branch_remove(ce_project *p, int32_t index);
CE_API int32_t ce_project_branch_marker_count(const ce_project *p, int32_t branch);
CE_API int64_t ce_project_branch_marker_frame(const ce_project *p, int32_t branch, int32_t index);
CE_API const char *ce_project_branch_marker_text(const ce_project *p, int32_t branch, int32_t index);
CE_API int32_t ce_project_branch_marker_keep_state(const ce_project *p, int32_t branch, int32_t index);
CE_API void ce_project_branch_marker_add(ce_project *p, int32_t branch, int64_t frame, const char *text, int32_t keep_state);

/* The headers map: movie metadata the format does not first-class (Author,
 * emulator version, platform facts) as ordered verbatim key/value pairs.
 * set with a NULL value removes; a new key appends. */
CE_API int32_t ce_project_header_count(const ce_project *p);
CE_API const char *ce_project_header_key_at(const ce_project *p, int32_t index);
CE_API const char *ce_project_header_value_at(const ce_project *p, int32_t index);
CE_API const char *ce_project_header_get(const ce_project *p, const char *key);
CE_API void ce_project_header_set(ce_project *p, const char *key, const char *value);

/* subtitles: verbatim display lines, in order */
CE_API int32_t ce_project_subtitle_count(const ce_project *p);
CE_API const char *ce_project_subtitle_at(const ce_project *p, int32_t index);
CE_API void ce_project_subtitle_add(ce_project *p, const char *line);
CE_API void ce_project_subtitle_remove(ce_project *p, int32_t index);

/* Adds a file: canonical (bare) name, the slot it fills, and where its
 * bytes are RIGHT NOW - read and hashed immediately, so the entry is born
 * resolved. A .cue's referenced files are auto-added from the cue's own
 * folder with the reserved slot "support" (closure at creation). 0 on
 * success; nonzero with *error_out (static per-thread) otherwise. */
CE_API int32_t ce_project_file_add(ce_project *p, const char *name, const char *slot, const char *source_path, const char **error_out);
CE_API void ce_project_file_remove(ce_project *p, int32_t index);

CE_API int32_t ce_project_file_count(const ce_project *p);
CE_API const char *ce_project_file_name(const ce_project *p, int32_t index);
CE_API const char *ce_project_file_slot(const ce_project *p, int32_t index);
/* the recorded hash (40 uppercase hex) */
CE_API const char *ce_project_file_sha1(const ce_project *p, int32_t index);
/* the hash of the resolved bytes; "" while unresolved */
CE_API const char *ce_project_file_actual_sha1(const ce_project *p, int32_t index);
/* 0 = resolved and matching, 1 = unresolved, 2 = resolved but mismatched */
CE_API int32_t ce_project_file_status(const ce_project *p, int32_t index);
/* Where this file's bytes were read from in THIS run, or "" if not yet read.
 * In memory only - NEVER written to the project, which is distributable and
 * has no business carrying one machine's paths. A frontend that wants to
 * remember locations keeps its own sidecar and reads them from here. */
CE_API const char *ce_project_file_source_path(const ce_project *p, int32_t index);
/* How many bytes the resolved file holds, or 0 while unresolved.
 *
 * There is deliberately no call for the BYTES. A project's files stay where
 * they are and are read when the machine asks for them (ce_session_open's
 * file_paths): a PS2 disc is over four gigabytes, and every copy of one
 * between the disk and the guest is four gigabytes nothing needed. What a
 * project knows about a file is its name, its hash, its size and its path. */
CE_API uint64_t ce_project_file_size(const ce_project *p, int32_t index);

/* Resolves one file from a caller-provided location (the per-session
 * resolution dialog): reads, hashes, sets status 0 or 2. The on-disk name
 * may differ from the canonical one. 0 on success (even a mismatch - that
 * is a status, not an error); nonzero with *error_out when unreadable. */
CE_API int32_t ce_project_file_resolve(ce_project *p, int32_t index, const char *path, const char **error_out);

/* Puts a file back to unresolved, dropping the bytes, the actual hash and the
 * source path. For a caller that resolved speculatively (a remembered location
 * that turned out to hold something else) and wants the file asked about
 * rather than mounted. */
CE_API void ce_project_file_unresolve(ce_project *p, int32_t index);
/* Tries to resolve every unresolved file by its canonical name inside dir
 * (the "files beside the project" convenience). Returns how many resolved. */
CE_API int32_t ce_project_resolve_dir(ce_project *p, const char *dir);
/* 1 when every file is resolved with a matching hash */
CE_API int32_t ce_project_files_ok(const ce_project *p);

/* The manifest checked against a core's file_slots.json declaration (slot
 * ids, cardinality, formats, atLeastOneOf groups; "support" is exempt).
 * 0 when it conforms; nonzero with *error_out otherwise. */
CE_API int32_t ce_project_validate(const ce_project *p, const char *slots_json, uint64_t slots_len, const char **error_out);

/* The slot map the session mounts as "slots": a JSON object of slot id ->
 * canonical names in manifest order, "support" excluded. Borrowed;
 * invalidated by the next call on p. */
CE_API const char *ce_project_slots_text(ce_project *p, uint64_t *len_out);

/* ---- core packages ----
 *
 * A core package is a zip (or, for development, a directory) whose root holds
 * core.wbx + waterbox.config - the data-driven form the generic adapter runs -
 * or a chimera-core.json manifest. The engine owns the container: what makes
 * a path a package, the package's identity (the SHA1 of its zip), and access
 * to its entries. What the entries MEAN stays with the caller for now; the
 * waterbox.config schema moves into the engine with the session.
 *
 * This is the engine's first filesystem read - packages are read-only inputs,
 * and the coming native session has to open them without a frontend anyway.
 */

typedef struct ce_package ce_package;

/* Returns NULL two ways, told apart by error_out: NULL error for a path that
 * is simply not a core package (an unrelated zip or directory, a missing
 * path), a message (static per-thread, valid until the thread's next failed
 * open) for something that looks like a package but cannot be read. The zip
 * is hashed only after it is known to be a package. */
CE_API ce_package *ce_package_open(const char *path, const char **error_out);
CE_API void ce_package_free(ce_package *p);

/* The package's identity: SHA1 of the zip file, uppercase hex. NULL for the
 * directory form, which has no file to hash. */
CE_API const char *ce_package_sha1(const ce_package *p);

/* Nonzero for the data-driven waterbox form (core.wbx + waterbox.config). */
CE_API int32_t ce_package_is_waterbox(const ce_package *p);

CE_API int32_t ce_package_has_entry(ce_package *p, const char *name);

/* Core-owned asset files: every entry under "assets/", sorted. A waterbox
 * session mounts each one at its path with the "assets" prefix dropped, so
 * a package carrying assets/sys/GC/font.bin gives its guest /sys/GC/font.bin.
 * The names stay valid until the package is freed. */
CE_API int32_t ce_package_asset_count(ce_package *p);
CE_API const char *ce_package_asset_name(ce_package *p, int32_t i);

/* The entry's bytes (decompressed from the zip, or the file in the directory
 * form). NULL when absent or unreadable (see _last_error to tell which;
 * absent is ""). Invalidated by the next entry read on the same package. */
CE_API const uint8_t *ce_package_entry(ce_package *p, const char *name, uint64_t *len_out);

/* "" when the last entry miss was a plain absence.
 * Invalidated by the next call on the same package. */
CE_API const char *ce_package_last_error(ce_package *p);

/* ---- the session ----
 *
 * A running waterboxed machine: a core package's guest, loaded through the
 * miniBox host (libminiboxhost, found beside this library), configured by its
 * own waterbox.config, fed a rom and settings, and driven a frame at a time.
 * This is the same machine the C# WaterboxCore adapter runs - one machine,
 * one behaviour, with or without a frontend.
 *
 * The engine composes the effective settings itself: every declared setting
 * at its config default, overlaid with the caller's overrides JSON (the
 * user's or the movie's sync settings). Buttons map to input bits by their
 * position in the config's buttons array.
 */

typedef struct ce_session ce_session;

/* rom_len 0 with rom NULL is allowed for coreless boots (none exist today).
 * settings_overrides_json may be NULL for pure defaults. firmware arrives as
 * ids and blobs, parallel arrays; extra files (a multi-file game's rom2..N,
 * support files, savedata, and "rom.name" when the name is contract) the
 * same way - each is mounted under its given name before the core boots.
 * NULL with *error_out set (static per-thread, valid until the thread's
 * next failed open) on any failure - an unreadable package, a missing
 * export, a core that refused the rom (its GetLoadError text is the message
 * when it gives one). */
/* Opens a machine.
 *
 * A file reaches the guest one of two ways, and the choice is per file. Give
 * BYTES (rom/rom_len, extra_data/extra_lens) for something the caller made or
 * already holds - a settings blob, a slot map, a name. Give a PATH (rom_path,
 * extra_paths[i], either may be NULL for "use the bytes") for something that
 * exists on disk, and it is never read into memory at all: the guest's reads
 * go to the file as it makes them.
 *
 * That is not a tuning knob. A PS2 disc image is over four gigabytes and a
 * .NET byte[] cannot hold two, so bytes are not merely wasteful for a disc,
 * they are impossible. The bytes a guest sees are identical either way, a
 * read-only file has nothing a savestate must carry, and the machine cannot
 * tell the difference - so the only rule is that a file must not change while
 * a session has it open. Firmware stays bytes: it is small, and a frontend
 * resolves it from its own store rather than from a path the project knows. */
CE_API ce_session *ce_session_open(
	const char *package_path,
	const uint8_t *rom, uint64_t rom_len, const char *rom_path,
	const char *settings_overrides_json,
	const char *const *firmware_ids, const uint8_t *const *firmware_data,
	const uint64_t *firmware_lens, int32_t firmware_count,
	const char *const *extra_names, const uint8_t *const *extra_data,
	const uint64_t *extra_lens, const char *const *extra_paths, int32_t extra_count,
	const char **error_out);

CE_API void ce_session_free(ce_session *s);

/* config-derived facts (borrowed strings live as long as the session) */
CE_API const char *ce_session_core_name(const ce_session *s);
CE_API const char *ce_session_system_id(const ce_session *s);
CE_API int32_t ce_session_width(const ce_session *s);
CE_API int32_t ce_session_height(const ce_session *s);
CE_API int32_t ce_session_virtual_width(const ce_session *s);
CE_API int32_t ce_session_virtual_height(const ce_session *s);
/* post-Init: the guest's own answer when it gives one, else the config's */
CE_API int32_t ce_session_vsync_numerator(const ce_session *s);
CE_API int32_t ce_session_vsync_denominator(const ce_session *s);
CE_API int32_t ce_session_samples_per_frame(const ce_session *s);
CE_API int32_t ce_session_channels(const ce_session *s);
CE_API int32_t ce_session_deterministic(const ce_session *s);

/* Whether a GPU OUTSIDE the sandbox drew this session's pictures.
 *
 * A core can be given a real OpenGL context: its renderer runs in the sandbox
 * as always, but its GL calls are answered on the host's device. That is much
 * faster than a software rasteriser and it is not deterministic - the GPU is
 * outside the savestate and different on every machine - so a session that had
 * one reports 0 from ce_session_deterministic whatever its config said, and a
 * movie recorded on it must record that a GPU drew. Without that a replay
 * elsewhere desyncs with nothing to explain it.
 *
 * It is only ever true when the caller asked (ce_gl_request), this build has a
 * bridge, a driver gave it a context, and the core knew what to do with one. */
CE_API int32_t ce_session_gpu_drew(const ce_session *s);

/* ---------------------------------------------------------------------------
 * The GPU bridge itself. One context per process, because there is only ever
 * one machine running; sessions borrow it.
 */

/* Ask for hardware acceleration on the sessions opened after this call. Asking
 * is not having: see ce_session_gpu_drew for what actually happened. */
CE_API void ce_gl_request(int32_t want);
CE_API int32_t ce_gl_requested(void);

/* Whether a context exists now. Null-safe, cheap, false in a build without the
 * bridge compiled in. */
CE_API int32_t ce_gl_available(void);

/* What the driver calls itself ("4.6 (Core Profile) Mesa ... on ..."), for a
 * movie header and for saying which GPU a recording was made on. Empty when
 * there is none. */
CE_API const char *ce_gl_description(void);

/* Give the current-context slot back to whoever had it.
 *
 * "Current context" is one slot per thread and the frontend draws its own
 * picture through it, so the bridge borrows it for the duration of a guest
 * call and returns it here. The engine calls this itself at every point where
 * a core stops running and the caller gets control back; a caller that runs
 * guest code by some other route can call it too. Harmless with no bridge, no
 * context, or nothing borrowed. */
CE_API void ce_gl_release(void);

/* ---------------------------------------------------------------------------
 * The compile cache. A core that recompiles its machine's code keeps the
 * compiled objects between sessions in a directory the host owns: an object
 * is a pure function of the module, the core package and the target CPU, so a
 * warm run is a cold run minus the compile, and nothing about determinism,
 * movies or projects depends on whether the cache was warm. The contract is
 * miniBox's source/cache/cache-bridge.h; a core that keeps such files exports
 * SetCacheBridge and receives the dispatcher before Init.
 */
/* The directory for the sessions opened after this call (NULL: none). The
 * caller names it by core and package identity; the engine creates it and
 * keeps whatever the core names under it, relative paths only. */
CE_API void ce_cache_dir(const char *dir);
CE_API const char *ce_cache_dir_get(void);
/* Objects stored into and fetched from the cache by this session's core. */
CE_API uint64_t ce_session_cache_stored(const ce_session *s);
CE_API uint64_t ce_session_cache_fetched(const ce_session *s);

/* ---------------------------------------------------------------------------
 * Precompile sessions. A core that can fill its compile cache without running
 * exports SetPrecompile(index, count, firmware_too): the session boots, never
 * runs, compiles every module part whose name hashes to its index (so several
 * sessions side by side, in separate processes, each compile a share), and
 * reports when it is done. The caller pumps ce_session_frame_advance until
 * ce_session_precompile_done and reads the progress for its bar.
 */
/* Sessions opened after this call are precompile sessions (count 0: off). */
CE_API void ce_precompile_request(int32_t index, int32_t count, int32_t firmware_too);
/* -1 when this session is no precompile session or the core has none. */
CE_API int32_t ce_session_precompile_done(const ce_session *s);
CE_API int32_t ce_session_precompile_progress(const ce_session *s, uint32_t *done_out, uint32_t *total_out);
CE_API int64_t ce_session_button_count(const ce_session *s);
CE_API const char *ce_session_button_name(const ce_session *s, int64_t index);
CE_API int64_t ce_session_axis_count(const ce_session *s);
CE_API const char *ce_session_axis_name(const ce_session *s, int64_t index);

/* Whether a declared control is one THIS machine has.
 *
 * waterbox.config declares the union of every peripheral a package's ports can
 * hold - it is a static declaration and cannot know what a project plugged in.
 * The running core can, because it read the port settings and built the machine
 * from them, so it is asked (its optional IsButtonActive/IsAxisActive exports)
 * once, after Init.
 *
 * An inactive control is absent from the frontend's controller, from TAStudio's
 * columns and from a movie entry. Its INDEX never moves: the count and the
 * order are the declaration's, so ce_session_set_button and every core's own
 * wire enum are untouched by any of this. A core that exports neither answer
 * has all of its controls, which is what every core had before this existed. */
/* Drive lights: one per medium the machine has (a disc, a hard disk), lit on
 * any frame that drive was read or written. A core that exports none has none,
 * and the count is zero - which is what a machine with no removable media
 * should show, rather than an icon that never lights. Names are settled at
 * load; the light is asked every frame. */
CE_API int32_t ce_session_drive_count(const ce_session *s);
CE_API const char *ce_session_drive_name(const ce_session *s, int32_t index);
CE_API int32_t ce_session_drive_light(const ce_session *s, int32_t index);

CE_API int32_t ce_session_button_active(const ce_session *s, int64_t index);
CE_API int32_t ce_session_axis_active(const ce_session *s, int64_t index);

/* Axes are set per frame, before the advance they belong to. */
CE_API void ce_session_set_axis(ce_session *s, int32_t index, int32_t value);

/* WIDE INPUT: buttons past the packed mask's 64 (a DOS keyboard is 101 keys
 * before the mouse and joysticks). Set like axes - per frame, before the
 * advance; values persist until changed. The effective state of button i at
 * an advance is this call's value OR'd with packed bit i (i < 64), so a
 * caller uses whichever path fits and either alone is exact. The session
 * delivers only CHANGES to the guest through its SetButton export; a config
 * declaring more than 64 buttons refuses to open without that export.
 * Movie entries have always carried arbitrary button counts - one mnemonic
 * column per declared button - so recording and playback are unchanged. */
CE_API void ce_session_set_button(ce_session *s, int32_t index, int32_t pressed);

/* One frame: buttons is the bitmask (bit i = the config's buttons[i]);
 * buttons beyond 63 ride ce_session_set_button. Returns nonzero when the frame
 * was a lag frame (the guest never read input), per the config's lag export.
 *
 * render 0 skips the video copy, and - for a core that exports
 * SetRenderingEnabled - tells the core itself to stop drawing. That is turbo:
 * the frame still happens, the machine is still exactly the machine it would
 * have been, and only the picture is not produced. On a console with a 3D chip
 * the picture is most of the frame's cost, so a seek that would have taken a
 * minute takes a few seconds. The video buffer keeps the last frame that was
 * drawn, so a caller that wants a picture at the end of a fast run asks for the
 * last frame with render 1. */
CE_API int32_t ce_session_frame_advance(ce_session *s, uint64_t buttons, int32_t render);

/* The last rendered frame, BGRA, video_width*video_height pixels.
 * Invalidated by the next rendered frame_advance. */
CE_API const uint32_t *ce_session_video(const ce_session *s);

/* The LIVE frame size: a machine that changes video modes (DOS) reports it
 * per frame through optional GetVideoWidth/GetVideoHeight exports, clamped
 * to the config's declared buffer. Without the exports these equal
 * ce_session_width/height. Valid after the advance they belong to. */
CE_API int32_t ce_session_video_width(const ce_session *s);
CE_API int32_t ce_session_video_height(const ce_session *s);

/* The last frame's audio as interleaved stereo s16 (mono sources are
 * doubled), and its sample-pair count. Invalidated by the next advance. */
CE_API const int16_t *ce_session_audio(const ce_session *s, int32_t *sample_count);

/* The guest's whole-machine state. The buffer is the session's, reused and
 * invalidated by the next save. Load returns 0 on success. */
CE_API const uint8_t *ce_session_save_state(ce_session *s, uint64_t *len_out);
CE_API int32_t ce_session_load_state(ce_session *s, const uint8_t *data, uint64_t len);

/* The guest's self-described memory domains. */
CE_API int32_t ce_session_domain_count(const ce_session *s);
CE_API const char *ce_session_domain_name(const ce_session *s, int32_t index);
CE_API int64_t ce_session_domain_size(const ce_session *s, int32_t index);
CE_API int32_t ce_session_domain_writable(const ce_session *s, int32_t index);
/* Copies out [offset, offset+len); returns bytes copied (clamped at end). */
CE_API int64_t ce_session_domain_read(const ce_session *s, int32_t index, int64_t offset, uint8_t *buf, int64_t len);

/* "" when no error. Invalidated by the next call on the same session. */
CE_API const char *ce_session_last_error(ce_session *s);

/* What built the waterbox host this engine loads (its wbx_build_info JSON):
 * the frontend shows it and movies record it. NULL when the host is not
 * loadable. Static string, never invalidated. */
CE_API const char *ce_host_build_info(void);

/* The raw guest pointer behind a memory domain, for pointer-backed peek/poke
 * (the hex editor's path). Stable for the session's lifetime - the guest is
 * non-PIE - and 0 when the domain has no linear backing. */
CE_API uint64_t ce_session_domain_ptr(const ce_session *s, int32_t index);

/* Re-composes the effective settings (declared defaults overlaid with
 * overrides_json) and hands them to the RUNNING guest through its live-
 * settings exports. 0 = applied; 1 = the core has no live-settings group
 * (the caller must reboot instead); 2 = error (see _last_error). */
CE_API int32_t ce_session_apply_settings(ce_session *s, const char *overrides_json);

/* ---- the optional guest ABI groups ----
 *
 * A core may export any subset of four independent tooling groups (surfaces,
 * registers, buses, trace). The session probes them once, post-Init (names and counts may depend on the rom and
 * settings). An absent group answers with a zero count / zero flag, and the
 * frontend simply does not offer the tool.
 */

/* surfaces: core-rendered viewer windows */
CE_API int32_t ce_session_surface_count(const ce_session *s);
CE_API const char *ce_session_surface_name(const ce_session *s, int32_t index);
CE_API int32_t ce_session_surface_width(const ce_session *s, int32_t index);
CE_API int32_t ce_session_surface_height(const ce_session *s, int32_t index);
/* BGRA, width*height pixels. Borrowed; invalidated by the next render of the
 * same surface. NULL when the guest gave nothing. */
CE_API const uint32_t *ce_session_surface_render(ce_session *s, int32_t index);

/* registers: the generic debugger's register box */
CE_API int32_t ce_session_register_count(const ce_session *s);
CE_API const char *ce_session_register_name(const ce_session *s, int32_t index);
/* how many hex digits the debugger shows; 32 when the core does not say */
CE_API int32_t ce_session_register_bits(const ce_session *s, int32_t index);
CE_API int64_t ce_session_register_value(const ce_session *s, int32_t index);
/* 0 = written; 1 = this core does not support writing registers */
CE_API int32_t ce_session_register_set(ce_session *s, int32_t index, int64_t value);
CE_API int32_t ce_session_has_executed_cycles(const ce_session *s);
CE_API int64_t ce_session_executed_cycles(const ce_session *s);

/* buses: address SPACES the guest resolves through its own mapper logic -
 * peek/poke, never pointer-mapped */
CE_API int32_t ce_session_bus_count(const ce_session *s);
CE_API const char *ce_session_bus_name(const ce_session *s, int32_t index);
CE_API int64_t ce_session_bus_size(const ce_session *s, int32_t index);
CE_API int32_t ce_session_bus_writable(const ce_session *s, int32_t index);
CE_API int32_t ce_session_bus_peek(const ce_session *s, int32_t index, int32_t addr);
CE_API void ce_session_bus_poke(ce_session *s, int32_t index, int32_t addr, int32_t value);

/* savedata export: files the guest deems the user's progress (a memory
 * card's contents, a disk image), enumerated at export time - the list is
 * dynamic, a game creates files while it runs (docs/save-data.md). The
 * frontend writes them out verbatim; nothing here interprets a byte. */

/* nonzero when the core exports the savedata group */
CE_API int32_t ce_session_savedata_available(const ce_session *s);
/* Snapshots the guest's file list and returns its length; name and size
 * below refer to this snapshot. Call at a frame boundary. Entries with a
 * path that is not relative-and-clean (leading '/', "..", backslash) are
 * dropped here, with a warning on stderr, rather than handed to a writer. */
CE_API int32_t ce_session_savedata_count(ce_session *s);
/* Borrowed; invalidated by the next snapshot. NULL when out of range. */
CE_API const char *ce_session_savedata_name(const ce_session *s, int32_t index);
CE_API int64_t ce_session_savedata_size(const ce_session *s, int32_t index);
/* Copies out [offset, offset+len) of file index; returns bytes copied
 * (clamped at the file's end). Ranged so a huge file streams in chunks and
 * is never materialized whole. */
CE_API int64_t ce_session_savedata_read(ce_session *s, int32_t index, int64_t offset, uint8_t *buf, int64_t len);

/* trace: the guest appends lines to a buffer of its own; drain it once per
 * frame - a callback per instruction would cross the sandbox boundary
 * millions of times a second */
CE_API int32_t ce_session_trace_available(const ce_session *s);
CE_API const char *ce_session_trace_header(const ce_session *s);
/* The session REMEMBERS the desired flag: a savestate overwrites the guest's
 * own tracing state, and load_state re-asserts this (and clears the restored
 * buffer - its lines were traced before the load and would appear out of
 * order). */
CE_API void ce_session_trace_enable(ce_session *s, int32_t on);
/* The lines the guest traced since the last drain, as consecutive
 * NUL-terminated strings (line_count of them), cleared on the way out.
 * overflow_out reports the guest's raw truncation flag for this window.
 * Borrowed; invalidated by the next drain. */
CE_API const uint8_t *ce_session_trace_drain(
	ce_session *s, uint64_t *len_out, int32_t *line_count_out, int32_t *overflow_out);


/* ---- the session's movie ----
 *
 * The session can carry the movie itself: the input log lives inside the
 * engine, entries are parsed and generated by the engine (the Bk2 entry
 * format - groups by player, axes before buttons, positional parse), and
 * the frame position is session state. This is the movie-correctness core:
 * a frontend can display it, but cannot desync it.
 *
 * Frame numbering: the session sits "before frame N" having applied inputs
 * 0..N-1; ce_session_frame reports N.
 */

/* Copies a log's entries in and enters PLAY mode at the current frame
 * (normally 0, right after open). 0 on success. */
CE_API int32_t ce_session_movie_load(ce_session *s, const ce_movie_log *log);

/* Enters RECORD mode. mnemonics gives the character generated entries use
 * for each pressed button, one per button in config order (the frontend's
 * per-system vocabulary; pass NULL for a neutral fallback of the button
 * name's first character). Recording over existing entries truncates them:
 * recording at frame N drops entries >= N before appending - the rerecord. */
CE_API void ce_session_movie_record(ce_session *s, const char *mnemonics);

/* 0 = no movie, 1 = play, 2 = record, 3 = finished (play ran past the end;
 * input is the caller's again, nothing is appended). */
CE_API int32_t ce_session_movie_mode(const ce_session *s);
CE_API int64_t ce_session_movie_length(const ce_session *s);
CE_API int64_t ce_session_frame(const ce_session *s);

/* The session's own log, for saving through ce_movie_log_serialize.
 * Borrowed and READ-ONLY: mutate the movie through the session, never
 * through this. Invalidated when the session is freed. */
CE_API const ce_movie_log *ce_session_movie_log(const ce_session *s);

/* Decodes one entry against THIS session's controller: buttons_out gets the
 * pressed-button mask (bit per button in config order), axes_out (may be null)
 * gets ce_session_axis_count values in config order, each axis the entry does
 * not carry left at its declared neutral. 0 on success, nonzero when the entry
 * runs out before the controller does or an axis field will not parse.
 * The inverse - generating an entry - is what record mode does; a caller that
 * wants an entry drives the movie rather than formatting one itself. */
CE_API int32_t ce_session_movie_entry_decode(
	const ce_session *s, const char *entry, uint64_t *buttons_out, int32_t *axes_out);

/* The wide twin: states_out (may be null) gets ce_session_button_count
 * bytes, 0/1 per button in config order - the form a controller wider than
 * 64 buttons needs, and exact for any width. Same returns as above. */
CE_API int32_t ce_session_movie_entry_decode_wide(
	const ce_session *s, const char *entry, uint8_t *states_out, int32_t *axes_out);

/* One frame under the movie. In play mode the input comes from the log
 * (buttons/axes are ignored) until the log runs out, which flips the mode to
 * finished; in record and finished modes the input is the caller's - the
 * packed mask OR'd with the ce_session_set_button states, so wide
 * controllers record exactly what the machine receives - and record appends
 * the generated entry. axes may be NULL for all-neutral;
 * otherwise it carries ce_session_axis_count values in config order.
 * Returns like ce_session_frame_advance (nonzero = lag frame), or -1 on
 * error (no movie loaded, or an unparseable entry - see _last_error). */
CE_API int32_t ce_session_movie_advance(ce_session *s, uint64_t buttons, const int32_t *axes, int32_t render);

/* ---- the greenzone ----
 *
 * A budget-bounded history of whole-machine states along the movie, so a
 * seek is "restore the nearest state at or before N, replay to N" instead of
 * a replay from power-on. States are captured after every movie advance; the
 * oldest above the anchor (the first captured state, normally frame 0) are
 * evicted when the budget fills, and the anchor never is - every frame stays
 * reachable.
 */

/* Enables with a byte budget (captures the current frame immediately as the
 * anchor), or disables and drops everything with budget 0. */
CE_API void ce_session_greenzone_enable(ce_session *s, uint64_t budget_bytes);
CE_API int64_t ce_session_greenzone_count(const ce_session *s);

/* How dense the history is at each distance from the newest frame it holds,
 * all in FRAMES because the engine does not know a core's frame rate and the
 * caller does. Any argument left at 0 keeps the current value.
 *
 * Editing a movie is local, so the history is dense where the work is: every
 * frame within near_frames of the playhead, one in mid_stride across the
 * mid_frames behind that, and one in far_stride beyond. A frame is captured
 * into the near band and coarsened as the playhead leaves it behind - two
 * adjacent deltas merging into one that spans both - which costs a little work
 * every frame rather than a stall when the budget fills.
 *
 * anchor_spacing is the one that decides what a seek costs: a restore walks the
 * links of one anchor's stretch and no further, so it trades memory for
 * latency directly. A far_stride wider than it collapses old stretches to their
 * anchors, which is usually what is wanted.
 *
 * The defaults suit 60 frames a second on a core nobody has measured. What they
 * cost on a heavy one, and how to pick better, is docs/state-manager.md. */
CE_API void ce_session_greenzone_bands(ce_session *s, int64_t near_frames, int64_t mid_frames,
	int64_t mid_stride, int64_t far_stride, int64_t anchor_spacing);

/* Driving the history yourself, for a caller that owns its own movie and its
 * own frame advance - which the frontend does, and which the hooks inside
 * ce_session_movie_advance therefore never see.
 *
 * _before_advance marks the epoch and MUST be called before the machine moves,
 * since a delta is what changed since a moment and the moment has to be marked
 * first; a capture with no epoch open is simply a whole state, which is correct
 * and merely dearer. _capture stores the frame just reached - the caller says
 * which, because it is the caller counting. _restore puts the machine on a
 * stored frame, one ce_session_greenzone_nearest offered, and replaying from
 * there is then the caller's business.
 *
 * ce_session_movie_advance and ce_session_seek are these same three, for a
 * caller that lets the engine hold the movie. 0 on success. */
CE_API void ce_session_greenzone_before_advance(ce_session *s);
CE_API int32_t ce_session_greenzone_capture(
	ce_session *s, int64_t frame, const uint8_t *note, uint32_t note_len);
CE_API int32_t ce_session_greenzone_restore(ce_session *s, int64_t frame);

/* What _capture was given for `frame`, copied into `out` (which may be NULL to
 * ask only the size). Returns the note's length, or 0 when there is none.
 *
 * The note is for the caller's own bookkeeping beside the machine: a frontend
 * has a lag flag, a lag count and a frame number that a savestate does not
 * carry, and keeping those in a table of its own would mean mirroring every
 * invalidation, eviction, coarsening and spill the history does. Riding along
 * is the only way it stays true. The engine never looks inside it. */
CE_API uint32_t ce_session_greenzone_note(
	const ce_session *s, int64_t frame, uint8_t *out, uint32_t out_len);

/* Frames to keep reachable whatever the bands would otherwise do: a pinned
 * frame is never coarsened away, and a stretch holding one is spilled rather
 * than dropped. What deserves pinning is the caller's business - a marker
 * somebody wants to jump to instantly is the case this exists for, and nothing
 * the engine could work out for itself. Pinning a frame the history does not
 * hold is not an error; it takes effect if that frame is ever stored.
 * _unpin_all forgets the lot, for a caller rebuilding its set. */
CE_API void ce_session_greenzone_pin(ce_session *s, int64_t frame, int32_t pinned);
CE_API void ce_session_greenzone_unpin_all(ce_session *s);

/* Where the far band goes once the budget is full; NULL or "" for nowhere.
 *
 * The oldest stretches are the right thing to put on disk - large, rarely
 * touched, and regenerable if the file is lost - so with a directory the budget
 * costs reading them back, and without one it costs replaying to them. The
 * caller owns the directory and the file in it is the engine's, named and
 * removed by it; changing the directory throws away what was already out there.
 * A project's cache directory is what this is for. */
CE_API void ce_session_greenzone_spill(ce_session *s, const char *dir);
/* Whether putting the far band on disk has failed since the directory was set;
 * a full disk, near enough always. Setting a new directory clears it.
 *
 * The history carries on when it happens - it thins in memory instead, which
 * is the right fallback and completely silent. From a piano roll that looks
 * like the greenzone mysteriously going sparse, so somebody has to be told.
 * The engine knows; saying it is the frontend's job, which is why this is a
 * question to ask rather than a message to print. */
CE_API int32_t ce_session_greenzone_spill_failed(const ce_session *s);
/* The nearest stored frame at or before frame; -1 when none is. */
CE_API int64_t ce_session_greenzone_nearest(const ce_session *s, int64_t frame);
/* Drops stored states AFTER frame - an input edit at frame N makes every
 * later state a lie, while the state at N itself (inputs 0..N-1) still holds. */
CE_API void ce_session_greenzone_invalidate(ce_session *s, int64_t after_frame);
/* Restores the nearest stored state at or before frame, then replays the
 * movie to frame. Needs the movie's entries up to frame. 0 on success. */
CE_API int32_t ce_session_seek(ce_session *s, int64_t frame);

/* The history, kept between sessions. It is written to one file, streamed a
 * segment at a time - nothing about it is ever assembled in memory, which is
 * the whole point: the greenzone this replaces serialized itself through a
 * managed array that stops near 2GB, so a long run's history silently failed
 * to save and took the work with it.
 *
 * machine_id says which machine these states belong to. The caller owns that
 * question, knowing about cores, settings and files as it does; the engine
 * only carries the string and refuses a history that names a different one,
 * because a state is the memory of one exact machine and the sandbox checks
 * only the core binary. Loading an absent, unreadable or foreign history is
 * not an error - it leaves the history empty and costs recomputation, never
 * work, which is what a cold greenzone has always meant. 0 on success, and
 * see _last_error otherwise. */
CE_API int32_t ce_session_history_save(ce_session *s, const char *path, const char *machine_id);
CE_API int32_t ce_session_history_load(ce_session *s, const char *path, const char *machine_id);

#ifdef __cplusplus
}
#endif

#endif
