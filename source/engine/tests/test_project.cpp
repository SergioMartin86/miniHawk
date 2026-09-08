/* test_project.cpp - the .chimeraProject rules, pinned.
 *
 * Round-trips a full project (identity, core pin, settings, firmware, log,
 * markers, branches, files), then pins the file semantics: born-resolved
 * creation with cue auto-add, per-session resolution (by folder and by an
 * arbitrarily named path), mismatch statuses, save recording ACTUAL hashes,
 * cue closure at save, validation against a core's slot declaration, and
 * the structural rejections.
 */

#include "chimera/engine.h"

#include <cassert>
#include <cstdio>
#include <filesystem>
#include <cstdlib>
#include <cstring>
#include <string>

static std::string g_dir;

static void writeFile(const std::string &name, const std::string &content)
{
	FILE *f = std::fopen((g_dir + "/" + name).c_str(), "wb");
	assert(f != nullptr);
	std::fwrite(content.data(), 1, content.size(), f);
	std::fclose(f);
}

/* A file bigger than a 32-bit length, made sparse so it costs no disk: the
 * point is the SIZE arithmetic, not the bytes. Returns false when the
 * filesystem will not make one, which is not a failure of the project. */
static bool writeSparse(const std::string &name, uint64_t size)
{
	FILE *f = std::fopen((g_dir + "/" + name).c_str(), "wb");
	if (f == nullptr) return false;
	bool ok = fseeko(f, static_cast<off_t>(size) - 1, SEEK_SET) == 0
		&& std::fputc(0, f) != EOF;
	std::fclose(f);
	return ok;
}

static std::string sha1Of(const std::string &content)
{
	char hex[41];
	ce_sha1_hex(reinterpret_cast<const uint8_t *>(content.data()), content.size(), hex);
	return hex;
}

static const char *SLOTS_DECL =
	"{\"slots\":["
	"{\"id\":\"floppy\",\"title\":\"Floppy disks\",\"min\":0,\"max\":-1,\"formats\":[\"img\",\"ima\"]},"
	"{\"id\":\"cdrom\",\"title\":\"CD-ROMs\",\"min\":0,\"max\":-1,\"formats\":[\"iso\",\"cue\"]},"
	"{\"id\":\"hdd\",\"title\":\"Hard disk\",\"min\":0,\"max\":1,\"formats\":[\"hdd\",\"img\"]}"
	"],\"atLeastOneOf\":[[\"floppy\",\"cdrom\",\"hdd\"]]}";

int main(int argc, char **argv)
{
	/* A directory of its own. These tests scribble files with ordinary
	 * names - save.hdd, disc2.iso - and two of them scribble the SAME
	 * names, so sharing the build directory made them race: one removed
	 * save.hdd to prove a missing file is reported missing while the
	 * other was writing it back. */
	g_dir = argc > 1 ? argv[1] : "work-project";
	std::filesystem::create_directories(g_dir);
	const char *err = nullptr;
	const std::string path = g_dir + "/work.chimeraProject";

	writeFile("disc1.cue", "FILE \"disc one.bin\" BINARY\n  TRACK 01 MODE1/2048\n");
	writeFile("disc one.bin", "first disc bytes");
	writeFile("disc2.iso", "second disc bytes");
	writeFile("save.hdd", "savedata bytes");

	// ---- creation: everything set, files born resolved, cue auto-add ----
	{
		ce_project *p = ce_project_new();
		ce_project_set_title(p, "Alley Cat in 4:03");
		ce_project_set_description(p, "the funny cat game");
		ce_project_set_core(p, "dosbox-x", "abc123+local", sha1Of("a package").c_str());
		ce_project_set_rerecords(p, 421);
		assert(ce_project_set_settings_text(p, "{\"cpuCycles\":3000,\"machinePreset\":\"x\"}", &err) == 0);
		std::string firmware = "[{\"id\":\"font\",\"sha1\":\"" + sha1Of("f") + "\"}]";
		assert(ce_project_set_firmware_text(p, firmware.c_str(), &err) == 0);
		const char *lump = "LogKey:#P1 Up|\n|..|\n|U.|\n";
		ce_project_set_log_text(p, lump, std::strlen(lump));

		assert(ce_project_file_add(p, "disc1.cue", "cdrom", (g_dir + "/disc1.cue").c_str(), &err) == 0);
		// the cue brought its bin, as support, right after it
		assert(ce_project_file_count(p) == 2);
		assert(std::strcmp(ce_project_file_name(p, 1), "disc one.bin") == 0);
		assert(std::strcmp(ce_project_file_slot(p, 1), "support") == 0);
		assert(ce_project_file_status(p, 1) == 0);
		assert(ce_project_file_add(p, "disc2.iso", "cdrom", (g_dir + "/disc2.iso").c_str(), &err) == 0);
		assert(ce_project_file_add(p, "save.hdd", "hdd", (g_dir + "/save.hdd").c_str(), &err) == 0);
		assert(ce_project_files_ok(p) == 1);

		// markers keep frame order regardless of insertion order
		ce_project_marker_add(p, 500, "lap two", 1);
		ce_project_marker_add(p, 10, "power on", 0);
		ce_project_marker_add(p, 500, "lap two again", 1);
		assert(ce_project_marker_count(p) == 3);
		assert(ce_project_marker_frame(p, 0) == 10);
		assert(std::strcmp(ce_project_marker_text(p, 1), "lap two") == 0);
		assert(ce_project_marker_keep_state(p, 0) == 0);
		assert(ce_project_marker_keep_state(p, 1) == 1);

		ce_project_branch_add(p, "risky route", 400, "2026-08-26 21:00:00", "|..|\n", 5);
		ce_project_branch_marker_add(p, 0, 350, "the setup", 1);
		ce_project_subtitle_add(p, "subtitle 100 10 10 300 FFFFFFFF hello");
		ce_project_header_set(p, "Author", "sergio");
		ce_project_header_set(p, "Platform", "DOS");
		ce_project_header_set(p, "Platform", "DOSBox");
		ce_project_header_set(p, "Doomed", "yes");
		ce_project_header_set(p, "Doomed", nullptr);

		assert(ce_project_validate(p, SLOTS_DECL, std::strlen(SLOTS_DECL), &err) == 0);
		assert(ce_project_save(p, path.c_str(), &err) == 0);
		ce_project_free(p);
	}

	// ---- open: everything round-trips; files come back unresolved ----
	{
		ce_project *p = ce_project_open(path.c_str(), &err);
		assert(p != nullptr);
		assert(std::strcmp(ce_project_title(p), "Alley Cat in 4:03") == 0);
		assert(std::strcmp(ce_project_description(p), "the funny cat game") == 0);
		assert(std::strcmp(ce_project_core_name(p), "dosbox-x") == 0);
		assert(std::strcmp(ce_project_core_version(p), "abc123+local") == 0);
		assert(std::strcmp(ce_project_core_sha1(p), sha1Of("a package").c_str()) == 0);
		assert(ce_project_rerecords(p) == 421);
		uint64_t len = 0;
		assert(std::string(ce_project_settings_text(p, &len)).find("\"cpuCycles\":3000") != std::string::npos);
		assert(std::string(ce_project_firmware_text(p, &len)).find("\"font\"") != std::string::npos);
		const char *logText = ce_project_log_text(p, &len);
		assert(std::string(logText, len) == "LogKey:#P1 Up|\n|..|\n|U.|\n");
		assert(ce_project_marker_count(p) == 3 && ce_project_marker_frame(p, 2) == 500);
		assert(ce_project_branch_count(p) == 1);
		assert(std::strcmp(ce_project_branch_name(p, 0), "risky route") == 0);
		assert(ce_project_branch_frame(p, 0) == 400);
		assert(std::strcmp(ce_project_branch_time(p, 0), "2026-08-26 21:00:00") == 0);
		const char *branchLog = ce_project_branch_log_text(p, 0, &len);
		assert(std::string(branchLog, len) == "|..|\n");
		assert(ce_project_branch_marker_count(p, 0) == 1);
		assert(ce_project_branch_marker_frame(p, 0, 0) == 350);
		assert(std::strcmp(ce_project_branch_marker_text(p, 0, 0), "the setup") == 0);
		assert(ce_project_subtitle_count(p) == 1);
		assert(std::strcmp(ce_project_subtitle_at(p, 0), "subtitle 100 10 10 300 FFFFFFFF hello") == 0);
		assert(ce_project_header_count(p) == 2);
		assert(std::strcmp(ce_project_header_key_at(p, 0), "Author") == 0);
		assert(std::strcmp(ce_project_header_get(p, "Platform"), "DOSBox") == 0);
		assert(ce_project_header_get(p, "Doomed") == nullptr);

		assert(ce_project_file_count(p) == 4);
		assert(ce_project_file_status(p, 0) == 1);
		assert(ce_project_file_size(p, 0) == 0);            // unresolved: nothing known yet
		assert(ce_project_file_source_path(p, 0)[0] == '\0');
		assert(ce_project_files_ok(p) == 0);
		assert(std::strcmp(ce_project_file_sha1(p, 2), sha1Of("second disc bytes").c_str()) == 0);

		// resolution by folder: the "files beside the project" convenience
		assert(ce_project_resolve_dir(p, g_dir.c_str()) == 4);
		assert(ce_project_files_ok(p) == 1);
		// a resolved file says how big it is and WHERE it is; the bytes stay on
		// the disk until a machine asks for them
		assert(ce_project_file_size(p, 2) == std::strlen("second disc bytes"));
		assert(std::string(ce_project_file_source_path(p, 2)) == g_dir + "/disc2.iso");

		// the slot map the session mounts: manifest order, support excluded
		const char *slots = ce_project_slots_text(p, &len);
		assert(std::string(slots, len) ==
			"{\"cdrom\":[\"disc1.cue\",\"disc2.iso\"],\"hdd\":[\"save.hdd\"]}");
		ce_project_free(p);
	}

	// ---- resolution by path: the on-disk name may differ ----
	{
		writeFile("renamed-elsewhere.iso", "second disc bytes");
		ce_project *p = ce_project_open(path.c_str(), &err);
		assert(p != nullptr);
		assert(ce_project_file_resolve(p, 2, (g_dir + "/renamed-elsewhere.iso").c_str(), &err) == 0);
		assert(ce_project_file_status(p, 2) == 0);
		assert(std::strcmp(ce_project_file_name(p, 2), "disc2.iso") == 0); // the canonical name is the label
		// an unreadable path is an error, not a status
		assert(ce_project_file_resolve(p, 3, (g_dir + "/not-there").c_str(), &err) != 0);
		assert(err != nullptr && ce_project_file_status(p, 3) == 1);
		ce_project_free(p);
	}

	// ---- a mismatch is a status, and save records what actually ran ----
	{
		writeFile("disc2.iso", "TAMPERED disc bytes");
		ce_project *p = ce_project_open(path.c_str(), &err);
		assert(p != nullptr);
		ce_project_resolve_dir(p, g_dir.c_str());
		assert(ce_project_file_status(p, 2) == 2);
		assert(ce_project_files_ok(p) == 0);
		assert(std::strcmp(ce_project_file_actual_sha1(p, 2), sha1Of("TAMPERED disc bytes").c_str()) == 0);
		assert(ce_project_save(p, (g_dir + "/overridden.chimeraProject").c_str(), &err) == 0);
		ce_project_free(p);

		ce_project *q = ce_project_open((g_dir + "/overridden.chimeraProject").c_str(), &err);
		assert(q != nullptr);
		assert(std::strcmp(ce_project_file_sha1(q, 2), sha1Of("TAMPERED disc bytes").c_str()) == 0);
		ce_project_free(q);
		writeFile("disc2.iso", "second disc bytes"); // restore
	}

	// ---- validation against the declaration ----
	{
		ce_project *p = ce_project_new();
		// unknown slot
		assert(ce_project_file_add(p, "disc2.iso", "tape", (g_dir + "/disc2.iso").c_str(), &err) == 0);
		assert(ce_project_validate(p, SLOTS_DECL, std::strlen(SLOTS_DECL), &err) != 0);
		assert(std::strstr(err, "tape") != nullptr);
		ce_project_file_remove(p, 0);
		// wrong format for the slot
		assert(ce_project_file_add(p, "disc2.iso", "floppy", (g_dir + "/disc2.iso").c_str(), &err) == 0);
		assert(ce_project_validate(p, SLOTS_DECL, std::strlen(SLOTS_DECL), &err) != 0);
		ce_project_file_remove(p, 0);
		// cardinality: two hdds against max 1
		assert(ce_project_file_add(p, "save.hdd", "hdd", (g_dir + "/save.hdd").c_str(), &err) == 0);
		assert(ce_project_file_add(p, "save2.hdd", "hdd", (g_dir + "/save.hdd").c_str(), &err) == 0);
		assert(ce_project_validate(p, SLOTS_DECL, std::strlen(SLOTS_DECL), &err) != 0);
		ce_project_file_remove(p, 1);
		assert(ce_project_validate(p, SLOTS_DECL, std::strlen(SLOTS_DECL), &err) == 0);
		ce_project_file_remove(p, 0);
		// nothing at all: the atLeastOneOf group speaks
		assert(ce_project_validate(p, SLOTS_DECL, std::strlen(SLOTS_DECL), &err) != 0);
		assert(std::strstr(err, "floppy, cdrom, hdd") != nullptr);
		// a min is enforced
		const char *needOne = "{\"slots\":[{\"id\":\"rom\",\"min\":1,\"max\":1}]}";
		assert(ce_project_validate(p, needOne, std::strlen(needOne), &err) != 0);
		// mutually exclusive slots: files in a slot the manifest itself makes
		// unavailable are structurally invalid
		const char *exclusive =
			"{\"slots\":["
			"{\"id\":\"cart\",\"formats\":[\"iso\"],\"exposedWhen\":{\"not\":{\"slot\":\"fdsx\"}}},"
			"{\"id\":\"fdsx\",\"formats\":[\"hdd\"],\"exposedWhen\":{\"not\":{\"slot\":\"cart\"}}}]}";
		assert(ce_project_file_add(p, "disc2.iso", "cart", (g_dir + "/disc2.iso").c_str(), &err) == 0);
		assert(ce_project_validate(p, exclusive, std::strlen(exclusive), &err) == 0);
		assert(ce_project_file_add(p, "save.hdd", "fdsx", (g_dir + "/save.hdd").c_str(), &err) == 0);
		assert(ce_project_validate(p, exclusive, std::strlen(exclusive), &err) != 0);
		assert(std::strstr(err, "unavailable") != nullptr);
		ce_project_file_remove(p, 1);
		assert(ce_project_validate(p, exclusive, std::strlen(exclusive), &err) == 0);
		// an unexposed slot's minimum does not bind: "cart min 1 unless
		// fdsx" plus "fdsx min 1 unless cart" means exactly one of either
		const char *eitherOne =
			"{\"slots\":["
			"{\"id\":\"cart\",\"min\":1,\"max\":1,\"formats\":[\"iso\"],\"exposedWhen\":{\"not\":{\"slot\":\"fdsx\"}}},"
			"{\"id\":\"fdsx\",\"min\":1,\"max\":1,\"formats\":[\"hdd\"],\"exposedWhen\":{\"not\":{\"slot\":\"cart\"}}}]}";
		assert(ce_project_validate(p, eitherOne, std::strlen(eitherOne), &err) == 0);
		ce_project_file_remove(p, 0);
		ce_project_free(p);
	}

	// ---- creation-side errors ----
	{
		ce_project *p = ce_project_new();
		assert(ce_project_file_add(p, "disc2.iso", "cdrom", (g_dir + "/disc2.iso").c_str(), &err) == 0);
		// duplicates, unreadable sources, bad names and slots
		assert(ce_project_file_add(p, "disc2.iso", "cdrom", (g_dir + "/disc2.iso").c_str(), &err) != 0);
		assert(ce_project_file_add(p, "gone.iso", "cdrom", (g_dir + "/gone.iso").c_str(), &err) != 0);
		assert(ce_project_file_add(p, "../evil.iso", "cdrom", (g_dir + "/disc2.iso").c_str(), &err) != 0);
		assert(ce_project_file_add(p, "disc3.iso", "CDROM", (g_dir + "/disc2.iso").c_str(), &err) != 0);
		// a cue whose bin is not next to it: all or nothing
		writeFile("lonely.cue", "FILE \"nowhere.bin\" BINARY\n  TRACK 01 MODE1/2048\n");
		assert(ce_project_file_add(p, "lonely.cue", "cdrom", (g_dir + "/lonely.cue").c_str(), &err) != 0);
		assert(std::strstr(err, "nowhere.bin") != nullptr);
		assert(ce_project_file_count(p) == 1);
		ce_project_free(p);
	}

	// ---- cue closure at save ----
	{
		ce_project *p = ce_project_new();
		assert(ce_project_file_add(p, "disc1.cue", "cdrom", (g_dir + "/disc1.cue").c_str(), &err) == 0);
		ce_project_file_remove(p, 1); // drop the bin the cue brought
		assert(ce_project_save(p, (g_dir + "/bad.chimeraProject").c_str(), &err) != 0);
		assert(std::strstr(err, "disc one.bin") != nullptr);
		ce_project_free(p);
	}

	// ---- structural rejections at open ----
	auto expectReject = [&](const char *tag, const std::string &json)
	{
		writeFile("bad.chimeraProject", json);
		err = nullptr;
		ce_project *p = ce_project_open((g_dir + "/bad.chimeraProject").c_str(), &err);
		if (p != nullptr)
		{
			std::fprintf(stderr, "%s: expected rejection\n", tag);
			assert(false);
		}
		assert(err != nullptr && err[0] != '\0');
	};
	const std::string goodSha = sha1Of("x");
	expectReject("not json", "so very not json");
	expectReject("not an object", "[1,2,3]");
	expectReject("unknown key", "{\"totallyNew\":1}");
	expectReject("title type", "{\"title\":5}");
	expectReject("core incomplete", "{\"core\":{\"name\":\"x\"}}");
	expectReject("core sha malformed", "{\"core\":{\"name\":\"x\",\"version\":\"v\",\"sha1\":\"nope\"}}");
	expectReject("file path name",
		"{\"files\":[{\"name\":\"a/b.iso\",\"sha1\":\"" + goodSha + "\",\"slot\":\"cdrom\"}]}");
	expectReject("file dup",
		"{\"files\":[{\"name\":\"a.iso\",\"sha1\":\"" + goodSha + "\",\"slot\":\"cdrom\"},"
		"{\"name\":\"a.iso\",\"sha1\":\"" + goodSha + "\",\"slot\":\"cdrom\"}]}");
	expectReject("file bad sha",
		"{\"files\":[{\"name\":\"a.iso\",\"sha1\":\"nothex\",\"slot\":\"cdrom\"}]}");
	expectReject("file bad slot",
		"{\"files\":[{\"name\":\"a.iso\",\"sha1\":\"" + goodSha + "\",\"slot\":\"CD ROM\"}]}");
	expectReject("settings type", "{\"settings\":[]}");
	expectReject("branch incomplete", "{\"branches\":[{\"name\":\"x\"}]}");

	// an empty project is structurally fine (validation is the core's say)
	{
		writeFile("empty.chimeraProject", "{}");
		ce_project *p = ce_project_open((g_dir + "/empty.chimeraProject").c_str(), &err);
		assert(p != nullptr);
		assert(ce_project_file_count(p) == 0 && ce_project_rerecords(p) == 0);
		uint64_t len = 0;
		assert(std::string(ce_project_settings_text(p, &len)) == "{}");
		ce_project_free(p);
	}

	// the cue parser, exported: what a form shows as "(+ N tracks)"
	{
		const char *cue =
			"FILE \"track01.bin\" BINARY\n  TRACK 01 MODE1/2352\nFILE track02.bin BINARY\n";
		uint64_t len = 0;
		const char *refs = ce_cue_references(cue, std::strlen(cue), &len);
		assert(std::string(refs, len) == "[\"track01.bin\",\"track02.bin\"]");
		refs = ce_cue_references("not a cue at all", 16, &len);
		assert(std::string(refs, len) == "[]");
	}

	// A file no byte[] could hold, and no 32-bit length could count.
	//
	// This is the whole reason a project keeps paths rather than bytes: a PS2
	// disc is over four gigabytes. What is checked is that the size survives
	// intact (a truncation to 32 bits would show here and nowhere else) and
	// that adding it costs nothing but the hash - the file is sparse, so a
	// build that read it whole would have to find 5GB of memory to do it.
	//
	// OPT-IN, because it hashes five gigabytes and takes half a minute, and
	// this gate runs on every build: CHIMERA_BIG_FILE_TEST=1 to include it.
	if (std::getenv("CHIMERA_BIG_FILE_TEST") != nullptr)
	{
		const uint64_t huge = 5ULL << 30;
		if (writeSparse("huge.iso", huge))
		{
			ce_project *p = ce_project_new();
			const char *err = nullptr;
			assert(ce_project_file_add(p, "huge.iso", "disc", (g_dir + "/huge.iso").c_str(), &err) == 0);
			assert(ce_project_file_size(p, 0) == huge);
			assert(ce_project_file_status(p, 0) == 0);
			assert(std::string(ce_project_file_source_path(p, 0)) == g_dir + "/huge.iso");
			ce_project_free(p);
			std::remove((g_dir + "/huge.iso").c_str());
		}
		else
		{
			std::printf("project: skipped the 5GB entry (no sparse file here)\n");
		}
	}

	std::printf("project: all assertions passed\n");
	return 0;
}
