#nullable enable

using System;
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
