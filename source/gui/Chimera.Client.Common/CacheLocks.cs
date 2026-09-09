#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

namespace Chimera.Client.Common
{
	/// <summary>
	/// Which cached things the auto-clean is not allowed to take.
	///
	/// The auto-clean removes the oldest entries when the cache goes over its
	/// limit, and "oldest" is a decent rule that is occasionally exactly wrong:
	/// the run somebody put a fortnight into and has not opened this month is
	/// both the oldest thing on disk and the one it would hurt most to lose. A
	/// lock is how that gets said. It is the ONLY thing it says - a locked entry
	/// can still be removed by hand in the cache manager, because a person who
	/// ticks a row and presses Remove has already decided.
	///
	/// The defaults follow which way round the mistake would matter. A greenzone
	/// is unlocked: it is the big thing, it is what a limit exists to bound, and
	/// losing one costs a replay. Everything else - unpacked cores, compiled
	/// code, the version index - starts locked: it is small enough that evicting
	/// it frees nothing worth having, and losing it stalls the next boot for no
	/// gain. So the limit falls where the room actually goes, and the little
	/// things stay put unless somebody says otherwise.
	///
	/// Kept beside the caches rather than inside them, and keyed by location: a
	/// core package directory is unzipped from a package and must stay exactly
	/// what the package said it was, so nothing of Chimera's own goes in it.
	/// Locations are machine-local and so is this file, which is the same thing
	/// the caches themselves are.
	///
	/// One file with one writer: setting a lock reads the book, changes what it
	/// was asked about and writes the whole thing back. Two Chimeras open at once
	/// could talk over each other and the later save would win, which is a lost
	/// padlock and not a lost run - not worth a lock file to prevent.
	/// </summary>
	public static class CacheLocks
	{
		/// <summary>
		/// Whether this kind starts locked, for an entry nobody has spoken about.
		/// Greenzones are the room; the rest is the furniture.
		/// </summary>
		public static bool DefaultFor(CacheKind kind) => kind is not CacheKind.Project;

		/// <summary>The lock book. Named here so a person can delete it, which resets every lock.</summary>
		public static string File => Path.Combine(ProjectCache.DataHome, "cache-locks.json");

		/// <summary>
		/// What somebody has said about each location, for the ones somebody has
		/// said anything about. Read whole rather than asked per row: a survey
		/// wants every answer at once, and there are as many rows as caches.
		/// </summary>
		public static IReadOnlyDictionary<string, bool> Read() => ReadRaw();

		private static Dictionary<string, bool> ReadRaw()
		{
			Dictionary<string, bool> said = new(StringComparer.Ordinal);
			try
			{
				if (!System.IO.File.Exists(File)) return said;
				foreach (var pair in JObject.Parse(System.IO.File.ReadAllText(File)))
				{
					if (pair.Value?.Type is JTokenType.Boolean) said[pair.Key] = pair.Value.Value<bool>();
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
			{
				// a lock book that will not be read leaves every entry at its default,
				// which is the safe way round: nothing that was locked gets evicted by
				// this run, because the auto-clean is what re-reads it next time
				return new Dictionary<string, bool>(StringComparer.Ordinal);
			}
			return said;
		}

		/// <summary>Whether one entry is locked, given what the book says and what its kind assumes.</summary>
		public static bool IsLocked(IReadOnlyDictionary<string, bool> said, CacheKind kind, string path)
			=> said.TryGetValue(path, out var locked) ? locked : DefaultFor(kind);

		/// <summary>Whether one entry is locked, reading the book to find out.</summary>
		public static bool IsLocked(CacheKind kind, string path) => IsLocked(Read(), kind, path);

		/// <summary>
		/// Locks or unlocks these. Only what differs from its kind's default is
		/// written down, so the book stays a list of deliberate exceptions rather
		/// than a second copy of the cache - and so changing a default later takes
		/// effect for everything nobody has overruled.
		/// </summary>
		public static void Set(IEnumerable<CacheItem> items, bool locked)
		{
			var said = ReadRaw();
			foreach (var item in items)
			{
				if (item.Path.Length is 0) continue;
				if (locked == DefaultFor(item.Kind)) said.Remove(item.Path);
				else said[item.Path] = locked;
			}
			Write(said);
		}

		/// <summary>Forgets what was said about a location. For one that has just been removed.</summary>
		public static void Forget(string path)
		{
			var said = ReadRaw();
			if (!said.Remove(path)) return;
			Write(said);
		}

		/// <summary>
		/// Writes the book, dropping whatever names a location that is no longer
		/// there. A cache that has been removed takes its lock with it, or the
		/// book grows forever and a location reused by something else inherits an
		/// answer nobody gave about it.
		/// </summary>
		private static void Write(IReadOnlyDictionary<string, bool> said)
		{
			try
			{
				JObject root = new();
				foreach (var pair in said.OrderBy(static p => p.Key, StringComparer.Ordinal))
				{
					if (!Directory.Exists(pair.Key)) continue;
					root[pair.Key] = pair.Value;
				}
				Directory.CreateDirectory(ProjectCache.DataHome);
				System.IO.File.WriteAllText(File, root.ToString(Newtonsoft.Json.Formatting.Indented));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
				// a lock that cannot be written down is a lock that does not hold, and
				// the window says what it reads back rather than what it was told
			}
		}
	}
}
