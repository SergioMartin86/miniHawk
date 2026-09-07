#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Chimera.Client.Common
{
	/// <summary>
	/// Which kind of build a release is. The two that matter are the ones the
	/// pipeline actually produces (see <c>.github/workflows/release.yml</c>): a
	/// rolling <c>dev</c> that is replaced on every green push, and an immutable
	/// dated <c>nightly</c> that is never deleted.
	/// </summary>
	public enum CoreChannel
	{
		/// <summary>Rolling: always the newest, and gone the next time main moves.</summary>
		Dev,

		/// <summary>Dated and permanent. What a movie replays against in five years.</summary>
		Nightly,

		/// <summary>A tagged release that is neither: rare, and treated as permanent.</summary>
		Release,
	}

	/// <summary>
	/// One published build of one core: what the manager lists, and what it
	/// downloads. Built from a GitHub release, but deliberately not a mirror of
	/// one - everything here is something the manager shows or uses.
	/// </summary>
	public sealed class CoreRelease
	{
		/// <summary>The release's git tag: <c>dev</c>, or <c>nightly-2026-09-07</c>.</summary>
		public string Tag { get; init; } = "";

		public CoreChannel Channel { get; init; }

		/// <summary>
		/// The core's version, as the build stamped it: the commit it was made from.
		/// This is what a movie cites (<c>CoreVersion</c>) and what names the file in
		/// the store, so it is read out of the ASSET name, which the build wrote,
		/// rather than out of the tag, which a person could have typed.
		/// </summary>
		public string Version { get; init; } = "";

		public DateTimeOffset PublishedAt { get; init; }

		public string AssetName { get; init; } = "";

		public string AssetUrl { get; init; } = "";

		public long AssetSize { get; init; }

		/// <summary>
		/// The digest GitHub reports for the asset (<c>sha256:...</c>), where it
		/// reports one at all. Older releases have none, so it is a check to make when
		/// it is available rather than something to require.
		/// </summary>
		public string? Digest { get; init; }

		/// <summary>How the version reads in a list: short, and never empty.</summary>
		public string DisplayVersion => Version.Length is not 0 ? Version : Tag;

		/// <summary>
		/// The version cut to the length a commit is usually read at. A core's version
		/// IS the commit it was built from, and eight characters is what the rest of
		/// the frontend shows of a hash.
		/// </summary>
		public string ShortVersion
			=> DisplayVersion.Length <= 8 ? DisplayVersion : DisplayVersion.Substring(0, 8);

		/// <summary>
		/// The one line the manager puts under a chosen version: when it was published
		/// and which commit it is. Those are the two things somebody comparing two
		/// builds actually needs.
		/// </summary>
		public string DateAndCommit
			=> PublishedAt == default
				? ShortVersion
				: $"{PublishedAt.ToLocalTime():yyyy-MM-dd}  ({ShortVersion})";

		public override string ToString() => $"{DisplayVersion} ({Channel}, {PublishedAt:yyyy-MM-dd})";
	}

	/// <summary>
	/// Turns a GitHub releases response into the versions of one core.
	///
	/// This is the whole of "the manager checks the official releases": one request
	/// per core, made when somebody presses something. Parsing is separate from
	/// fetching so what the manager believes about a feed can be tested without one.
	/// </summary>
	public static class CoreReleases
	{
		/// <summary>The permanent release a core's index is attached to, and the asset on it.</summary>
		public const string IndexTag = "index";

		public const string IndexFile = "releases.json";

		/// <summary>
		/// Where a core says what it has published: an asset its own publish job wrote
		/// (tools/write-core-index.sh) onto a permanent release, so the address is
		/// fixed for the life of the core.
		///
		/// NOT api.github.com, and this is the point of the whole arrangement: the API
		/// allows an unauthenticated address 60 requests an hour, one per core per
		/// question, and charges for a 304 as readily as for a 200 - so one press of
		/// Check for updates over fifteen cores spends a quarter of the hour's budget.
		/// A release asset costs nothing at all: it redirects off the API entirely.
		///
		/// The file holds GitHub's own /releases shape, trimmed, so <see cref="Parse"/>
		/// reads it unchanged: one shape, one parser, nothing to keep in step.
		/// </summary>
		public static string IndexUrl(string repo)
			=> $"https://github.com/{repo}/releases/download/{IndexTag}/{IndexFile}";

		/// <summary>
		/// The releases of <paramref name="coreId"/> found in a GitHub
		/// <c>/releases</c> response, newest first.
		///
		/// A release without an asset for this core is skipped rather than listed
		/// empty: one repository could publish more than one package, and a release
		/// that failed to upload is not a version anybody can install.
		/// </summary>
		public static IReadOnlyList<CoreRelease> Parse(string json, string coreId)
		{
			// dates stay text: Newtonsoft would otherwise hand back a JValue holding a
			// DateTime, which will not cast to DateTimeOffset under Mono
			using JsonTextReader reader = new(new System.IO.StringReader(json)) { DateParseHandling = DateParseHandling.None };
			var releases = JArray.Load(reader);
			List<CoreRelease> found = new();
			foreach (var release in releases.OfType<JObject>())
			{
				if (release.Value<bool?>("draft") is true) continue; // not published to anyone
				var tag = release.Value<string>("tag_name") ?? "";
				var asset = FindAsset(release, coreId);
				if (asset is null) continue;
				var assetName = asset.Value<string>("name") ?? "";
				found.Add(new CoreRelease
				{
					Tag = tag,
					Channel = ChannelOf(tag),
					Version = VersionFromAssetName(assetName, coreId, tag),
					PublishedAt = ParseTime(release.Value<string>("published_at")) ?? ParseTime(release.Value<string>("created_at")) ?? default,
					AssetName = assetName,
					AssetUrl = asset.Value<string>("browser_download_url") ?? "",
					AssetSize = asset.Value<long?>("size") ?? 0,
					Digest = asset.Value<string>("digest"),
				});
			}
			return found
				.Where(static r => r.AssetUrl.Length is not 0)
				.OrderByDescending(static r => r.PublishedAt)
				.ThenBy(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		private static DateTimeOffset? ParseTime(string? text)
			=> DateTimeOffset.TryParse(
				text,
				System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
				out var when) ? when : null;

		/// <summary>
		/// The asset this core is published as. Matched on the package extension and
		/// the core's id, so a repository that also attaches a source zip, a checksum
		/// file or another core's package is read correctly.
		/// </summary>
		private static JObject? FindAsset(JObject release, string coreId)
		{
			var assets = (release["assets"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
			var packages = assets
				.Where(a => (a.Value<string>("name") ?? "").EndsWith(CorePackageDiscovery.Extension, StringComparison.OrdinalIgnoreCase))
				.ToList();
			if (packages.Count is 0) return null;
			return packages.FirstOrDefault(a => NamesCore(a.Value<string>("name") ?? "", coreId))
				?? (packages.Count is 1 ? packages[0] : null); // one package and no id in its name: it is this core's
		}

		private static bool NamesCore(string assetName, string coreId)
		{
			var stem = assetName.Substring(0, assetName.Length - CorePackageDiscovery.Extension.Length);
			return stem.Equals(coreId, StringComparison.OrdinalIgnoreCase)
				|| stem.StartsWith(coreId + "-", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// <c>gpgx-4ed3532117ad.chimeraCore</c> -&gt; <c>4ed3532117ad</c>. Falls back to
		/// the tag for an asset that carries no version, and to nothing at all rather
		/// than inventing one.
		/// </summary>
		public static string VersionFromAssetName(string assetName, string coreId, string tag)
		{
			if (assetName.EndsWith(CorePackageDiscovery.Extension, StringComparison.OrdinalIgnoreCase))
			{
				var stem = assetName.Substring(0, assetName.Length - CorePackageDiscovery.Extension.Length);
				if (stem.StartsWith(coreId + "-", StringComparison.OrdinalIgnoreCase))
				{
					return stem.Substring(coreId.Length + 1);
				}
			}
			return tag;
		}

		/// <summary>
		/// The core id in a published asset name: everything before the version.
		/// <c>gpgx-4ed3532117ad.chimeraCore</c> -&gt; <c>gpgx</c>, and
		/// <c>dosbox-x-4ed3532117ad.chimeraCore</c> -&gt; <c>dosbox-x</c>, which is why
		/// it is the LAST hyphen that separates them - an id may contain one, and a
		/// published version may not (a version carrying "-dirty" is refused at
		/// publish time).
		/// </summary>
		public static string IdFromAssetName(string assetName)
		{
			if (!assetName.EndsWith(CorePackageDiscovery.Extension, StringComparison.OrdinalIgnoreCase)) return "";
			var stem = assetName.Substring(0, assetName.Length - CorePackageDiscovery.Extension.Length);
			var dash = stem.LastIndexOf('-');
			return dash <= 0 ? stem : stem.Substring(0, dash);
		}

		/// <summary>
		/// Which channel a tag names. Anything that is not the rolling <c>dev</c> or a
		/// dated <c>nightly-</c> is taken to be a permanent release, because the
		/// dangerous mistake is the other way round: treating something permanent as
		/// disposable and letting a movie's core disappear.
		/// </summary>
		public static CoreChannel ChannelOf(string tag)
		{
			if (tag.Equals("dev", StringComparison.OrdinalIgnoreCase)) return CoreChannel.Dev;
			if (tag.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase)) return CoreChannel.Nightly;
			return CoreChannel.Release;
		}

		/// <summary>
		/// The newest release of <paramref name="channel"/>, or null. The default
		/// channel is Nightly: it is immutable and never deleted, so a movie recorded
		/// against it stays replayable, which dev cannot promise.
		/// </summary>
		public static CoreRelease? Newest(IEnumerable<CoreRelease> releases, CoreChannel channel = CoreChannel.Nightly)
			=> releases.FirstOrDefault(r => r.Channel == channel)
				?? (channel is CoreChannel.Nightly ? releases.FirstOrDefault(static r => r.Channel is CoreChannel.Release) : null);
	}
}
