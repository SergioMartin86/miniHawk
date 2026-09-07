#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;

namespace Chimera.Client.Common
{
	/// <summary>What asking a core's repository for its versions produced.</summary>
	public sealed class CoreFeedResult
	{
		public IReadOnlyList<CoreRelease> Releases { get; init; } = [ ];

		/// <summary>Non-null if the question could not be answered; showable as-is.</summary>
		public string? Error { get; init; }

		/// <summary>True when the answer came out of the cache because GitHub said nothing changed.</summary>
		public bool FromCache { get; init; }

		public bool Ok => Error is null;
	}

	/// <summary>
	/// Asks a core's GitHub repository what versions of it exist.
	///
	/// Nothing here ever runs on its own. Chimera does not poll, does not check at
	/// startup, and makes no request until somebody presses Download or Check for
	/// updates - so the whole of this class is on a path a person started.
	///
	/// It asks the core, not GitHub's API. Each core's publish job attaches a
	/// releases.json to a permanent release on its own repository
	/// (tools/write-core-index.sh) and this downloads that asset.
	///
	/// The API was the obvious way and is unusable: 60 requests an hour per
	/// ADDRESS, one per core per question, and a 304 charged exactly like a 200
	/// (measured 2026-09-07). Fifteen cores make one press of Check for updates
	/// cost fifteen, so four presses is the hour's entire budget - which anybody
	/// developing exhausts before lunch, and which a shared address exhausts on
	/// somebody else's behalf. A release asset costs nothing at all: it redirects
	/// off the API entirely.
	///
	/// There is no fallback to the API, on purpose. A fallback would hide the case
	/// this has to get right - a core whose index is missing - behind a path that
	/// works four times an hour and then mysteriously stops.
	///
	/// The ETag cache below no longer buys anything against a limit; it saves the
	/// bandwidth, and it is what lets an offline manager list what it saw last time.
	/// </summary>
	public sealed class CoreFeed
	{
		/// <summary>
		/// GitHub requires a User-Agent and refuses requests without one. Naming
		/// Chimera also means a rate-limited request is attributable to the right
		/// program rather than to "some .NET process".
		/// </summary>
		public const string UserAgent = "Chimera-Core-Manager";

		private readonly HttpClient _http;

		private readonly string _cacheDir;

		public CoreFeed(HttpClient? http = null, string? cacheDir = null)
		{
			_http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent))
			{
				// a handed-in client may already carry one; that is fine
			}
			_cacheDir = cacheDir ?? System.IO.Path.Combine(CoreStore.Path, ".feed-cache");
		}

		/// <summary>
		/// The versions of <paramref name="core"/> that exist, newest first. Never
		/// throws: a network that is not there, a repository that is not there, and a
		/// rate limit are all ordinary answers the manager has to show somebody.
		/// </summary>
		public async Task<CoreFeedResult> FetchAsync(RosterCore core, CancellationToken cancel = default)
		{
			var cached = ReadCache(core.Repo);
			try
			{
				using HttpRequestMessage request = new(HttpMethod.Get, CoreReleases.IndexUrl(core.Repo));
				if (cached?.ETag is { Length: not 0 } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

				using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);

				if (response.StatusCode is HttpStatusCode.NotModified && cached is not null)
				{
					return new CoreFeedResult { Releases = CoreReleases.Parse(cached.Body, core.Id), FromCache = true };
				}
				if (response.StatusCode is HttpStatusCode.NotFound)
				{
					// the index is written by a core's publish job, so the ordinary
					// reason it is absent is that the core has not published since -
					// which is a different problem from a repository that has gone,
					// and says so rather than blaming the address
					return new CoreFeedResult
					{
						Error = $"{core.Repo} publishes no version index yet ({CoreReleases.IndexTag}/{CoreReleases.IndexFile}). "
							+ "It appears the next time that core publishes.",
					};
				}
				if (!response.IsSuccessStatusCode)
				{
					return new CoreFeedResult { Error = $"{core.Repo} answered {(int) response.StatusCode} {response.ReasonPhrase}" };
				}

				var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				WriteCache(core.Repo, response.Headers.ETag?.Tag ?? "", body);
				return new CoreFeedResult { Releases = CoreReleases.Parse(body, core.Id) };
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// offline is the common case here, and the cache is what makes the
				// window still worth opening
				return new CoreFeedResult
				{
					Error = $"could not reach GitHub: {ex.Message}",
					Releases = CachedReleases(cached, core.Id),
					FromCache = true,
				};
			}
		}

		/// <summary>
		/// Asks a repository what core it publishes, for one somebody has just named
		/// by its GitHub page. Returns an entry ready to be listed, or the reason it
		/// cannot be: the id and the name come from the newest published ASSET, since
		/// nothing else about an external core is known here.
		/// </summary>
		public async Task<(RosterCore? Core, string? Error)> ProbeAsync(string repo, CancellationToken cancel = default)
		{
			// Id empty: the feed then takes the one package asset a release carries,
			// which is what tells us what this core is called.
			var result = await FetchAsync(new RosterCore { Id = "", Repo = repo }, cancel).ConfigureAwait(false);
			if (result.Error is not null) return (null, result.Error);
			if (result.Releases.Count is 0)
			{
				return (null, $"{repo} publishes no Chimera core package. Check it is the right repository.");
			}
			var id = CoreReleases.IdFromAssetName(result.Releases[0].AssetName);
			if (id.Length is 0) return (null, $"{repo} publishes an asset this cannot make sense of ({result.Releases[0].AssetName}).");
			return (new RosterCore { Id = id, Name = id, Repo = repo, IsExternal = true }, null);
		}

		private static IReadOnlyList<CoreRelease> CachedReleases(CachedFeed? cached, string coreId)
		{
			if (cached is null) return [ ];
			try
			{
				return CoreReleases.Parse(cached.Body, coreId);
			}
			catch (Exception)
			{
				return [ ];
			}
		}

		private sealed class CachedFeed
		{
			[JsonProperty("etag")]
			public string ETag { get; set; } = "";

			[JsonProperty("body")]
			public string Body { get; set; } = "";
		}

		private string CachePath(string repo)
			=> System.IO.Path.Combine(_cacheDir, repo.Replace('/', '_') + ".json");

		private CachedFeed? ReadCache(string repo)
		{
			try
			{
				var path = CachePath(repo);
				return File.Exists(path) ? JsonConvert.DeserializeObject<CachedFeed>(File.ReadAllText(path)) : null;
			}
			catch (Exception)
			{
				return null; // a damaged cache is just a cache miss
			}
		}

		private void WriteCache(string repo, string etag, string body)
		{
			try
			{
				Directory.CreateDirectory(_cacheDir);
				File.WriteAllText(CachePath(repo), JsonConvert.SerializeObject(new CachedFeed { ETag = etag, Body = body }));
			}
			catch (Exception)
			{
				// a cache that cannot be written costs a request next time, nothing more
			}
		}
	}
}
