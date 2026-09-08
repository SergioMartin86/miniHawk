/* test_multifile.cpp - the .chimeraMultiFile rules, pinned.
 *
 * Round-trips creation -> open, and pins every structural rule: bare unique
 * names, known roles, at least one image, one savedata at most, cue closure,
 * hash verification with per-file status, and the canonical movie line
 * (order preserved, names percent-encoded, ACTUAL hashes recorded).
 */

#include "chimera/engine.h"

#include <cassert>
#include <cstdio>
#include <filesystem>
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

static std::string sha1Of(const std::string &content)
{
	char hex[41];
	ce_sha1_hex(reinterpret_cast<const uint8_t *>(content.data()), content.size(), hex);
	return hex;
}

static void writeDescriptor(const std::string &name, const std::string &json)
{
	writeFile(name, json);
}

static std::string entry(const std::string &name, const std::string &sha1, const std::string &role)
{
	return "{\"name\":\"" + name + "\",\"sha1\":\"" + sha1 + "\",\"role\":\"" + role + "\"}";
}

int main(int argc, char **argv)
{
	/* A directory of its own. These tests scribble files with ordinary
	 * names - save.hdd, disc2.iso - and two of them scribble the SAME
	 * names, so sharing the build directory made them race: one removed
	 * save.hdd to prove a missing file is reported missing while the
	 * other was writing it back. */
	g_dir = argc > 1 ? argv[1] : "work-multifile";
	std::filesystem::create_directories(g_dir);
	const char *err = nullptr;

	// ---- the happy path: create through the engine, then open ----
	writeFile("disc1.cue", "FILE \"disc one.bin\" BINARY\n  TRACK 01 MODE1/2048\n");
	writeFile("disc one.bin", "first disc bytes");
	writeFile("disc2.iso", "second disc bytes");
	writeFile("save.hdd", "savedata bytes");
	{
		const char *names[] = { "disc1.cue", "disc one.bin", "disc2.iso", "save.hdd" };
		const char *roles[] = { "image", "support", "image", "savedata" };
		int rc = ce_multifile_save((g_dir + "/game.chimeraMultiFile").c_str(), names, roles, 4, &err);
		assert(rc == 0);

		ce_multifile *m = ce_multifile_open((g_dir + "/game.chimeraMultiFile").c_str(), &err);
		assert(m != nullptr);
		assert(ce_multifile_count(m) == 4);
		assert(ce_multifile_ok(m) == 1);
		assert(std::strcmp(ce_multifile_name(m, 0), "disc1.cue") == 0);
		assert(std::strcmp(ce_multifile_role(m, 1), "support") == 0);
		assert(std::strcmp(ce_multifile_sha1(m, 2), sha1Of("second disc bytes").c_str()) == 0);
		assert(std::strcmp(ce_multifile_sha1(m, 2), ce_multifile_actual_sha1(m, 2)) == 0);
		assert(ce_multifile_image_count(m) == 2);
		assert(ce_multifile_image_index(m, 0) == 0);
		assert(ce_multifile_image_index(m, 1) == 2);
		assert(ce_multifile_savedata_index(m) == 3);
		uint64_t len = 0;
		const uint8_t *data = ce_multifile_data(m, 2, &len);
		assert(data != nullptr && len == std::strlen("second disc bytes"));

		// the canonical movie line: order preserved, space in a name encoded,
		// roles tagged, images untagged
		const char *line = ce_multifile_record_line(m, &len);
		assert(line != nullptr);
		std::string expect =
			"disc1.cue=" + sha1Of("FILE \"disc one.bin\" BINARY\n  TRACK 01 MODE1/2048\n") +
			" disc%20one.bin=" + sha1Of("first disc bytes") + ":support" +
			" disc2.iso=" + sha1Of("second disc bytes") +
			" save.hdd=" + sha1Of("savedata bytes") + ":savedata";
		assert(std::string(line, len) == expect);
		ce_multifile_free(m);
	}

	// ---- a hash mismatch is visible per file, and does not fail the open ----
	{
		writeFile("disc2.iso", "TAMPERED disc bytes");
		ce_multifile *m = ce_multifile_open((g_dir + "/game.chimeraMultiFile").c_str(), &err);
		assert(m != nullptr);
		assert(ce_multifile_ok(m) == 0);
		assert(ce_multifile_status(m, 2) == 2);
		assert(ce_multifile_status(m, 0) == 0);
		// the line records what was ACTUALLY loaded
		uint64_t len = 0;
		const char *line = ce_multifile_record_line(m, &len);
		assert(line != nullptr);
		assert(std::string(line, len).find("disc2.iso=" + sha1Of("TAMPERED disc bytes")) != std::string::npos);
		ce_multifile_free(m);
		writeFile("disc2.iso", "second disc bytes"); // restore
	}

	// ---- a missing file: status 1, no data, no movie line ----
	{
		std::remove((g_dir + "/save.hdd").c_str());
		ce_multifile *m = ce_multifile_open((g_dir + "/game.chimeraMultiFile").c_str(), &err);
		assert(m != nullptr);
		assert(ce_multifile_status(m, 3) == 1);
		assert(ce_multifile_data(m, 3, nullptr) == nullptr);
		assert(ce_multifile_record_line(m, nullptr) == nullptr);
		ce_multifile_free(m);
		writeFile("save.hdd", "savedata bytes");
	}

	// ---- structural rules reject the descriptor outright ----
	const std::string goodSha = sha1Of("x");
	auto expectReject = [&](const char *tag, const std::string &json)
	{
		writeDescriptor("bad.chimeraMultiFile", json);
		err = nullptr;
		ce_multifile *m = ce_multifile_open((g_dir + "/bad.chimeraMultiFile").c_str(), &err);
		if (m != nullptr)
		{
			std::fprintf(stderr, "%s: expected rejection\n", tag);
			assert(false);
		}
		assert(err != nullptr && err[0] != '\0');
	};
	expectReject("not json", "this is not json");
	expectReject("no files", "{\"files\":[]}");
	expectReject("path in name",
		"{\"files\":[" + entry("../evil.iso", goodSha, "image") + "]}");
	expectReject("unknown role",
		"{\"files\":[" + entry("disc2.iso", goodSha, "disk") + "]}");
	expectReject("duplicate name",
		"{\"files\":[" + entry("disc2.iso", goodSha, "image") + "," + entry("disc2.iso", goodSha, "image") + "]}");
	expectReject("no image",
		"{\"files\":[" + entry("disc2.iso", goodSha, "support") + "]}");
	expectReject("two savedata",
		"{\"files\":[" + entry("disc2.iso", goodSha, "image") + ","
			+ entry("save.hdd", goodSha, "savedata") + "," + entry("disc1.cue", goodSha, "savedata") + "]}");
	expectReject("bad sha1",
		"{\"files\":[" + entry("disc2.iso", "nothex", "image") + "]}");
	// cue closure: the cue is listed and readable, its bin is not listed
	expectReject("cue closure",
		"{\"files\":[" + entry("disc1.cue", sha1Of("FILE \"disc one.bin\" BINARY\n  TRACK 01 MODE1/2048\n"), "image") + "]}");

	// ---- creation-side strictness: a missing file blocks the save ----
	{
		const char *names[] = { "disc2.iso", "not-here.iso" };
		const char *roles[] = { "image", "image" };
		err = nullptr;
		int rc = ce_multifile_save((g_dir + "/bad.chimeraMultiFile").c_str(), names, roles, 2, &err);
		assert(rc != 0 && err != nullptr);
		assert(std::strstr(err, "not-here.iso") != nullptr);
	}
	// creation enforces cue closure too: the referenced bin exists in the
	// folder, but closure is about the LIST - unlisted means unhashed
	{
		const char *names[] = { "disc1.cue" };
		const char *roles[] = { "image" };
		err = nullptr;
		int rc = ce_multifile_save((g_dir + "/bad.chimeraMultiFile").c_str(), names, roles, 1, &err);
		assert(rc != 0 && err != nullptr);
		assert(std::strstr(err, "disc one.bin") != nullptr);
	}

	std::printf("multifile: all assertions passed\n");
	return 0;
}
