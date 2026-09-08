#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

using Chimera.Common;
using Chimera.Common.PathExtensions;

namespace Chimera.Client.Common
{
	/// <summary>
	/// Where a project's regenerable things live: the state history, and where
	/// this machine last found the project's files.
	///
	/// The rule is that a <c>.chimeraProject</c> is the ONE file that exists as
	/// far as anyone else is concerned. It is what gets handed over, and what
	/// gets synced to a cloud folder; everything else about a project can be
	/// recomputed from it, so nothing else belongs beside it. That is not
	/// tidiness: projects live in synced folders - a multi-gigabyte state
	/// history next to one would be uploaded on every save, and the sync client
	/// holds files open while it works.
	///
	/// So the rest lives here instead, per user and per project, keyed by the
	/// project's own id rather than by its path. A path is true of one machine
	/// and stops being true the moment the file is renamed or moved; an id
	/// survives all of that, and survives the same project being opened on
	/// another machine through a synced folder. Two attempts at the same game
	/// have different ids and so cannot share a cache, which anything derived
	/// from the contents would have got wrong.
	///
	/// Losing all of this costs recomputation and never work, which is the rule
	/// the state history has always been held to. Deleting the directory is a
	/// supported thing to do.
	/// </summary>
	public static class ProjectCache
	{
		/// <summary>
		/// The root everything per-project hangs under. Follows the core store
		/// (see <see cref="CoreStore"/>): <c>CHIMERA_DATA_HOME</c> wins where it is
		/// set, then the platform's per-user data location.
		///
		/// Worked out on every ask rather than cached, so that changing
		/// <c>CHIMERA_DATA_HOME</c> takes effect - which is what a portable install
		/// wants, and what lets a test point the cache at a temporary directory
		/// instead of at the machine's real one. It is a few string joins.
		/// </summary>
		public static string Root => Resolve();

		private const string DirName = "Projects";

		private static string Resolve()
		{
			var dataHome = Environment.GetEnvironmentVariable("CHIMERA_DATA_HOME");
			if (!string.IsNullOrWhiteSpace(dataHome)) return Path.Combine(dataHome!, DirName);
			if (OSTailoredCode.IsUnixHost)
			{
				var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
				var home = Environment.GetEnvironmentVariable("HOME") ?? ".";
				var baseDir = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg!;
				return Path.Combine(baseDir, "chimera", DirName);
			}
			var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
			if (string.IsNullOrWhiteSpace(local)) return Path.Combine(PathUtils.DataDirectoryPath, DirName);
			return Path.Combine(local, "Chimera", DirName);
		}

		/// <summary>
		/// This project's directory. Not created by asking; see <see cref="Ensure"/>.
		/// An id that is empty or holds anything but plain hex is refused a
		/// directory of its own and given "unidentified" - a project file somebody
		/// hand-edited must not be able to name a path.
		/// </summary>
		public static string DirectoryFor(string projectId) => Path.Combine(Root, Safe(projectId));

		/// <summary>Creates this project's directory if it is not there, and returns it.</summary>
		public static string Ensure(string projectId)
		{
			var dir = DirectoryFor(projectId);
			Directory.CreateDirectory(dir);
			return dir;
		}

		/// <summary>
		/// A name for this project's cache, so the cache manager can show something
		/// a person recognises instead of sixteen hex digits. Written whenever the
		/// project is saved; it is a label and nothing depends on it.
		/// </summary>
		public static void RememberLabel(string projectId, string label)
		{
			if (string.IsNullOrWhiteSpace(label)) return;
			try
			{
				Ensure(projectId);
				File.WriteAllText(Path.Combine(DirectoryFor(projectId), LabelFile), label);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// a label that cannot be written costs a less friendly list, nothing more
			}
		}

		/// <summary>What this project calls itself, or "" if the cache never learned.</summary>
		public static string LabelOf(string projectId)
		{
			try
			{
				var path = Path.Combine(DirectoryFor(projectId), LabelFile);
				return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return "";
			}
		}

		private const string LabelFile = "about.txt";

		/// <summary>
		/// Every project this machine has cached anything for: the id, what it
		/// calls itself, how much room it takes and when it was last touched.
		/// Nothing here is authority - a directory whose project has been deleted
		/// is listed exactly like one whose project is sitting open.
		/// </summary>
		public static IReadOnlyList<CachedProject> All()
		{
			List<CachedProject> found = new();
			try
			{
				if (!Directory.Exists(Root)) return found;
				foreach (var dir in Directory.EnumerateDirectories(Root))
				{
					var id = Path.GetFileName(dir);
					long bytes = 0;
					var touched = DateTime.MinValue;
					try
					{
						foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
						{
							bytes += f.Length;
							if (f.LastWriteTimeUtc > touched) touched = f.LastWriteTimeUtc;
						}
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						// a directory that will not be measured is still worth listing
					}
					found.Add(new CachedProject { Id = id, Label = LabelOf(id), Path = dir, Bytes = bytes, LastUsed = touched });
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return found;
			}
			return found;
		}

		/// <summary>One project's cache, as the cache manager lists it.</summary>
		public sealed class CachedProject
		{
			public string Id { get; init; } = "";

			/// <summary>What the project calls itself, or "" when the cache never learned.</summary>
			public string Label { get; init; } = "";

			public string Path { get; init; } = "";

			public long Bytes { get; init; }

			public DateTime LastUsed { get; init; }
		}

		/// <summary>Forgets everything cached for one project. Costs recomputation, never work.</summary>
		public static void Forget(string projectId)
		{
			try
			{
				var dir = DirectoryFor(projectId);
				if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// a cache that will not go is not worth an error: it is only a cache
			}
		}

		/// <summary>
		/// An id reduced to what may safely name a directory. The engine mints
		/// sixteen hex characters; anything else came from an edited file.
		/// </summary>
		private static string Safe(string projectId)
		{
			if (string.IsNullOrWhiteSpace(projectId)) return "unidentified";
			foreach (var c in projectId)
			{
				if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))) return "unidentified";
			}
			return projectId.Length <= 64 ? projectId : projectId.Substring(0, 64);
		}
	}
}
