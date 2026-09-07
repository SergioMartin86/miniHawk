#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Chimera.Client.Common
{
	/// <summary>One core as the manager sees it: what is installed, and what exists.</summary>
	public sealed class CoreManagerRow
	{
		/// <summary>
		/// The roster entry, or null for a package that is installed but not one of
		/// the official cores - somebody's own build, or a core from elsewhere. Those
		/// are listed too: a window that showed only what it could fetch would leave
		/// somebody unable to see the core they are actually running.
		/// </summary>
		public RosterCore? Core { get; init; }

		/// <summary>Every version of this core that is installed, newest first.</summary>
		public IReadOnlyList<DiscoveredCorePackage> Installed { get; init; } = [ ];

		/// <summary>
		/// Every version that exists, newest first - empty until somebody has asked.
		/// Nothing is fetched to build this list; the manager fills it in per core.
		/// </summary>
		public IReadOnlyList<CoreRelease> Available { get; init; } = [ ];

		/// <summary>What went wrong the last time this core's versions were asked for.</summary>
		public string? FeedError { get; init; }

		public string Name => Core?.Name ?? Installed.FirstOrDefault()?.Name ?? "";

		public IReadOnlyList<string> Systems
			=> Core?.Systems is { Count: not 0 } fromRoster ? fromRoster : Installed.FirstOrDefault()?.Systems ?? [ ];

		public bool IsInstalled => Installed.Count is not 0;

		/// <summary>
		/// True for something installed that no entry claims at all: a package
		/// somebody dropped in by hand. There is nowhere to check it for updates.
		/// </summary>
		public bool IsUnclaimed => Core is null;

		/// <summary>
		/// Which half of the list this belongs in. Official cores are the ones this
		/// build ships a roster entry for; everything else - cores added by hand, and
		/// packages nothing claims - is external, and sits below them.
		/// </summary>
		public bool IsOfficial => Core is { IsExternal: false };

		/// <summary>
		/// Whether removing this core should take its row away with it. An official
		/// core always has a row - it can be installed again from the roster - but an
		/// external one exists only because somebody added it or its package is here,
		/// so with the package gone there is nothing left to list.
		/// </summary>
		public bool RowGoesWhenRemoved => !IsOfficial;

		/// <summary>Every installed version's file, for removing the core entire.</summary>
		public IReadOnlyList<string> InstalledPaths => Installed.Select(static p => p.Path).ToList();

		/// <summary>
		/// The newest published version that is not installed, or null. Nothing is
		/// ever installed automatically because of this; it is what puts a core in the
		/// "has an update" list after somebody presses Check for updates.
		/// </summary>
		public CoreRelease? Update
		{
			get
			{
				if (!IsInstalled) return null; // nothing to update; it is simply not here
				// the NEWEST published version, and only if it is missing. Any older
				// one that happens not to be installed is not an update - somebody
				// holding the latest build would otherwise be told forever that there
				// is something newer, naming a version from last month.
				var newest = Available.FirstOrDefault(static r => r.Channel is not CoreChannel.Dev) ?? Available.FirstOrDefault();
				return newest is not null && !Has(newest) ? newest : null;
			}
		}

		/// <summary>
		/// The published release the row's date describes: the installed version where
		/// that is a version we have heard of, otherwise the newest known one. Null
		/// until somebody has asked this core what it has published - a date is the
		/// one thing in the list that cannot be read off a local file.
		/// </summary>
		public CoreRelease? Shown
		{
			get
			{
				if (Installed.FirstOrDefault()?.Version is { Length: not 0 } version)
				{
					return Available.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
				}
				return Available.FirstOrDefault();
			}
		}

		/// <summary>When <see cref="Shown"/> was published, or null if that is unknown.</summary>
		public DateTimeOffset? PublishedAt
			=> Shown is { PublishedAt: var when } && when != default ? when : null;

		/// <summary>
		/// How big this core is, in bytes; 0 when it cannot be known yet.
		///
		/// An installed one is measured on disk, so the column says something useful
		/// before anybody asks GitHub anything. Failing that - not installed, or a
		/// file that cannot be measured - the release itself declared a size, and that
		/// is a better answer than a blank.
		/// </summary>
		public long SizeBytes
		{
			get
			{
				if (IsInstalled && FileSize(Installed[0].Path) is > 0 and var onDisk) return onDisk;
				return Shown?.AssetSize ?? 0;
			}
		}

		private static long FileSize(string path)
		{
			try
			{
				System.IO.FileInfo info = new(path);
				return info.Exists ? info.Length : 0;
			}
			catch (Exception)
			{
				return 0; // a size is a nicety; nothing here is worth an exception
			}
		}

		/// <summary>
		/// Where this core is published, as <c>owner/name</c>; empty for a package
		/// nothing claims. That is the identifying part of the address and the part
		/// that fits a column - <see cref="RosterCore.Url"/> is the whole thing.
		/// </summary>
		public string Source => Core?.Repo ?? "";

		/// <summary>Whether <paramref name="release"/> is already in the store.</summary>
		public bool Has(CoreRelease release)
			=> Installed.Any(p => string.Equals(p.Version, release.Version, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// What File &gt; Core Manager shows: the roster and the store merged into one
	/// list. Kept out of the form so what somebody is told - which cores exist,
	/// which they have, which have something newer - can be tested without a window.
	/// </summary>
	public static class CoreManagerModel
	{
		/// <summary>
		/// Merges the shipped roster with what discovery found, name-sorted, official
		/// cores first. Installed packages that no roster entry claims come last and
		/// are marked unofficial rather than hidden.
		/// </summary>
		/// <param name="feeds">versions already fetched, by core id; cores absent from it simply have none yet</param>
		/// <param name="feedErrors">what went wrong per core id, for the ones that were asked and could not answer</param>
		public static IReadOnlyList<CoreManagerRow> Build(
			IEnumerable<RosterCore> roster,
			IEnumerable<DiscoveredCorePackage> discovered,
			IReadOnlyDictionary<string, IReadOnlyList<CoreRelease>>? feeds = null,
			IReadOnlyDictionary<string, string>? feedErrors = null)
		{
			var rosterList = roster.ToList();
			var packages = discovered.ToList();
			List<CoreManagerRow> rows = new();
			HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

			foreach (var core in rosterList)
			{
				var mine = packages.Where(p => Claims(core, p)).ToList();
				foreach (var p in mine) claimed.Add(p.Path);
				rows.Add(new CoreManagerRow
				{
					Core = core,
					Installed = Newest(mine),
					Available = feeds is not null && feeds.TryGetValue(core.Id, out var releases) ? releases : [ ],
					FeedError = feedErrors is not null && feedErrors.TryGetValue(core.Id, out var error) ? error : null,
				});
			}

			// whatever is installed that the roster does not know about, one row per
			// core name so several versions of somebody's own build still group
			foreach (var group in packages
				.Where(p => !claimed.Contains(p.Path))
				.GroupBy(static p => p.Name, StringComparer.OrdinalIgnoreCase))
			{
				rows.Add(new CoreManagerRow { Installed = Newest(group.ToList()) });
			}

			// official first, then everything else: the window draws a separator
			// between the two halves and this is what decides which side a row is on
			return rows
				.OrderBy(static r => r.IsOfficial ? 0 : 1)
				.ThenBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		/// <summary>
		/// Whether a package is a build of this roster core. The package's own
		/// coreName is what it calls itself, and the file name is how the store filed
		/// it; either is enough, because a hand-built package can be named anything
		/// while still being that core.
		/// </summary>
		private static bool Claims(RosterCore core, DiscoveredCorePackage package)
		{
			if (string.Equals(core.Name, package.Name, StringComparison.OrdinalIgnoreCase)) return true;
			var file = System.IO.Path.GetFileNameWithoutExtension(package.Path);
			return file.Equals(core.Id, StringComparison.OrdinalIgnoreCase)
				|| file.StartsWith(core.Id + "-", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Installed versions newest first. There is no date on a package, so this is
		/// as close as the store gets: what the manager downloaded most recently comes
		/// first, and everything else falls back to the version string.
		/// </summary>
		private static IReadOnlyList<DiscoveredCorePackage> Newest(List<DiscoveredCorePackage> packages)
			=> packages
				.OrderByDescending(static p => WrittenAt(p.Path))
				.ThenByDescending(static p => p.Version, StringComparer.OrdinalIgnoreCase)
				.ToList();

		private static DateTime WrittenAt(string path)
		{
			try
			{
				return System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
			}
			catch (Exception)
			{
				return DateTime.MinValue;
			}
		}
	}
}
