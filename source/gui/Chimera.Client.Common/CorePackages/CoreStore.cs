#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Chimera.Common;
using Chimera.Common.PathExtensions;

namespace Chimera.Client.Common
{
	/// <summary>
	/// Where the core manager puts what it downloads.
	///
	/// It is deliberately NOT the bundle's own <c>Cores/</c>. A Chimera bundle is a
	/// zip somebody unpacks, and updating it means unpacking a newer one: cores
	/// living inside it would have to be downloaded again every time the frontend
	/// moved, which for a fifteen-core install is unreasonable. So downloads go to
	/// the user's data directory and outlive any number of Chimeras, while
	/// <c>Cores/</c> beside the executable keeps working exactly as it does today
	/// for a package somebody put there by hand.
	///
	/// Both are scanned (see <see cref="CorePackageDiscovery.SearchPaths"/>), and
	/// the bundle's is scanned FIRST, so a portable install that carries its own
	/// cores wins over whatever else the machine has lying around.
	/// </summary>
	public static class CoreStore
	{
		/// <summary>
		/// The store directory. Not created by reading this; see <see cref="Ensure"/>.
		///
		/// <c>CHIMERA_DATA_HOME</c> wins where it is set, which is how a portable
		/// install keeps everything under one root. Otherwise this is the platform's
		/// per-user data location: <c>%LOCALAPPDATA%\Chimera\Cores</c> on Windows,
		/// <c>$XDG_DATA_HOME/chimera/Cores</c> (default <c>~/.local/share</c>) elsewhere.
		/// </summary>
		public static string Path => _path ??= Resolve();

		private static string? _path;

		private static string Resolve()
		{
			var dataHome = Environment.GetEnvironmentVariable("CHIMERA_DATA_HOME");
			if (!string.IsNullOrWhiteSpace(dataHome))
			{
				return System.IO.Path.Combine(dataHome!, CorePackageDiscovery.DefaultDirName);
			}
			if (OSTailoredCode.IsUnixHost)
			{
				var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
				var home = Environment.GetEnvironmentVariable("HOME") ?? ".";
				var baseDir = string.IsNullOrWhiteSpace(xdg)
					? System.IO.Path.Combine(home, ".local", "share")
					: xdg!;
				return System.IO.Path.Combine(baseDir, "chimera", CorePackageDiscovery.DefaultDirName);
			}
			var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
			// an empty LocalApplicationData means a profile this process cannot see;
			// falling back beside the executable is worse than nothing only if it is
			// unwritable, which the caller finds out the ordinary way
			if (string.IsNullOrWhiteSpace(local)) return System.IO.Path.Combine(PathUtils.DataDirectoryPath, CorePackageDiscovery.DefaultDirName);
			return System.IO.Path.Combine(local, "Chimera", CorePackageDiscovery.DefaultDirName);
		}

		/// <summary>Creates the store if it is not there. Returns the path either way.</summary>
		/// <exception cref="IOException">the directory cannot be created</exception>
		public static string Ensure()
		{
			Directory.CreateDirectory(Path);
			return Path;
		}

		/// <summary>
		/// What a version of a core is called in the store:
		/// <c>&lt;id&gt;-&lt;version&gt;.chimeraCore</c>. Two versions of one core are
		/// two files sitting side by side, which is the whole of "version picking":
		/// discovery lists both, each with its own version and identity, and opening
		/// one is choosing it.
		/// </summary>
		public static string FileNameFor(string coreId, string version)
		{
			var id = Sanitise(coreId);
			var ver = Sanitise(version);
			return ver.Length is 0
				? id + CorePackageDiscovery.Extension
				: $"{id}-{ver}{CorePackageDiscovery.Extension}";
		}

		/// <summary>
		/// A version string reaches us from a GitHub tag, so it can hold anything a
		/// tag can. Everything that is not plainly safe in a file name becomes '_';
		/// the result is only a NAME, and the package's identity is still its SHA1.
		/// </summary>
		private static string Sanitise(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "";
			StringBuilder sb = new(s.Length);
			foreach (var c in s.Trim())
			{
				sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '.' or '+' or '_' ? c : '_');
			}
			// no leading dot: that is a hidden file on Unix and a name Windows dislikes
			return sb.ToString().TrimStart('.');
		}

		/// <summary>
		/// Moves a downloaded file into the store under <paramref name="fileName"/>,
		/// replacing an existing file of that name. Same name means same core at the
		/// same version, so replacing is idempotent rather than destructive - and it
		/// is how a half-written file from an interrupted download gets fixed.
		/// </summary>
		/// <returns>the path the package now has</returns>
		public static string Adopt(string downloadedPath, string fileName)
		{
			var target = System.IO.Path.Combine(Ensure(), fileName);
			// Move cannot overwrite on .NET Framework, and delete-then-move has a
			// window where neither exists. It is the smallest window available here,
			// and the file is re-downloadable, so it is the right trade.
			if (File.Exists(target)) File.Delete(target);
			File.Move(downloadedPath, target);
			return target;
		}

		/// <summary>
		/// Every package file the store holds, in no particular order. Used to answer
		/// "what is installed" without a full scan, and to find the other versions of
		/// a core when one is being replaced.
		/// </summary>
		public static IReadOnlyList<string> Installed()
		{
			try
			{
				return Directory.Exists(Path)
					? Directory.EnumerateFiles(Path, "*" + CorePackageDiscovery.Extension).ToList()
					: [ ];
			}
			catch (Exception)
			{
				return [ ];
			}
		}

		/// <summary>Whether <paramref name="path"/> is a file the manager may delete.</summary>
		public static bool Owns(string path)
		{
			try
			{
				var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
				return dir is not null && string.Equals(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar), Path.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
