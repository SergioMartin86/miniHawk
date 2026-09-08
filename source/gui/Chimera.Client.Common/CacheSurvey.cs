#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Chimera.Client.Common
{
	/// <summary>What kind of work a cached thing saves, which is what decides the cost of losing it.</summary>
	public enum CacheKind
	{
		/// <summary>A project's state history and where this machine found its files.</summary>
		Project,

		/// <summary>A core package unzipped so it can be loaded.</summary>
		CorePackage,

		/// <summary>A core's translation of a game's code (docs/compile-cache.md).</summary>
		CompiledCode,

		/// <summary>What each core's repository last said it had published.</summary>
		CoreVersions,
	}

	/// <summary>One thing on disk that can be thrown away.</summary>
	public sealed class CacheItem
	{
		public CacheKind Kind { get; init; }

		/// <summary>What a person would call it: a project's title, a core's name.</summary>
		public string Label { get; init; } = "";

		/// <summary>The identifying detail under the label - a version, an id.</summary>
		public string Detail { get; init; } = "";

		public string Path { get; init; } = "";

		public long Bytes { get; init; }

		/// <summary>When anything in it was last written; default when unknown.</summary>
		public DateTime LastUsed { get; init; }

		/// <summary>
		/// True while something open is relying on it. Deleting one of these would
		/// pull the floor out from under a running session, so the manager refuses
		/// rather than asking.
		/// </summary>
		public bool InUse { get; init; }

		/// <summary>What is actually lost by deleting it. Never work; always time.</summary>
		public string Cost => Kind switch
		{
			CacheKind.Project => "The run replays instead of resuming, and its files are asked for once more.",
			CacheKind.CorePackage => "The package is unzipped again the next time it is loaded.",
			CacheKind.CompiledCode => "The game's code is translated again, which is minutes on a first boot.",
			CacheKind.CoreVersions => "The Core Manager asks each repository again instead of showing what it saw last.",
			_ => "",
		};
	}

	/// <summary>
	/// Everything Chimera keeps on disk that it could work out again.
	///
	/// The rule these all share is the one the greenzone has always been held to:
	/// losing a cache costs RECOMPUTATION, never work. That is what makes a window
	/// like this safe to offer at all - every row can be deleted, and the worst
	/// outcome is waiting. What it is emphatically not is a manager for things
	/// that cannot be rebuilt: installed cores are not here (they are the Core
	/// Manager's, and a movie needs the exact build that recorded it), and neither
	/// are projects, roms or firmware.
	///
	/// Kept out of the window so that what is listed, what it costs and what may
	/// not be deleted can be tested without one - the same split as the firmware
	/// survey and the core manager.
	/// </summary>
	public static class CacheSurvey
	{
		/// <summary>
		/// Takes stock. The caller supplies the roots it knows about and what is
		/// currently open, because the survey has no business reaching for config
		/// or for the running session.
		/// </summary>
		/// <param name="corePackageCacheRoot">where packages are unzipped (beside the executable)</param>
		/// <param name="compiledCodeRoot">the Core Cache path, where translated code lives</param>
		/// <param name="openProjectId">the project open right now, or null</param>
		/// <param name="loadedPackageSha1s">packages a loaded core is using right now</param>
		public static IReadOnlyList<CacheItem> Take(
			string? corePackageCacheRoot,
			string? compiledCodeRoot,
			string? openProjectId = null,
			IReadOnlyCollection<string>? loadedPackageSha1s = null)
		{
			List<CacheItem> items = new();

			foreach (var project in ProjectCache.All())
			{
				items.Add(new CacheItem
				{
					Kind = CacheKind.Project,
					Label = project.Label.Length is not 0 ? project.Label : "(a project that never said its name)",
					Detail = project.Id,
					Path = project.Path,
					Bytes = project.Bytes,
					LastUsed = project.LastUsed,
					InUse = openProjectId is { Length: > 0 } && string.Equals(openProjectId, project.Id, StringComparison.OrdinalIgnoreCase),
				});
			}

			// An unzipped package is named "<package>-<sha1>", which is how a
			// loaded core's own hash says which row it is standing on.
			foreach (var dir in Directories(corePackageCacheRoot))
			{
				var name = System.IO.Path.GetFileName(dir);
				var cut = name.LastIndexOf('-');
				var label = cut > 0 ? name.Substring(0, cut) : name;
				var sha1 = cut > 0 ? name.Substring(cut + 1) : "";
				items.Add(new CacheItem
				{
					Kind = CacheKind.CorePackage,
					Label = label,
					Detail = sha1.Length >= 8 ? sha1.Substring(0, 8) : sha1,
					Path = dir,
					Bytes = SizeOf(dir),
					LastUsed = TouchedAt(dir),
					InUse = sha1.Length is not 0 && loadedPackageSha1s is not null
						&& loadedPackageSha1s.Any(s => string.Equals(s, sha1, StringComparison.OrdinalIgnoreCase)),
				});
			}

			// <core>/<package version>/, one directory per build that compiled
			// anything - a different build generates different code and must not
			// read the old one's objects.
			foreach (var coreDir in Directories(compiledCodeRoot))
			{
				foreach (var versionDir in Directories(coreDir))
				{
					items.Add(new CacheItem
					{
						Kind = CacheKind.CompiledCode,
						Label = System.IO.Path.GetFileName(coreDir),
						Detail = Short(System.IO.Path.GetFileName(versionDir)),
						Path = versionDir,
						Bytes = SizeOf(versionDir),
						LastUsed = TouchedAt(versionDir),
					});
				}
			}

			var feed = System.IO.Path.Combine(CoreStore.Path, ".feed-cache");
			if (Directory.Exists(feed))
			{
				items.Add(new CacheItem
				{
					Kind = CacheKind.CoreVersions,
					Label = "Published core versions",
					Detail = "",  /* the label says it; the column is for identifying detail */
					Path = feed,
					Bytes = SizeOf(feed),
					LastUsed = TouchedAt(feed),
				});
			}

			return items
				.OrderBy(static i => i.Kind)
				.ThenByDescending(static i => i.Bytes)
				.ToList();
		}

		/// <summary>
		/// Deletes one cached thing. Refuses what something open is standing on,
		/// because the cost of that is not recomputation.
		/// </summary>
		/// <returns>null when it went, otherwise why it did not</returns>
		public static string? Remove(CacheItem item)
		{
			if (item.InUse) return $"{item.Label} is in use right now.";
			try
			{
				if (Directory.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
				return null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return ex.Message;
			}
		}

		/// <summary>A size in the units somebody compares two rows in.</summary>
		public static string Size(long bytes)
		{
			if (bytes <= 0) return "";
			double mb = bytes / 1024.0 / 1024.0;
			if (mb >= 1024.0) return $"{mb / 1024.0:0.0} GB";
			return mb >= 1.0 ? $"{mb:0.0} MB" : $"{bytes / 1024.0:0} KB";
		}

		/// <summary>How each kind reads in a list.</summary>
		public static string Describe(CacheKind kind) => kind switch
		{
			CacheKind.Project => "Project",
			CacheKind.CorePackage => "Unpacked core",
			CacheKind.CompiledCode => "Compiled code",
			CacheKind.CoreVersions => "Core versions",
			_ => kind.ToString(),
		};

		private static IEnumerable<string> Directories(string? root)
		{
			if (string.IsNullOrWhiteSpace(root)) return Array.Empty<string>();
			try
			{
				return Directory.Exists(root) ? Directory.EnumerateDirectories(root!).ToList() : Array.Empty<string>();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return Array.Empty<string>();
			}
		}

		private static long SizeOf(string dir)
		{
			try
			{
				return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(static f => f.Length);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return 0;
			}
		}

		private static DateTime TouchedAt(string dir)
		{
			try
			{
				var newest = DateTime.MinValue;
				foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
				{
					if (f.LastWriteTimeUtc > newest) newest = f.LastWriteTimeUtc;
				}
				return newest;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return DateTime.MinValue;
			}
		}

		/// <summary>
		/// A version at the length a person reads one. It is a commit, so eight
		/// characters is what the rest of the frontend shows; anything shorter
		/// (a "dev") is already its own name.
		/// </summary>
		private static string Short(string version)
			=> version.Length > 8 ? version.Substring(0, 8) : version;
	}
}
