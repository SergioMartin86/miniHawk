#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;

using Chimera.Emulation.Common.Engine;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Chimera.Client.Common
{
	/// <summary>
	/// Where this machine keeps a project's files, remembered beside it.
	///
	/// A .chimeraProject is meant to be handed to someone else: it names files
	/// by name and hash and never by path, because a path is true of one machine
	/// and nowhere else (docs/project.md). But the machine that MADE the project
	/// knows where those files are, and asking it again every time you open your
	/// own work is friction for nothing.
	///
	/// So the paths are remembered - never distributed, and never read as
	/// authority. They are hints. Every path one offers is checked: the file must
	/// still be there and still hash to what the project records, and anything
	/// that fails simply falls through to the usual resolution, which is what
	/// happens on a machine that has never seen the project. Losing them costs
	/// nothing but the asking.
	///
	/// They live in the per-user cache (<see cref="ProjectCache"/>), keyed by the
	/// project's id, rather than in a sibling file. A .chimeraProject is the one
	/// file that exists as far as anyone else is concerned - it is what gets
	/// handed over and what a cloud folder syncs - so nothing that can be
	/// recomputed belongs beside it. A sidecar found next to an older project is
	/// read once and moved here, so nothing anybody had is lost.
	/// </summary>
	public sealed class ProjectLocalPaths
	{
		public const string Extension = "chimeraLocal";

		/// <summary>canonical file name (as the project records it) -> where it was last read from</summary>
		private readonly Dictionary<string, string> _files = new();

		/// <summary>firmware id -> where the file that satisfied it was last read from</summary>
		private readonly Dictionary<string, string> _firmware = new();

		public IReadOnlyDictionary<string, string> Files => _files;

		public IReadOnlyDictionary<string, string> Firmware => _firmware;

		/// <summary>Where a project's remembered paths are kept, in the per-user cache.</summary>
		public static string PathFor(EngineProject project)
			=> Path.Combine(ProjectCache.DirectoryFor(project.Id), "local-paths.json");

		/// <summary>
		/// Where a project written before the cache existed left its sidecar: beside
		/// the project file itself. Only ever read, and only to move it.
		/// </summary>
		public static string LegacyPathFor(string projectPath)
			=> Path.ChangeExtension(projectPath, Extension);

		/// <summary>
		/// The paths remembered for a project; an absent or unreadable record is
		/// simply empty. A sidecar left beside the project by an older Chimera is
		/// taken over: read, and then removed once its contents are safely here, so
		/// the project folder ends up holding only the project.
		/// </summary>
		public static ProjectLocalPaths Read(EngineProject project, string projectPath = null)
		{
			ProjectLocalPaths local = new();
			var path = PathFor(project);
			if (!File.Exists(path) && projectPath is not null)
			{
				var legacy = LegacyPathFor(projectPath);
				if (File.Exists(legacy))
				{
					local.ReadFrom(legacy);
					local.Write(project);
					try
					{
						if (File.Exists(PathFor(project))) File.Delete(legacy);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						// it stays where it is; it will be read and re-offered next time
					}
					return local;
				}
			}
			local.ReadFrom(path);
			return local;
		}

		private void ReadFrom(string path)
		{
			ProjectLocalPaths local = this;
			if (!File.Exists(path)) return;
			try
			{
				var root = JObject.Parse(File.ReadAllText(path));
				Fill(local._files, root["files"] as JObject);
				Fill(local._firmware, root["firmware"] as JObject);
			}
			catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
			{
				// a hint that cannot be read is a hint nobody has: resolution proceeds
				local._files.Clear();
				local._firmware.Clear();
			}
		}

		private static void Fill(Dictionary<string, string> into, JObject? from)
		{
			if (from is null) return;
			foreach (var (key, value) in from)
			{
				var path = value?.Value<string>();
				if (!string.IsNullOrWhiteSpace(path)) into[key] = path!;
			}
		}

		/// <summary>
		/// Where THIS session found the open project's firmware, by id.
		///
		/// A project that has never been written has no sidecar to write into:
		/// the wizard's choices and the boot's lookups live only in the config,
		/// and a config that is replaced (a fresh install copied over an old one)
		/// or a file that is moved takes them with it - which is a saved project
		/// that will not open, with its firmware still on the machine that made
		/// it (issue #40). The first save is the first chance to write them
		/// beside the project, so the boot leaves them here and every save merges
		/// them in. They are hints and nothing more: the next load checks each
		/// one by hash before it mounts anything, so an entry left over from a
		/// project that is no longer open costs the asking and no more.
		/// </summary>
		private static readonly Dictionary<string, string> SessionFirmware = new();

		/// <summary>Forgotten when another project boots, so a sidecar records this project's answers.</summary>
		public static void ForgetSessionFirmware()
		{
			lock (SessionFirmware) SessionFirmware.Clear();
		}

		public void RememberFirmware(string id, string path)
		{
			if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path)) return;
			var full = Path.GetFullPath(path);
			_firmware[id] = full;
			lock (SessionFirmware) SessionFirmware[id] = full;
		}

		/// <summary>
		/// Offers the remembered locations to a project's unresolved files. A file
		/// that has moved, or whose bytes no longer match what the project records,
		/// is left unresolved for the resolution dialog to ask about - the hint is
		/// never allowed to mount the wrong bytes quietly.
		/// </summary>
		/// <returns>how many files the sidecar resolved</returns>
		public int ApplyTo(EngineProject project)
		{
			var resolved = 0;
			for (var i = 0; i < project.FileCount; i++)
			{
				if (project.FileStatus(i) is not 1) continue; // already found beside the project
				if (!_files.TryGetValue(project.FileName(i), out var path)) continue;
				if (!File.Exists(path)) continue;
				try
				{
					project.FileResolve(i, path);
				}
				catch (InvalidOperationException)
				{
					continue; // unreadable now; the dialog will ask
				}
				if (project.FileStatus(i) is 0)
				{
					resolved++;
				}
				else
				{
					// the path still exists but holds something else now: that is a
					// question for the user, not an answer from a hint
					project.FileUnresolve(i);
				}
			}
			return resolved;
		}

		/// <summary>
		/// Writes down where the session actually read each file from, into this
		/// project's cache directory. Nothing here is needed to open the project
		/// anywhere else, which is the whole point of keeping it out of the project
		/// file - and out of the folder the project lives in.
		/// </summary>
		public void Save(EngineProject project)
		{
			for (var i = 0; i < project.FileCount; i++)
			{
				var source = project.FileSourcePath(i);
				if (source.Length is 0) continue;
				_files[project.FileName(i)] = Path.GetFullPath(source);
			}
			// what the session found, for a project whose sidecar this is the
			// first of; anything this instance was told itself wins
			lock (SessionFirmware)
			{
				foreach (var (id, source) in SessionFirmware)
				{
					if (!_firmware.ContainsKey(id)) _firmware[id] = source;
				}
			}
			Write(project);
		}

		/// <summary>Puts what is remembered into the project's cache directory.</summary>
		private void Write(EngineProject project)
		{
			if (_files.Count is 0 && _firmware.Count is 0) return;

			JObject root = new()
			{
				["files"] = new JObject(_files.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal)
					.Select(static kvp => new JProperty(kvp.Key, kvp.Value))),
				["firmware"] = new JObject(_firmware.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal)
					.Select(static kvp => new JProperty(kvp.Key, kvp.Value))),
			};
			try
			{
				ProjectCache.Ensure(project.Id);
				File.WriteAllText(PathFor(project), root.ToString(Formatting.Indented));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// a convenience that cannot be written is not worth an error: the
				// project itself saved fine, and next time it will ask
			}
		}
	}
}
