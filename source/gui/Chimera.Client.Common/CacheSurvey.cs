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

		/// <summary>
		/// Where the project this belongs to was last seen, for a project cache
		/// that was told; "" otherwise, and always "" for the other kinds.
		/// </summary>
		public string ProjectPath { get; init; } = "";

		/// <summary>The machine, as the movie records it (XBOX, NES); "" when not known.</summary>
		public string System { get; init; } = "";

		/// <summary>The core it is pinned to, by name; "" when not known.</summary>
		public string Core { get; init; } = "";

		/// <summary>The game's own files, as the project names them. Empty when not known.</summary>
		public IReadOnlyList<string> Games { get; init; } = Array.Empty<string>();

		/// <summary>
		/// The game in one column's worth of words. Most projects are one file;
		/// the ones that are not say how many rather than running off the edge.
		/// </summary>
		public string Game => Games.Count switch
		{
			0 => "",
			1 => Games[0],
			_ => $"{Games[0]}  (+{Games.Count - 1} more)",
		};

		/// <summary>
		/// True when this belongs to a project whose file is not where it was last
		/// seen - deleted, or moved and not opened since. These are the rows worth
		/// reclaiming: nothing points at them any more, and if the project turns up
		/// again it simply builds its history back.
		/// </summary>
		public bool Orphaned { get; init; }

		public long Bytes { get; init; }

		/// <summary>When anything in it was last written; default when unknown.</summary>
		public DateTime LastUsed { get; init; }

		/// <summary>
		/// True while something open is relying on it. Deleting one of these would
		/// pull the floor out from under a running session, so the manager refuses
		/// rather than asking.
		/// </summary>
		public bool InUse { get; init; }

		/// <summary>
		/// True when the auto-clean may not take this one (see <see cref="CacheLocks"/>).
		/// It says nothing about removing it by hand: a person who ticks a row and
		/// presses Remove has already decided, and a padlock that also argued with
		/// them would be a lock on the wrong thing.
		/// </summary>
		public bool Locked { get; init; }

		/// <summary>
		/// Why this row is worth a second look, or "". Only ever advice: an orphan
		/// is still perfectly good, it is simply the one nothing is asking for.
		/// </summary>
		public string Note => Orphaned
			? "The project this belongs to is not where it was last seen."
			: "";

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
	/// How large the cache is allowed to get, and whether anything enforces it.
	///
	/// Passed around rather than read out of the config where it is needed, so
	/// that what the limit DOES can be tested without a config file - the same
	/// reason the survey takes its roots as arguments.
	/// </summary>
	public sealed class CacheCleanPolicy
	{
		/// <summary>
		/// Whether the cache is held under the limit on its own. On by default:
		/// a cache that grows without bound is a disk that fills up while somebody
		/// is working, and every rule here already says that losing a cache costs
		/// time and never work. Anyone who would rather decide by hand turns it
		/// off, and anyone who wants one particular run kept locks it.
		/// </summary>
		public bool Enabled { get; set; } = true;

		/// <summary>
		/// What the whole cache may weigh. A hundred gigabytes because a single
		/// PS2 or PS3 greenzone runs to tens of them, so a smaller number would
		/// spend its life evicting the run being worked on, and a machine with
		/// room to spare loses nothing by keeping more.
		/// </summary>
		public long LimitBytes { get; set; } = DefaultLimitBytes;

		/// <summary>
		/// In megabytes, because that is the unit the config stores a size in
		/// (see <c>MovieConfig.GreenzoneBudgetMb</c>) and the one its round-trip
		/// test knows how to keep stable.
		/// </summary>
		public const int DefaultLimitMb = 100 * 1024;

		public const long DefaultLimitBytes = DefaultLimitMb * 1024L * 1024L;
	}

	/// <summary>What one pass of the auto-clean did.</summary>
	public sealed class CacheCleanResult
	{
		public IReadOnlyList<CacheItem> Removed { get; init; } = Array.Empty<CacheItem>();

		/// <summary>What the cache weighed before, and what it weighs now.</summary>
		public long Before { get; init; }

		public long After { get; init; }

		/// <summary>
		/// True when the cache is STILL over the limit and nothing else may be
		/// taken - everything left is locked, or in use. Not a failure: it is the
		/// lock doing exactly what it was asked to, and worth saying so rather
		/// than silently going on being over.
		/// </summary>
		public bool StillOver { get; init; }
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
			var locks = CacheLocks.Read();

			foreach (var project in ProjectCache.All())
			{
				var inUse = openProjectId is { Length: > 0 }
					&& string.Equals(openProjectId, project.Id, StringComparison.OrdinalIgnoreCase);
				items.Add(new CacheItem
				{
					Kind = CacheKind.Project,
					Label = project.Label.Length is not 0 ? project.Label : "(a project that never said its name)",
					Detail = project.Id,
					Path = project.Path,
					ProjectPath = project.ProjectPath,
					System = project.System,
					Core = project.Core,
					Games = project.Games,
					// a project that is open is obviously not missing, whatever the
					// note says - it was opened from somewhere
					Orphaned = !inUse && project.ProjectPath.Length is not 0 && !File.Exists(project.ProjectPath),
					Bytes = project.Bytes,
					LastUsed = project.LastUsed,
					InUse = inUse,
					Locked = CacheLocks.IsLocked(locks, CacheKind.Project, project.Path),
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
					Core = label,
					Detail = sha1.Length >= 8 ? sha1.Substring(0, 8) : sha1,
					Path = dir,
					Bytes = SizeOf(dir),
					LastUsed = TouchedAt(dir),
					InUse = sha1.Length is not 0 && loadedPackageSha1s is not null
						&& loadedPackageSha1s.Any(s => string.Equals(s, sha1, StringComparison.OrdinalIgnoreCase)),
					Locked = CacheLocks.IsLocked(locks, CacheKind.CorePackage, dir),
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
						Locked = CacheLocks.IsLocked(locks, CacheKind.CompiledCode, versionDir),
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
					Locked = CacheLocks.IsLocked(locks, CacheKind.CoreVersions, feed),
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
				// a lock belongs to the thing it was put on; leaving it behind would
				// hand its answer to whatever is written at that location next
				CacheLocks.Forget(item.Path);
				return null;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return ex.Message;
			}
		}

		/// <summary>
		/// Which entries the auto-clean would take, in the order it would take
		/// them, to bring the cache back under the limit. Nothing is removed by
		/// asking - this is what the window shows before it does anything, and
		/// what a test can check without a disk to delete from.
		///
		/// OLDEST FIRST, by when anything in it was last written. That is the one
		/// ordering that means "least likely to be wanted next", and it is the
		/// rule a lock exists to overrule for the run where it is wrong.
		///
		/// What may never go: a cache something open is standing on (removing it
		/// would cost work, not time) and a locked one. Entries that weigh nothing
		/// are left alone too - taking them would not move the total, so removing
		/// them would be deletion for its own sake.
		/// </summary>
		public static IReadOnlyList<CacheItem> WhatWouldGo(IReadOnlyList<CacheItem> items, long limitBytes)
		{
			var total = items.Sum(static i => i.Bytes);
			if (total <= limitBytes) return Array.Empty<CacheItem>();

			List<CacheItem> going = new();
			var candidates = items
				.Where(static i => !i.InUse && !i.Locked && i.Bytes > 0)
				.OrderBy(static i => i.LastUsed)
				// among entries of the same age the biggest goes first, so reaching
				// the limit costs as few removals as it can
				.ThenByDescending(static i => i.Bytes);
			foreach (var item in candidates)
			{
				if (total <= limitBytes) break;
				going.Add(item);
				total -= item.Bytes;
			}
			return going;
		}

		/// <summary>
		/// Brings the cache back under its limit, oldest first. Does nothing at
		/// all when the policy is off or the cache is already under it.
		/// </summary>
		public static CacheCleanResult AutoClean(IReadOnlyList<CacheItem> items, CacheCleanPolicy policy)
		{
			var before = items.Sum(static i => i.Bytes);
			if (!policy.Enabled || before <= policy.LimitBytes)
			{
				return new CacheCleanResult { Before = before, After = before };
			}

			List<CacheItem> removed = new();
			var after = before;
			foreach (var item in WhatWouldGo(items, policy.LimitBytes))
			{
				if (Remove(item) is not null) continue;
				removed.Add(item);
				after -= item.Bytes;
			}
			return new CacheCleanResult
			{
				Removed = removed,
				Before = before,
				After = after,
				StillOver = after > policy.LimitBytes,
			};
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
