#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Chimera.NativeInvoke;
using Chimera.Common;

namespace Chimera.Emulation.Common.Engine
{
	/// <summary>
	/// NativeInvoke surface of libchimera - the engine (engine/include/chimera/engine.h),
	/// which ships beside libminiboxhost in build/dll. The functional side of the
	/// frontend migrates into it component by component; see docs/engine-migration.md.
	/// </summary>
	public abstract class LibChimera
	{
		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract uint ce_abi_version();

		/// <summary>JSON: what built this engine (commit, compiler, OS, target).</summary>
		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_build_info();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_log_new();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_free(IntPtr log);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_movie_log_parse(IntPtr log, byte[] text, ulong len);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_log_last_error(IntPtr log);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_movie_log_count(IntPtr log);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_log_entry(IntPtr log, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_add(IntPtr log, string entry);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_clear(IntPtr log);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_truncate(IntPtr log, long count);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_set(IntPtr log, long index, string entry);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_insert(IntPtr log, long index, string entry);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_remove_range(IntPtr log, long index, long count);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_assign(IntPtr dst, IntPtr src);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_movie_log_has_state_frame(IntPtr log);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_movie_log_state_frame(IntPtr log);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_log_key(IntPtr log);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_log_set_key(IntPtr log, string? key);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_movie_log_divergent_point(IntPtr a, IntPtr b);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_log_serialize(IntPtr log, int crlf, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_header_new();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_header_free(IntPtr header);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_header_parse(IntPtr header, byte[] text, ulong len);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_movie_header_count(IntPtr header);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_header_key_at(IntPtr header, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_header_value_at(IntPtr header, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_movie_header_set(IntPtr header, string key, string value);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_movie_header_serialize(IntPtr header, int crlf, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_text_lines_new();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_text_lines_free(IntPtr lines);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_text_lines_parse(IntPtr lines, byte[] text, ulong len);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_text_lines_count(IntPtr lines);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_text_lines_at(IntPtr lines, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_text_lines_add(IntPtr lines, string line);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_text_lines_serialize(IntPtr lines, int crlf, ref ulong lenOut);

		[StructLayout(LayoutKind.Sequential)]
		public struct CeSubtitleFields
		{
			public int Frame;
			public int X;
			public int Y;
			public int Duration;
			public uint Color;
		}

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_subtitle_parse_line(string line, ref CeSubtitleFields fields, byte[] messageBuf, ulong cap);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_subtitle_format_line(ref CeSubtitleFields fields, string message, byte[] buf, ulong cap);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_state_writer_new(int compressionLevel, string emuVersion);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_state_writer_free(IntPtr writer);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_state_writer_put_lump(IntPtr writer, string name, string ext, int zstd, byte[] data, ulong len);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_state_writer_finish(IntPtr writer, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_state_writer_last_error(IntPtr writer);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_state_reader_open(byte[] data, ulong len, int isMovie, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_state_reader_free(IntPtr reader);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_state_reader_version(IntPtr reader);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_state_reader_lump(IntPtr reader, string name, string ext, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_state_reader_last_error(IntPtr reader);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_sha1_hex(byte[] data, ulong len, byte[] out41);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_sha1_file(string utf8Path, byte[] out41, ref ulong lenOut);



















		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_firmware_state(long declaredSize, string expectedSha1s, long actualSize, string actualSha1);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_firmware_record_line(string pairs, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_firmware_evaluate(
			byte[] declJson, ulong declLen,
			byte[] slotsJson, ulong slotsLen,
			byte[] settingsJson, ulong settingsLen,
			ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_settings_evaluate(
			byte[] declJson, ulong declLen,
			byte[] slotsJson, ulong slotsLen,
			byte[] settingsJson, ulong settingsLen,
			ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_slots_evaluate(
			byte[] declJson, ulong declLen,
			byte[] slotsJson, ulong slotsLen,
			byte[] settingsJson, ulong settingsLen,
			ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_cue_references(
			byte[] cueBytes, ulong cueLen,
			ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_package_open(string path, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_package_free(IntPtr package);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_package_sha1(IntPtr package);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_package_is_waterbox(IntPtr package);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_package_has_entry(IntPtr package, string name);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_package_entry(IntPtr package, string name, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_package_last_error(IntPtr package);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_new();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_open(string path, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_save(IntPtr project, string path, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_free(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_id(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_title(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_set_title(IntPtr project, string title);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_description(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_set_description(IntPtr project, string description);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_core_name(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_core_version(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_core_sha1(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_set_core(IntPtr project, string name, string version, string sha1);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract ulong ce_project_rerecords(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_set_rerecords(IntPtr project, ulong count);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_settings_text(IntPtr project, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_set_settings_text(IntPtr project, string json, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_firmware_text(IntPtr project, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_set_firmware_text(IntPtr project, string json, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_core_cache_text(IntPtr project, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_set_core_cache_text(IntPtr project, string json, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_log_text(IntPtr project, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_set_log_text(IntPtr project, byte[] text, ulong len);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_marker_count(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_project_marker_frame(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_marker_text(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_marker_keep_state(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_marker_add(IntPtr project, long frame, string text, int keepState);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_marker_remove(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_branch_count(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_branch_name(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_project_branch_frame(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_branch_time(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_branch_log_text(IntPtr project, int index, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_branch_add(IntPtr project, string name, long frame, string time, byte[] logText, ulong len);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_branch_remove(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_branch_marker_count(IntPtr project, int branch);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_project_branch_marker_frame(IntPtr project, int branch, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_branch_marker_text(IntPtr project, int branch, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_branch_marker_keep_state(IntPtr project, int branch, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_branch_marker_add(IntPtr project, int branch, long frame, string text, int keepState);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_header_count(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_header_key_at(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_header_value_at(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_header_get(IntPtr project, string key);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_header_set(IntPtr project, string key, string? value);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_subtitle_count(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_subtitle_at(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_subtitle_add(IntPtr project, string line);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_subtitle_remove(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_file_add(IntPtr project, string name, string slot, string sourcePath, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_file_remove(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_file_count(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_file_name(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_file_slot(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_file_sha1(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_file_actual_sha1(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_file_status(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_file_source_path(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract ulong ce_project_file_size(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_file_resolve(IntPtr project, int index, string path, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_project_file_unresolve(IntPtr project, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_resolve_dir(IntPtr project, string dir);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_files_ok(IntPtr project);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_project_validate(IntPtr project, byte[] slotsJson, ulong slotsLen, ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_project_slots_text(IntPtr project, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_open(
			string packagePath, byte[]? rom, ulong romLen, string? romPath, string? settingsOverridesJson,
			IntPtr[]? firmwareIds, IntPtr[]? firmwareData, ulong[]? firmwareLens, int firmwareCount,
			IntPtr[]? extraNames, IntPtr[]? extraData, ulong[]? extraLens, IntPtr[]? extraPaths, int extraCount,
			ref IntPtr errorOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_session_free(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_core_name(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_system_id(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_width(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_height(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_virtual_width(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_virtual_height(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_vsync_numerator(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_vsync_denominator(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_samples_per_frame(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_channels(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_deterministic(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_gpu_drew(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_gl_description();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_gl_request(int want);

		/// <summary>the progress sink (see <see cref="EngineProgress"/>); fn is a Cdecl (stage, done, total, user) function pointer, or zero to stop</summary>
		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_progress_set(IntPtr fn, IntPtr user);

		// the compile cache and precompile sessions (docs: a core's compiled
		// objects, kept on the host; never machine state)
		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_cache_dir([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_precompile_request(int index, int count, int firmwareToo);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract ulong ce_session_cache_stored(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract ulong ce_session_cache_fetched(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_precompile_done(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_precompile_progress(IntPtr session, out uint done, out uint total);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_gl_available();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_button_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_button_name(IntPtr session, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_axis_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_axis_name(IntPtr session, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_drive_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_drive_name(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_drive_light(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_button_active(IntPtr session, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_axis_active(IntPtr session, long index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_session_set_axis(IntPtr session, int index, int value);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_frame_advance(IntPtr session, ulong buttons, int render);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_video(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_video_width(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_video_height(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_audio(IntPtr session, ref int sampleCount);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_save_state(IntPtr session, ref ulong lenOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_load_state(IntPtr session, byte[] data, ulong len);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_domain_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_domain_name(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_domain_size(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_domain_writable(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract ulong ce_session_domain_ptr(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_last_error(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_host_build_info();

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_apply_settings(IntPtr session, string overridesJson);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_surface_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_surface_name(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_surface_width(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_surface_height(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_surface_render(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_register_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_register_name(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_register_bits(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_register_value(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_register_set(IntPtr session, int index, long value);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_has_executed_cycles(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_executed_cycles(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_bus_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_bus_name(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_bus_size(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_bus_writable(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_bus_peek(IntPtr session, int index, int addr);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_session_bus_poke(IntPtr session, int index, int addr, int value);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_trace_available(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_trace_header(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_session_trace_enable(IntPtr session, int on);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_trace_drain(IntPtr session, ref ulong lenOut, ref int lineCountOut, ref int overflowOut);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract void ce_session_set_button(IntPtr session, int index, int pressed);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_savedata_available(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract int ce_session_savedata_count(IntPtr session);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract IntPtr ce_session_savedata_name(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_savedata_size(IntPtr session, int index);

		[ChimeraImport(CallingConvention.Cdecl)]
		public abstract long ce_session_savedata_read(IntPtr session, int index, long offset, byte[] buf, long len);





	}

	public static class ChimeraEngine
	{
		private static readonly Lazy<LibChimera> _instance = new(Load, isThreadSafe: true);

		private static readonly string LibName = $"libchimera{(OSTailoredCode.IsUnixHost ? ".so" : ".dll")}";

		public static LibChimera Instance => _instance.Value;

		private static LibChimera Load()
		{
			DynamicLibraryImportResolver resolver;
			try
			{
				resolver = new(LibName, hasLimitedLifetime: false);
			}
			catch (Exception)
			{
				// dev tree fallback: tests and ad-hoc runs have no LD_LIBRARY_PATH /
				// SetDllDirectory pointing at build/dll, so walk up and look for it
				var found = FindInDevTree();
				if (found is null) throw;
				resolver = new(found, hasLimitedLifetime: false);
			}
			var lib = ChimeraInvoker.GetInvoker<LibChimera>(resolver, CallingConventionAdapters.Native);
			var abi = lib.ce_abi_version();
			if (abi != 1)
			{
				throw new InvalidOperationException($"{LibName} speaks engine ABI v{abi}; this frontend speaks v1");
			}
			EngineProgress.Install(lib);
			return lib;
		}

		private static string? FindInDevTree()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			{
				var candidate = Path.Combine(dir.FullName, "build", "dll", LibName);
				if (File.Exists(candidate)) return candidate;
			}
			return null;
		}

		/// <summary>Engine build provenance (JSON), for the frontend to show and movies to record.</summary>
		public static string BuildInfo => PtrToStringUtf8(Instance.ce_build_info()) ?? "{}";

		/// <summary>The identity hash the frontend uses everywhere: SHA1, 40 uppercase hex chars.</summary>
		public static string Sha1Hex(byte[] data)
		{
			var buf = new byte[41];
			Instance.ce_sha1_hex(data, (ulong)data.LongLength, buf);
			return Encoding.ASCII.GetString(buf, 0, 40);
		}

		/// <summary>
		/// The same hash of a file, taken by reading it rather than by holding
		/// it: firmware can be a disk image, and one that does not fit in memory
		/// still has an identity. Null when the file cannot be read.
		/// </summary>
		public static (string Sha1, long Length)? Sha1OfFile(string path)
		{
			var buf = new byte[41];
			ulong len = 0;
			if (Instance.ce_sha1_file(path, buf, ref len) is 0) return null;
			return (Encoding.ASCII.GetString(buf, 0, 40), (long)len);
		}

		public static unsafe string? PtrToStringUtf8(IntPtr p)
		{
			if (p == IntPtr.Zero) return null;
			var bytes = (byte*)p;
			var len = 0;
			while (bytes[len] != 0) len++;
			return Encoding.UTF8.GetString(bytes, len);
		}

		public static unsafe string PtrToStringUtf8(IntPtr p, ulong len)
			=> Encoding.UTF8.GetString((byte*)p, checked((int)len));
	}

	/// <summary>
	/// A movie's [Input] lump, held by the engine: the frame entries, the LogKey,
	/// and - when parsed from a savestate - the frame the state was taken at.
	/// Policy around it (truncation, rerecords, movie mode) stays with the caller.
	/// </summary>
	public sealed class EngineMovieLog : IDisposable
	{
		private IntPtr _log;

		/// <summary>For engine-to-engine operations (assign, divergence) and the session.</summary>
		public IntPtr Handle => _log;

		public EngineMovieLog() => _log = ChimeraEngine.Instance.ce_movie_log_new();

		// the backstop for logs that are simply dropped (undo history, branches);
		// freeing an unreferenced log is safe from any thread
		~EngineMovieLog() => Free();

		public void Dispose()
		{
			Free();
			GC.SuppressFinalize(this);
		}

		private void Free()
		{
			if (_log == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_movie_log_free(_log);
			_log = IntPtr.Zero;
		}

		public bool Parse(string text, out string errorMessage)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			if (ChimeraEngine.Instance.ce_movie_log_parse(_log, bytes, (ulong)bytes.LongLength) is 0)
			{
				errorMessage = "";
				return true;
			}
			errorMessage = ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_movie_log_last_error(_log)) ?? "engine error";
			return false;
		}

		public long Count => ChimeraEngine.Instance.ce_movie_log_count(_log);

		public string this[long index]
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_movie_log_entry(_log, index))
				?? throw new ArgumentOutOfRangeException(nameof(index));

		public void Add(string entry) => ChimeraEngine.Instance.ce_movie_log_add(_log, entry);

		public void Set(long index, string entry) => ChimeraEngine.Instance.ce_movie_log_set(_log, index, entry);

		public void Insert(long index, string entry) => ChimeraEngine.Instance.ce_movie_log_insert(_log, index, entry);

		public void RemoveRange(long index, long count) => ChimeraEngine.Instance.ce_movie_log_remove_range(_log, index, count);

		public void Truncate(long count) => ChimeraEngine.Instance.ce_movie_log_truncate(_log, count);

		public void Clear() => ChimeraEngine.Instance.ce_movie_log_clear(_log);

		/// <summary>Replaces this log's entries and LogKey with another's, engine-side.</summary>
		public void AssignFrom(EngineMovieLog source) => ChimeraEngine.Instance.ce_movie_log_assign(_log, source.Handle);

		/// <summary>First frame where the logs differ; the shorter length when one is a prefix; null when identical.</summary>
		public long? DivergentPoint(EngineMovieLog other)
		{
			var result = ChimeraEngine.Instance.ce_movie_log_divergent_point(_log, other.Handle);
			return result < 0 ? null : result;
		}

		public bool HasStateFrame => ChimeraEngine.Instance.ce_movie_log_has_state_frame(_log) is not 0;

		public int StateFrame => ChimeraEngine.Instance.ce_movie_log_state_frame(_log);

		public string? Key
		{
			get => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_movie_log_key(_log));
			set => ChimeraEngine.Instance.ce_movie_log_set_key(_log, value);
		}

		/// <summary>The whole [Input] block, ready to write into a movie file.</summary>
		public string Serialize(bool crlf)
		{
			ulong len = 0;
			var p = ChimeraEngine.Instance.ce_movie_log_serialize(_log, crlf ? 1 : 0, ref len);
			return ChimeraEngine.PtrToStringUtf8(p, len);
		}
	}

	/// <summary>
	/// A movie's Header.txt lump, held by the engine: ordered "Key Value" pairs.
	/// First occurrence of a key wins on parse; insertion order is kept on write.
	/// </summary>
	public sealed class EngineMovieHeader : IDisposable
	{
		private IntPtr _header;

		public EngineMovieHeader() => _header = ChimeraEngine.Instance.ce_movie_header_new();

		public void Dispose()
		{
			if (_header == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_movie_header_free(_header);
			_header = IntPtr.Zero;
		}

		public void Parse(string text)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			ChimeraEngine.Instance.ce_movie_header_parse(_header, bytes, (ulong)bytes.LongLength);
		}

		public long Count => ChimeraEngine.Instance.ce_movie_header_count(_header);

		public (string Key, string Value) this[long index]
		{
			get
			{
				var key = ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_movie_header_key_at(_header, index))
					?? throw new ArgumentOutOfRangeException(nameof(index));
				var value = ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_movie_header_value_at(_header, index))!;
				return (key, value);
			}
		}

		public void Set(string key, string value) => ChimeraEngine.Instance.ce_movie_header_set(_header, key, value);

		/// <summary>The whole lump, closing blank line included.</summary>
		public string Serialize(bool crlf)
		{
			ulong len = 0;
			var p = ChimeraEngine.Instance.ce_movie_header_serialize(_header, crlf ? 1 : 0, ref len);
			return ChimeraEngine.PtrToStringUtf8(p, len);
		}
	}

	/// <summary>
	/// A plain-lines lump (Comments.txt and friends), held by the engine: every
	/// non-blank line in order, duplicates included.
	/// </summary>
	public sealed class EngineTextLines : IDisposable
	{
		private IntPtr _lines;

		public EngineTextLines() => _lines = ChimeraEngine.Instance.ce_text_lines_new();

		public void Dispose()
		{
			if (_lines == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_text_lines_free(_lines);
			_lines = IntPtr.Zero;
		}

		public void Parse(string text)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			ChimeraEngine.Instance.ce_text_lines_parse(_lines, bytes, (ulong)bytes.LongLength);
		}

		public long Count => ChimeraEngine.Instance.ce_text_lines_count(_lines);

		public string this[long index]
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_text_lines_at(_lines, index))
				?? throw new ArgumentOutOfRangeException(nameof(index));

		public void Add(string line) => ChimeraEngine.Instance.ce_text_lines_add(_lines, line);

		/// <summary>The whole lump, closing blank line included.</summary>
		public string Serialize(bool crlf)
		{
			ulong len = 0;
			var p = ChimeraEngine.Instance.ce_text_lines_serialize(_lines, crlf ? 1 : 0, ref len);
			return ChimeraEngine.PtrToStringUtf8(p, len);
		}
	}

	/// <summary>
	/// Writes the zip-of-lumps container savestates and movies share. The engine
	/// owns the format; the caller owns the file - Finish hands back the whole
	/// archive as one buffer. Failures poison the writer and surface at Finish,
	/// which is how the old zip writer behaved.
	/// </summary>
	public sealed class EngineStateWriter : IDisposable
	{
		private IntPtr _writer;

		public EngineStateWriter(int compressionLevel, string emuVersion)
			=> _writer = ChimeraEngine.Instance.ce_state_writer_new(compressionLevel, emuVersion);

		public void Dispose()
		{
			if (_writer == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_state_writer_free(_writer);
			_writer = IntPtr.Zero;
		}

		public bool PutLump(string name, string ext, bool zstd, byte[] data)
			=> ChimeraEngine.Instance.ce_state_writer_put_lump(_writer, name, ext ?? "", zstd ? 1 : 0, data, (ulong)data.LongLength) is 0;

		public string LastError
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_state_writer_last_error(_writer)) ?? "";

		/// <summary>The finished archive.</summary>
		/// <exception cref="IOException">a lump failed earlier, or finalizing failed</exception>
		public byte[] Finish()
		{
			ulong len = 0;
			var p = ChimeraEngine.Instance.ce_state_writer_finish(_writer, ref len);
			if (p == IntPtr.Zero) throw new IOException($"engine could not write the archive: {LastError}");
			var ret = new byte[len];
			Marshal.Copy(p, ret, 0, checked((int)len));
			return ret;
		}
	}

	/// <summary>
	/// Reads the zip-of-lumps container. Lumps come back decompressed; a missing
	/// lump is null, a broken one throws.
	/// </summary>
	public sealed class EngineStateReader : IDisposable
	{
		private IntPtr _reader;

		private EngineStateReader(IntPtr reader) => _reader = reader;

		/// <returns>null when the bytes are not a readable container (same cases the old loader returned null for)</returns>
		/// <exception cref="Exception">the container is corrupt (e.g. duplicate lump names)</exception>
		public static EngineStateReader? Open(byte[] data, bool isMovie)
		{
			var error = IntPtr.Zero;
			var reader = ChimeraEngine.Instance.ce_state_reader_open(data, (ulong)data.LongLength, isMovie ? 1 : 0, ref error);
			if (reader != IntPtr.Zero) return new(reader);
			if (error != IntPtr.Zero) throw new Exception(ChimeraEngine.PtrToStringUtf8(error));
			return null;
		}

		public void Dispose()
		{
			if (_reader == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_state_reader_free(_reader);
			_reader = IntPtr.Zero;
		}

		/// <summary>The sub-version from the "ChimeraState 1.0" lump (1.0.N).</summary>
		public int Version => ChimeraEngine.Instance.ce_state_reader_version(_reader);

		/// <returns>the decompressed lump, or null when absent</returns>
		/// <exception cref="Exception">the lump exists but cannot be read</exception>
		public byte[]? Lump(string name, string? ext)
		{
			ulong len = 0;
			var p = ChimeraEngine.Instance.ce_state_reader_lump(_reader, name, ext ?? "", ref len);
			if (p == IntPtr.Zero)
			{
				var error = ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_state_reader_last_error(_reader));
				if (!string.IsNullOrEmpty(error)) throw new Exception(error);
				return null;
			}
			var ret = new byte[len];
			Marshal.Copy(p, ret, 0, checked((int)len));
			return ret;
		}
	}

	/// <summary>
	/// A core package's container, held by the engine: zip or directory form,
	/// identity, entry access. What the entries mean stays with the caller until
	/// the session migrates.
	/// </summary>
	public sealed class EnginePackage : IDisposable
	{
		private IntPtr _package;

		private EnginePackage(IntPtr package) => _package = package;

		/// <returns>null when the path is simply not a core package</returns>
		/// <exception cref="InvalidOperationException">something that looks like a package but cannot be read</exception>
		public static EnginePackage? Open(string path)
		{
			var error = IntPtr.Zero;
			var package = ChimeraEngine.Instance.ce_package_open(path, ref error);
			if (package != IntPtr.Zero) return new(package);
			if (error != IntPtr.Zero) throw new InvalidOperationException(ChimeraEngine.PtrToStringUtf8(error));
			return null;
		}

		public void Dispose()
		{
			if (_package == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_package_free(_package);
			_package = IntPtr.Zero;
		}

		/// <summary>SHA1 of the zip file (the package's identity); null for the directory form.</summary>
		public string? Sha1 => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_package_sha1(_package));

		/// <summary>True for the data-driven waterbox form (core.wbx + waterbox.config).</summary>
		public bool IsWaterbox => ChimeraEngine.Instance.ce_package_is_waterbox(_package) is not 0;

		public bool HasEntry(string name) => ChimeraEngine.Instance.ce_package_has_entry(_package, name) is not 0;

		/// <returns>the entry's bytes, or null when absent</returns>
		/// <exception cref="InvalidOperationException">the entry exists but cannot be read</exception>
		public byte[]? Entry(string name)
		{
			ulong len = 0;
			var p = ChimeraEngine.Instance.ce_package_entry(_package, name, ref len);
			if (p == IntPtr.Zero)
			{
				var error = ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_package_last_error(_package));
				if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
				return null;
			}
			var ret = new byte[len];
			Marshal.Copy(p, ret, 0, checked((int)len));
			return ret;
		}

		public string? EntryText(string name)
		{
			var bytes = Entry(name);
			return bytes is null ? null : Encoding.UTF8.GetString(bytes);
		}
	}

	/// <summary>
	/// A .chimeraProject, held by the engine (see ce_project in engine.h):
	/// chimera's entry point and its movie in one file. The engine owns the
	/// format - the rules, hashing, per-session file resolution, validation
	/// against a core's file_slots.json - and this wrapper only makes it
	/// idiomatic. Files carry a status: 0 resolved and matching, 1 unresolved,
	/// 2 resolved but mismatched (docs/project.md).
	/// </summary>
	public sealed class EngineProject : IDisposable
	{
		private IntPtr _project;

		private EngineProject(IntPtr project) => _project = project;

		/// <summary>The internal handle's liveness backstop, like EngineMovieLog's.</summary>
		~EngineProject() => Free();

		public static EngineProject New() => new(ChimeraEngine.Instance.ce_project_new());

		/// <exception cref="InvalidOperationException">a structurally invalid or unreadable project</exception>
		public static EngineProject Open(string path)
		{
			var error = IntPtr.Zero;
			var project = ChimeraEngine.Instance.ce_project_open(path, ref error);
			if (project == IntPtr.Zero)
			{
				throw new InvalidOperationException(
					ChimeraEngine.PtrToStringUtf8(error) ?? "the project could not be opened");
			}
			return new(project);
		}

		public void Dispose()
		{
			Free();
			GC.SuppressFinalize(this);
		}

		private void Free()
		{
			if (_project == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_project_free(_project);
			_project = IntPtr.Zero;
		}

		/// <exception cref="InvalidOperationException">the save was refused (cue closure, unwritable path)</exception>
		public void Save(string path)
		{
			var error = IntPtr.Zero;
			if (ChimeraEngine.Instance.ce_project_save(_project, path, ref error) is not 0)
			{
				throw new InvalidOperationException(
					ChimeraEngine.PtrToStringUtf8(error) ?? "the project could not be saved");
			}
		}

		/// <summary>
		/// This project and no other, minted when it was created and carried in the
		/// file. Everything regenerable that belongs to a project - the state
		/// history, and where this machine last found its files - is kept in a
		/// per-user cache keyed by this, so the .chimeraProject stays the one file
		/// that has to travel. Stable across the file being renamed, moved, or
		/// synced to another machine.
		/// </summary>
		public string Id
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_id(_project)) ?? "";

		public string Title
		{
			get => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_title(_project)) ?? "";
			set => ChimeraEngine.Instance.ce_project_set_title(_project, value);
		}

		public string Description
		{
			get => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_description(_project)) ?? "";
			set => ChimeraEngine.Instance.ce_project_set_description(_project, value);
		}

		public string CoreName => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_core_name(_project)) ?? "";
		public string CoreVersion => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_core_version(_project)) ?? "";
		public string CoreSha1 => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_core_sha1(_project)) ?? "";

		public void SetCore(string name, string version, string sha1)
			=> ChimeraEngine.Instance.ce_project_set_core(_project, name, version, sha1);

		public ulong Rerecords
		{
			get => ChimeraEngine.Instance.ce_project_rerecords(_project);
			set => ChimeraEngine.Instance.ce_project_set_rerecords(_project, value);
		}

		/// <summary>The sync settings as the JSON object the session's settings channel takes.</summary>
		public string SettingsJson
		{
			get
			{
				ulong len = 0;
				var p = ChimeraEngine.Instance.ce_project_settings_text(_project, ref len);
				return ChimeraEngine.PtrToStringUtf8(p, len);
			}
		}

		/// <exception cref="InvalidOperationException">not a JSON object</exception>
		public void SetSettingsJson(string json)
		{
			var error = IntPtr.Zero;
			if (ChimeraEngine.Instance.ce_project_set_settings_text(_project, json, ref error) is not 0)
			{
				throw new InvalidOperationException(ChimeraEngine.PtrToStringUtf8(error) ?? "bad settings");
			}
		}

		/// <summary>The firmware pins, a JSON array carried verbatim.</summary>
		public string FirmwareJson
		{
			get
			{
				ulong len = 0;
				var p = ChimeraEngine.Instance.ce_project_firmware_text(_project, ref len);
				return ChimeraEngine.PtrToStringUtf8(p, len);
			}
		}

		/// <summary>
		/// What the core compiled for this game: [{name, sha1}], one per object
		/// (docs/compile-cache.md). Not the files, which regenerate; the list, so
		/// that opening the project later can check it is the same compiled code.
		/// </summary>
		public string CoreCacheJson
		{
			get
			{
				ulong len = 0;
				var p = ChimeraEngine.Instance.ce_project_core_cache_text(_project, ref len);
				return ChimeraEngine.PtrToStringUtf8(p, len);
			}
		}

		/// <exception cref="InvalidOperationException">not a JSON array</exception>
		public void SetCoreCacheJson(string json)
		{
			var error = IntPtr.Zero;
			if (ChimeraEngine.Instance.ce_project_set_core_cache_text(_project, json, ref error) is not 0)
			{
				throw new InvalidOperationException(ChimeraEngine.PtrToStringUtf8(error) ?? "bad coreCache");
			}
		}

		/// <exception cref="InvalidOperationException">not a JSON array</exception>
		public void SetFirmwareJson(string json)
		{
			var error = IntPtr.Zero;
			if (ChimeraEngine.Instance.ce_project_set_firmware_text(_project, json, ref error) is not 0)
			{
				throw new InvalidOperationException(ChimeraEngine.PtrToStringUtf8(error) ?? "bad firmware");
			}
		}

		/// <summary>The input-log lump, exactly what EngineMovieLog parses and serializes.</summary>
		public string LogText
		{
			get
			{
				ulong len = 0;
				var p = ChimeraEngine.Instance.ce_project_log_text(_project, ref len);
				return ChimeraEngine.PtrToStringUtf8(p, len);
			}
			set
			{
				var bytes = Encoding.UTF8.GetBytes(value ?? "");
				ChimeraEngine.Instance.ce_project_set_log_text(_project, bytes, (ulong)bytes.LongLength);
			}
		}

		public int MarkerCount => ChimeraEngine.Instance.ce_project_marker_count(_project);
		public long MarkerFrame(int index) => ChimeraEngine.Instance.ce_project_marker_frame(_project, index);
		public string MarkerText(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_marker_text(_project, index)) ?? "";
		public bool MarkerKeepState(int index) => ChimeraEngine.Instance.ce_project_marker_keep_state(_project, index) is not 0;
		public int MarkerAdd(long frame, string text, bool keepState = true)
			=> ChimeraEngine.Instance.ce_project_marker_add(_project, frame, text, keepState ? 1 : 0);
		public void MarkerRemove(int index) => ChimeraEngine.Instance.ce_project_marker_remove(_project, index);
		public void MarkersClear()
		{
			while (MarkerCount > 0) MarkerRemove(MarkerCount - 1);
		}

		public int BranchCount => ChimeraEngine.Instance.ce_project_branch_count(_project);
		public string BranchName(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_branch_name(_project, index)) ?? "";
		public long BranchFrame(int index) => ChimeraEngine.Instance.ce_project_branch_frame(_project, index);
		public string BranchTime(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_branch_time(_project, index)) ?? "";
		public string BranchLogText(int index)
		{
			ulong len = 0;
			var p = ChimeraEngine.Instance.ce_project_branch_log_text(_project, index, ref len);
			return p == IntPtr.Zero ? "" : ChimeraEngine.PtrToStringUtf8(p, len);
		}
		public void BranchAdd(string name, long frame, string time, string logText)
		{
			var bytes = Encoding.UTF8.GetBytes(logText ?? "");
			ChimeraEngine.Instance.ce_project_branch_add(_project, name, frame, time ?? "", bytes, (ulong)bytes.LongLength);
		}
		public void BranchRemove(int index) => ChimeraEngine.Instance.ce_project_branch_remove(_project, index);
		public int BranchMarkerCount(int branch) => ChimeraEngine.Instance.ce_project_branch_marker_count(_project, branch);
		public long BranchMarkerFrame(int branch, int index) => ChimeraEngine.Instance.ce_project_branch_marker_frame(_project, branch, index);
		public string BranchMarkerText(int branch, int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_branch_marker_text(_project, branch, index)) ?? "";
		public bool BranchMarkerKeepState(int branch, int index)
			=> ChimeraEngine.Instance.ce_project_branch_marker_keep_state(_project, branch, index) is not 0;
		public void BranchMarkerAdd(int branch, long frame, string text, bool keepState = true)
			=> ChimeraEngine.Instance.ce_project_branch_marker_add(_project, branch, frame, text, keepState ? 1 : 0);

		public int HeaderCount => ChimeraEngine.Instance.ce_project_header_count(_project);
		public string HeaderKeyAt(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_header_key_at(_project, index)) ?? "";
		public string HeaderValueAt(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_header_value_at(_project, index)) ?? "";
		public string? HeaderGet(string key)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_header_get(_project, key));
		/// <summary>value null removes the key; a new key appends, keeping order</summary>
		public void HeaderSet(string key, string? value) => ChimeraEngine.Instance.ce_project_header_set(_project, key, value);
		public void HeadersClear()
		{
			while (HeaderCount > 0) HeaderSet(HeaderKeyAt(HeaderCount - 1), null);
		}

		public int SubtitleCount => ChimeraEngine.Instance.ce_project_subtitle_count(_project);
		public string SubtitleAt(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_subtitle_at(_project, index)) ?? "";
		public void SubtitleAdd(string line) => ChimeraEngine.Instance.ce_project_subtitle_add(_project, line);
		public void SubtitleRemove(int index) => ChimeraEngine.Instance.ce_project_subtitle_remove(_project, index);
		public void SubtitlesClear()
		{
			while (SubtitleCount > 0) SubtitleRemove(SubtitleCount - 1);
		}
		public void BranchesClear()
		{
			while (BranchCount > 0) BranchRemove(BranchCount - 1);
		}

		/// <summary>
		/// Adds a file: canonical bare name, the slot it fills, and where its bytes
		/// are right now (hashed immediately; a cue auto-adds its referenced files).
		/// </summary>
		/// <exception cref="InvalidOperationException">refused, with the engine's reason</exception>
		public void FileAdd(string name, string slot, string sourcePath)
		{
			var error = IntPtr.Zero;
			if (ChimeraEngine.Instance.ce_project_file_add(_project, name, slot, sourcePath, ref error) is not 0)
			{
				throw new InvalidOperationException(ChimeraEngine.PtrToStringUtf8(error) ?? "file refused");
			}
		}

		public void FileRemove(int index) => ChimeraEngine.Instance.ce_project_file_remove(_project, index);
		public int FileCount => ChimeraEngine.Instance.ce_project_file_count(_project);
		public string FileName(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_file_name(_project, index)) ?? "";
		public string FileSlot(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_file_slot(_project, index)) ?? "";
		public string FileSha1(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_file_sha1(_project, index)) ?? "";
		public string FileActualSha1(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_file_actual_sha1(_project, index)) ?? "";

		/// <summary>0 = resolved and matching, 1 = unresolved, 2 = resolved but mismatched.</summary>
		public int FileStatus(int index) => ChimeraEngine.Instance.ce_project_file_status(_project, index);

		/// <summary>
		/// Where this file's bytes were read from in THIS run, or "" if not yet read.
		/// In memory only: the project never carries a path, so remembering one is
		/// the frontend's business (see <c>ProjectLocalPaths</c>).
		/// </summary>
		public string FileSourcePath(int index)
			=> ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_project_file_source_path(_project, index)) ?? "";

		/// <summary>
		/// How many bytes the resolved file holds, or 0 while unresolved.
		///
		/// There is deliberately no way to ask for the BYTES. A project's files
		/// stay where they are and the machine reads them from there: a PS2 disc
		/// is over four gigabytes, which is more than a byte[] can hold at all,
		/// and every copy on the way to the sandbox was a copy of that.
		/// </summary>
		public ulong FileSize(int index)
			=> ChimeraEngine.Instance.ce_project_file_size(_project, index);

		/// <summary>Resolves one file from a caller-provided location; a hash mismatch is a status, not an error.</summary>
		/// <exception cref="InvalidOperationException">the path is unreadable</exception>
		public void FileResolve(int index, string path)
		{
			var error = IntPtr.Zero;
			if (ChimeraEngine.Instance.ce_project_file_resolve(_project, index, path, ref error) is not 0)
			{
				throw new InvalidOperationException(ChimeraEngine.PtrToStringUtf8(error) ?? "unresolvable");
			}
		}

		/// <summary>Puts a file back to unresolved: no bytes, no hash, no source path.</summary>
		public void FileUnresolve(int index) => ChimeraEngine.Instance.ce_project_file_unresolve(_project, index);

		/// <summary>Tries every unresolved file by canonical name in a directory; returns how many resolved.</summary>
		public int ResolveDir(string dir) => ChimeraEngine.Instance.ce_project_resolve_dir(_project, dir);

		/// <summary>True when every file is resolved with a matching hash.</summary>
		public bool FilesOk => ChimeraEngine.Instance.ce_project_files_ok(_project) is not 0;

		/// <summary>The manifest against a core's file_slots.json declaration; null when it conforms, else the reason.</summary>
		public string? Validate(string slotsJson)
		{
			var bytes = Encoding.UTF8.GetBytes(slotsJson);
			var error = IntPtr.Zero;
			if (ChimeraEngine.Instance.ce_project_validate(_project, bytes, (ulong)bytes.LongLength, ref error) is 0) return null;
			return ChimeraEngine.PtrToStringUtf8(error) ?? "the manifest does not fit the core's slots";
		}

		/// <summary>The slot map the session mounts as "slots" (support excluded).</summary>
		public string SlotsJson
		{
			get
			{
				ulong len = 0;
				var p = ChimeraEngine.Instance.ce_project_slots_text(_project, ref len);
				return ChimeraEngine.PtrToStringUtf8(p, len);
			}
		}
	}

	/// <summary>
	/// A running waterboxed machine, held by the engine (see ce_session in
	/// engine.h): the same machine chimera-run drives headlessly, wrapped for
	/// the frontend's adapter. Frame data crosses as borrowed pointers the
	/// caller copies out of before the next advance.
	/// </summary>
	/// <summary>
	/// A precompile session: worker Index of Count; FirmwareToo sweeps the
	/// firmware's libraries as well as the game's directories.
	/// </summary>
	public sealed record PrecompileRequest(int Index, int Count, bool FirmwareToo);

	public sealed class EngineSession : IDisposable
	{
		private IntPtr _session;

		public IntPtr Handle => _session;

		private EngineSession(IntPtr session) => _session = session;

		/// <exception cref="InvalidOperationException">anything wrong with the package, the rom, or the core's own refusal</exception>
		/// <remarks>extraFiles: a multi-file game's additional mounts (rom2..N,
		/// support files, "savedata", "rom.name"), each ORDERED pair mounted
		/// under its given name before the core boots.</remarks>
		/// <param name="wantGpu">
		/// The project asked for a renderer that draws on the machine's own GPU.
		/// It is only ever an ASK: a build without the bridge, a driver that will
		/// not give a context, or a core with no GL renderer all end in the
		/// software path, and <see cref="GpuDrew"/> is what says which happened.
		/// </param>
		/// <param name="rom">the game's bytes, or null when <paramref name="romPath"/> says where it is</param>
		/// <param name="romPath">
		/// Where the game lies. Given one, the engine mounts the file and the
		/// machine reads it from the disk as it goes - nothing is loaded. That is
		/// the only way a PS2 disc can be opened at all: over four gigabytes, and
		/// a byte[] tops out below two.
		/// </param>
		public static EngineSession Open(
			string packagePath, byte[]? rom, string? romPath, string? settingsOverridesJson,
			IReadOnlyDictionary<string, byte[]>? firmware,
			IReadOnlyList<CoreFile>? extraFiles = null,
			bool wantGpu = false,
			string cacheDir = null,
			PrecompileRequest precompile = null)
		{
			var count = firmware?.Count ?? 0;
			var ids = new IntPtr[Math.Max(count, 1)];
			var blobs = new IntPtr[Math.Max(count, 1)];
			var lens = new ulong[Math.Max(count, 1)];
			var extraCount = extraFiles?.Count ?? 0;
			var extraNames = new IntPtr[Math.Max(extraCount, 1)];
			var extraData = new IntPtr[Math.Max(extraCount, 1)];
			var extraLens = new ulong[Math.Max(extraCount, 1)];
			var extraPaths = new IntPtr[Math.Max(extraCount, 1)];
			var allocated = new List<IntPtr>();
			IntPtr AllocUtf8(string text)
			{
				var bytes = Encoding.UTF8.GetBytes(text + "\0");
				var ptr = Marshal.AllocHGlobal(bytes.Length);
				allocated.Add(ptr);
				Marshal.Copy(bytes, 0, ptr, bytes.Length);
				return ptr;
			}
			IntPtr AllocBlob(byte[] bytes)
			{
				var ptr = Marshal.AllocHGlobal(bytes.Length is 0 ? 1 : bytes.Length);
				allocated.Add(ptr);
				if (bytes.Length is not 0) Marshal.Copy(bytes, 0, ptr, bytes.Length);
				return ptr;
			}
			try
			{
				var i = 0;
				foreach (var (id, bytes) in firmware ?? new Dictionary<string, byte[]>())
				{
					ids[i] = AllocUtf8(id);
					blobs[i] = AllocBlob(bytes);
					lens[i] = (ulong)bytes.LongLength;
					i++;
				}
				for (var e = 0; e < extraCount; e++)
				{
					var file = extraFiles![e];
					extraNames[e] = AllocUtf8(file.Name);
					// a path wins: the bytes were never read, and there are none
					extraPaths[e] = file.Path is null ? IntPtr.Zero : AllocUtf8(file.Path);
					extraData[e] = AllocBlob(file.Data ?? [ ]);
					extraLens[e] = (ulong)(file.Data?.LongLength ?? 0);
				}
				// a global switch, not a per-session one, so it is set every time
				ChimeraEngine.Instance.ce_gl_request(wantGpu ? 1 : 0);
				// the compile cache directory for this core and package (empty:
				// none), and whether this session precompiles instead of running
				ChimeraEngine.Instance.ce_cache_dir(cacheDir ?? "");
				ChimeraEngine.Instance.ce_precompile_request(precompile?.Index ?? 0, precompile?.Count ?? 0, precompile?.FirmwareToo == true ? 1 : 0);
				var error = IntPtr.Zero;
				var session = ChimeraEngine.Instance.ce_session_open(
					packagePath, rom, (ulong)(rom?.LongLength ?? 0), romPath, settingsOverridesJson,
					ids, blobs, lens, count,
					extraNames, extraData, extraLens, extraPaths, extraCount, ref error);
				if (session == IntPtr.Zero)
				{
					throw new InvalidOperationException(ChimeraEngine.PtrToStringUtf8(error) ?? "the engine could not open a session");
				}
				return new(session);
			}
			finally
			{
				foreach (var p in allocated) Marshal.FreeHGlobal(p);
			}
		}

		public void Dispose()
		{
			if (_session == IntPtr.Zero) return;
			ChimeraEngine.Instance.ce_session_free(_session);
			_session = IntPtr.Zero;
		}

		public bool Disposed => _session == IntPtr.Zero;

		private static LibChimera E => ChimeraEngine.Instance;

		public string CoreName => ChimeraEngine.PtrToStringUtf8(E.ce_session_core_name(_session)) ?? "";
		public string SystemId => ChimeraEngine.PtrToStringUtf8(E.ce_session_system_id(_session)) ?? "";
		public int Width => E.ce_session_width(_session);
		public int Height => E.ce_session_height(_session);
		public int VirtualWidth => E.ce_session_virtual_width(_session);
		public int VirtualHeight => E.ce_session_virtual_height(_session);
		public int VsyncNumerator => E.ce_session_vsync_numerator(_session);
		public int VsyncDenominator => E.ce_session_vsync_denominator(_session);
		public int SamplesPerFrame => E.ce_session_samples_per_frame(_session);
		public bool Deterministic => E.ce_session_deterministic(_session) is not 0;

		/// <summary>
		/// Whether a GPU outside the sandbox drew this session's pictures. A
		/// movie records it, because a run made this way carries no promise that
		/// it replays on another machine and a desync elsewhere should be
		/// explicable rather than mysterious.
		/// </summary>
		public bool GpuDrew => E.ce_session_gpu_drew(_session) is not 0;

		/// <summary>What the driver calls itself, or empty when none drew.</summary>
		public string GpuDescription
			=> GpuDrew ? (ChimeraEngine.PtrToStringUtf8(E.ce_gl_description()) ?? "") : "";

		public void SetAxis(int index, int value) => E.ce_session_set_axis(_session, index, value);

		/// <summary>
		/// Whether a declared control is one THIS machine has. waterbox.config
		/// declares the union of every peripheral a package's ports can hold;
		/// the project decides what is plugged in, and only the running core
		/// knows. An inactive control is not in the controller and not a column.
		/// Indices are the declaration's either way, so the wire is unaffected.
		/// </summary>
		/// <summary>
		/// One drive light per medium the machine has - a disc, a hard disk -
		/// lit on any frame that drive was read or written. Zero when the core
		/// has no removable media, which is most of them.
		/// </summary>
		public int DriveCount => E.ce_session_drive_count(_session);

		public string DriveName(int index)
			=> ChimeraEngine.PtrToStringUtf8(E.ce_session_drive_name(_session, index)) ?? "Drive";

		public bool DriveLight(int index) => E.ce_session_drive_light(_session, index) is not 0;

		public bool ButtonActive(int index) => E.ce_session_button_active(_session, index) is not 0;

		public bool AxisActive(int index) => E.ce_session_axis_active(_session, index) is not 0;

		/// <returns>true when the frame was a lag frame</returns>
		/// <summary>Objects this session's core stored into the compile cache.</summary>
		public ulong CacheStored => ChimeraEngine.Instance.ce_session_cache_stored(_session);

		/// <summary>Objects this session's core fetched from the compile cache.</summary>
		public ulong CacheFetched => ChimeraEngine.Instance.ce_session_cache_fetched(_session);

		/// <summary>For a precompile session: whether the core has finished compiling its share.</summary>
		public bool PrecompileDone => ChimeraEngine.Instance.ce_session_precompile_done(_session) == 1;

		/// <summary>For a precompile session: module parts done and in total, for a progress bar.</summary>
		public (uint Done, uint Total) PrecompileProgress
		{
			get
			{
				if (ChimeraEngine.Instance.ce_session_precompile_progress(_session, out var done, out var total) != 0) return (0, 0);
				return (done, total);
			}
		}

		public bool FrameAdvance(ulong buttons, bool render)
			=> E.ce_session_frame_advance(_session, buttons, render ? 1 : 0) is not 0;

		/// <summary>Borrowed: the last rendered frame, BGRA, Width*Height ints.</summary>
		public IntPtr VideoBuffer => E.ce_session_video(_session);
		/// <summary>The live frame size - a mode-changing machine (DOS) reports it per frame; others equal the config's.</summary>
		public int VideoWidth => E.ce_session_video_width(_session);
		public int VideoHeight => E.ce_session_video_height(_session);

		/// <summary>Borrowed: the last frame's interleaved stereo s16 and its pair count.</summary>
		public IntPtr AudioBuffer(out int sampleCount)
		{
			var n = 0;
			var p = E.ce_session_audio(_session, ref n);
			sampleCount = n;
			return p;
		}

		/// <summary>Borrowed until the next save: the whole-machine state.</summary>
		public IntPtr SaveState(out int length)
		{
			ulong len = 0;
			var p = E.ce_session_save_state(_session, ref len);
			if (p == IntPtr.Zero) throw new InvalidOperationException(LastError);
			length = checked((int)len);
			return p;
		}

		public void LoadState(byte[] data, int length)
		{
			if (E.ce_session_load_state(_session, data, (ulong)length) is not 0)
			{
				throw new InvalidOperationException(LastError);
			}
		}

		public int DomainCount => E.ce_session_domain_count(_session);
		public string DomainName(int index) => ChimeraEngine.PtrToStringUtf8(E.ce_session_domain_name(_session, index)) ?? $"Domain {index}";
		public long DomainSize(int index) => E.ce_session_domain_size(_session, index);
		public bool DomainWritable(int index) => E.ce_session_domain_writable(_session, index) is not 0;
		public IntPtr DomainPtr(int index) => unchecked((IntPtr)(long)E.ce_session_domain_ptr(_session, index));

		/// <returns>true when the running guest took the settings; false when it has no live-settings group (reboot instead)</returns>
		/// <exception cref="InvalidOperationException">the guest has the group but the push failed</exception>
		public bool ApplySettings(string overridesJson)
			=> E.ce_session_apply_settings(_session, overridesJson) switch
			{
				0 => true,
				1 => false,
				_ => throw new InvalidOperationException(LastError),
			};

		// ---- the optional guest ABI groups (zero count / false = absent) ----

		public int SurfaceCount => E.ce_session_surface_count(_session);
		public string SurfaceName(int index) => ChimeraEngine.PtrToStringUtf8(E.ce_session_surface_name(_session, index)) ?? $"Surface {index}";
		public int SurfaceWidth(int index) => E.ce_session_surface_width(_session, index);
		public int SurfaceHeight(int index) => E.ce_session_surface_height(_session, index);
		/// <summary>Borrowed until the same surface renders again; IntPtr.Zero when the guest gave nothing.</summary>
		public IntPtr RenderSurface(int index) => E.ce_session_surface_render(_session, index);

		public int RegisterCount => E.ce_session_register_count(_session);
		public string RegisterName(int index) => ChimeraEngine.PtrToStringUtf8(E.ce_session_register_name(_session, index)) ?? $"R{index}";
		public int RegisterBits(int index) => E.ce_session_register_bits(_session, index);
		public long RegisterValue(int index) => E.ce_session_register_value(_session, index);
		/// <returns>false when this core does not support writing registers</returns>
		public bool SetRegister(int index, long value) => E.ce_session_register_set(_session, index, value) is 0;
		public bool HasExecutedCycles => E.ce_session_has_executed_cycles(_session) is not 0;
		public long ExecutedCycles => E.ce_session_executed_cycles(_session);

		public int BusCount => E.ce_session_bus_count(_session);
		public string BusName(int index) => ChimeraEngine.PtrToStringUtf8(E.ce_session_bus_name(_session, index)) ?? $"Bus {index}";
		public long BusSize(int index) => E.ce_session_bus_size(_session, index);
		public bool BusWritable(int index) => E.ce_session_bus_writable(_session, index) is not 0;
		public byte BusPeek(int index, int addr) => unchecked((byte)E.ce_session_bus_peek(_session, index, addr));
		public void BusPoke(int index, int addr, byte value) => E.ce_session_bus_poke(_session, index, addr, value);

		public bool TraceAvailable => E.ce_session_trace_available(_session) is not 0;
		public string TraceHeader => ChimeraEngine.PtrToStringUtf8(E.ce_session_trace_header(_session)) ?? "Instructions";
		/// <summary>The session remembers the flag and re-asserts it after a state load.</summary>
		public void TraceEnable(bool on) => E.ce_session_trace_enable(_session, on ? 1 : 0);
		/// <summary>The traced lines since the last drain (consecutive NUL-terminated strings), cleared on the way out.</summary>
		public byte[] TraceDrain(out int lineCount, out bool overflowed)
		{
			ulong len = 0;
			var lines = 0;
			var overflow = 0;
			var p = E.ce_session_trace_drain(_session, ref len, ref lines, ref overflow);
			lineCount = lines;
			overflowed = overflow is not 0;
			if (len == 0) return [ ];
			var ret = new byte[len];
			Marshal.Copy(p, ret, 0, checked((int)len));
			return ret;
		}

		/// <summary>Buttons past the packed mask's 64 (wide controllers): set per frame, before the advance; values persist until changed.</summary>
		public void SetButton(int index, bool pressed) => E.ce_session_set_button(_session, index, pressed ? 1 : 0);

		public bool SaveDataAvailable => E.ce_session_savedata_available(_session) is not 0;
		/// <summary>Snapshots the guest's exportable files (the list is dynamic); names and sizes refer to this snapshot.</summary>
		public int SaveDataSnapshot() => E.ce_session_savedata_count(_session);
		public string SaveDataName(int index) => ChimeraEngine.PtrToStringUtf8(E.ce_session_savedata_name(_session, index)) ?? $"file{index}";
		public long SaveDataSize(int index) => E.ce_session_savedata_size(_session, index);
		/// <summary>Copies [offset, offset+buffer.Length) of file index into buffer; returns bytes copied (clamped at the file's end).</summary>
		public int SaveDataRead(int index, long offset, byte[] buffer)
			=> checked((int)E.ce_session_savedata_read(_session, index, offset, buffer, buffer.LongLength));

		private string LastError
			=> ChimeraEngine.PtrToStringUtf8(E.ce_session_last_error(_session)) ?? "engine session error";

		/// <summary>What built the waterbox host, as JSON; "" when the host is not loadable.</summary>
		public static string HostBuildInfo => ChimeraEngine.PtrToStringUtf8(ChimeraEngine.Instance.ce_host_build_info()) ?? "";
	}

	/// <summary>The firmware verdict and the canonical movie line; declarations and files stay with the caller.</summary>
	public static class EngineFirmware
	{
		public enum Verdict { WrongSize = 0, Unrecognised = 1, Good = 2 }

		public static Verdict Classify(long declaredSize, System.Collections.Generic.IEnumerable<string>? expectedSha1s, long actualSize, string actualSha1)
			=> (Verdict)ChimeraEngine.Instance.ce_firmware_state(
				declaredSize,
				expectedSha1s is null ? "" : string.Join("\n", expectedSha1s),
				actualSize,
				actualSha1);

		/// <summary>"&lt;id&gt;=&lt;sha1&gt;" pairs in canonical (id-ordinal) order, space-joined.</summary>
		public static string RecordLine(System.Collections.Generic.IEnumerable<(string Id, string Sha1)> pairs)
		{
			ulong len = 0;
			var packed = string.Join("\n", System.Linq.Enumerable.Select(pairs, static p => $"{p.Id}={p.Sha1}"));
			var result = ChimeraEngine.Instance.ce_firmware_record_line(packed, ref len);
			return ChimeraEngine.PtrToStringUtf8(result, len);
		}

		/// <summary>
		/// The firmware decision tree (docs/project.md): which declared entries
		/// the project's decisions call for - every one REQUIRED, each nailed to
		/// one exact file (the index says which declaration entry, so same-id
		/// variants resolve). Takes the package's raw "firmware" array, the slot
		/// map, and the EFFECTIVE (defaults-overlaid) settings.
		/// </summary>
		public static System.Collections.Generic.IReadOnlyList<(string Id, int Index)> Evaluate(
			string declJson, string slotsJson, string settingsJson)
		{
			var decl = Encoding.UTF8.GetBytes(declJson ?? "[]");
			var slots = Encoding.UTF8.GetBytes(slotsJson ?? "{}");
			var settings = Encoding.UTF8.GetBytes(settingsJson ?? "{}");
			ulong len = 0;
			var result = ChimeraEngine.Instance.ce_firmware_evaluate(
				decl, (ulong)decl.LongLength,
				slots, (ulong)slots.LongLength,
				settings, (ulong)settings.LongLength,
				ref len);
			var text = ChimeraEngine.PtrToStringUtf8(result, len);
			System.Collections.Generic.List<(string, int)> outList = new();
			foreach (var item in Newtonsoft.Json.Linq.JArray.Parse(text))
			{
				var id = item.Value<string>("id");
				if (id is null) continue;
				outList.Add((id, item.Value<int?>("index") ?? -1));
			}
			return outList;
		}
	}

	/// <summary>
	/// The decision tree over SYNC SETTINGS (docs/project.md): the game files
	/// chosen decide which settings are exposed at all, and settings may gate
	/// further settings. Same rules as the firmware tree: entries without a
	/// condition always apply; same-name variants are separate entries with
	/// disjoint conditions, the index picking which declaration governs.
	/// </summary>
	public static class EngineSettingsGate
	{
		public static System.Collections.Generic.IReadOnlyList<(string Name, int Index)> Evaluate(
			string declJson, string slotsJson, string settingsJson)
		{
			var decl = Encoding.UTF8.GetBytes(declJson ?? "[]");
			var slots = Encoding.UTF8.GetBytes(slotsJson ?? "{}");
			var settings = Encoding.UTF8.GetBytes(settingsJson ?? "{}");
			ulong len = 0;
			var result = ChimeraEngine.Instance.ce_settings_evaluate(
				decl, (ulong)decl.LongLength,
				slots, (ulong)slots.LongLength,
				settings, (ulong)settings.LongLength,
				ref len);
			var text = ChimeraEngine.PtrToStringUtf8(result, len);
			System.Collections.Generic.List<(string, int)> outList = new();
			foreach (var item in Newtonsoft.Json.Linq.JArray.Parse(text))
			{
				var name = item.Value<string>("name");
				if (name is null) continue;
				outList.Add((name, item.Value<int?>("index") ?? -1));
			}
			return outList;
		}
	}

	/// <summary>
	/// The decision tree over the SLOTS themselves (docs/project.md): filling
	/// one slot can make another unavailable until it is unloaded - a Famicom
	/// disk rules out a cartridge and vice versa.
	/// </summary>
	public static class EngineSlotsGate
	{
		/// <param name="settingsJson">
		/// the effective settings, because a slot can belong to one machine of a
		/// package that is several (a Sega CD drive is not a Master System's)
		/// </param>
		/// <returns>the exposed slot ids, declaration order</returns>
		public static System.Collections.Generic.IReadOnlyList<string> Evaluate(string declJson, string slotsJson, string settingsJson = "{}")
		{
			var decl = Encoding.UTF8.GetBytes(declJson ?? "{}");
			var slots = Encoding.UTF8.GetBytes(slotsJson ?? "{}");
			var settings = Encoding.UTF8.GetBytes(settingsJson ?? "{}");
			ulong len = 0;
			var result = ChimeraEngine.Instance.ce_slots_evaluate(
				decl, (ulong)decl.LongLength, slots, (ulong)slots.LongLength,
				settings, (ulong)settings.LongLength, ref len);
			var text = ChimeraEngine.PtrToStringUtf8(result, len);
			System.Collections.Generic.List<string> outList = new();
			foreach (var item in Newtonsoft.Json.Linq.JArray.Parse(text))
			{
				if (item.Type is Newtonsoft.Json.Linq.JTokenType.String) outList.Add(item.ToObject<string>()!);
			}
			return outList;
		}
	}

	/// <summary>
	/// The files a cue sheet references, in cue order - what the project will
	/// pull in beside the cue as support files. Lets a form show
	/// "disc.cue (+ N tracks)" and complain at pick time.
	/// </summary>
	public static class EngineCue
	{
		public static System.Collections.Generic.IReadOnlyList<string> References(byte[] cueBytes)
		{
			ulong len = 0;
			var result = ChimeraEngine.Instance.ce_cue_references(
				cueBytes ?? [ ], (ulong)(cueBytes?.LongLength ?? 0), ref len);
			var text = ChimeraEngine.PtrToStringUtf8(result, len);
			System.Collections.Generic.List<string> outList = new();
			foreach (var item in Newtonsoft.Json.Linq.JArray.Parse(text))
			{
				if (item.Type is Newtonsoft.Json.Linq.JTokenType.String) outList.Add(item.ToObject<string>()!);
			}
			return outList;
		}
	}

	/// <summary>One Subtitles.txt line: "subtitle FRAME X Y DURATION RRGGBBAA message".</summary>
	public static class EngineSubtitleLine
	{
		public static bool TryParse(string line, out LibChimera.CeSubtitleFields fields, out string message)
		{
			fields = default;
			var buf = new byte[Encoding.UTF8.GetByteCount(line) + 1];
			var n = ChimeraEngine.Instance.ce_subtitle_parse_line(line, ref fields, buf, (ulong)buf.LongLength);
			if (n < 0)
			{
				message = "";
				return false;
			}
			message = Encoding.UTF8.GetString(buf, 0, (int)n);
			return true;
		}

		public static string Format(in LibChimera.CeSubtitleFields fields, string message)
		{
			var f = fields;
			var buf = new byte[96 + Encoding.UTF8.GetByteCount(message)];
			var n = ChimeraEngine.Instance.ce_subtitle_format_line(ref f, message, buf, (ulong)buf.LongLength);
			return Encoding.UTF8.GetString(buf, 0, (int)n);
		}
	}

	/// <summary>
	/// How far along a slow thing is, from wherever it is being done - the engine
	/// hashing a disc image, compressing a greenzone, fetching a boot's compiled
	/// code, or a frontend step around those. One event, one shape: a stage in
	/// words, and done over total, where a total of zero means the end is not
	/// known. A progress window subscribes while it is up; nobody subscribed
	/// costs a null check. Raised on the thread doing the work, which for the
	/// engine is the caller's own, so a handler may pump the message loop and
	/// must not call back into the engine.
	/// </summary>
	public static class EngineProgress
	{
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate void NativeFn(IntPtr stage, ulong done, ulong total, IntPtr user);

		// kept alive for as long as the engine may call it, which is forever
		private static readonly NativeFn _native = OnNative;
		private static IntPtr _nativePtr;

		public static event Action<string, ulong, ulong>? Reported;

		internal static void Install(LibChimera lib)
		{
			if (_nativePtr == IntPtr.Zero) _nativePtr = Marshal.GetFunctionPointerForDelegate(_native);
			lib.ce_progress_set(_nativePtr, IntPtr.Zero);
		}

		private static void OnNative(IntPtr stage, ulong done, ulong total, IntPtr user)
			=> Reported?.Invoke(ChimeraEngine.PtrToStringUtf8(stage) ?? "", done, total);

		/// <summary>A step the frontend itself is taking, in the same stream as the engine's.</summary>
		public static void Report(string stage, ulong done = 0, ulong total = 0)
			=> Reported?.Invoke(stage, done, total);
	}
}
