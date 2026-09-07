#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
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
	/// Unauthenticated GitHub allows 60 requests an hour per address. A check over
	/// the cores somebody actually has is a handful of those and the whole roster is
	/// about fifteen, which is why the answers are cached by ETag: asking again
	/// costs nothing at all unless the answer changed. A token in the config raises
	/// the limit for anyone who hits it for real.
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

		private readonly string? _token;

		public CoreFeed(HttpClient? http = null, string? cacheDir = null, string? token = null)
		{
			_http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent))
			{
				// a handed-in client may already carry one; that is fine
			}
			_cacheDir = cacheDir ?? System.IO.Path.Combine(CoreStore.Path, ".feed-cache");
			_token = string.IsNullOrWhiteSpace(token) ? null : token;
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
				using HttpRequestMessage request = new(HttpMethod.Get, CoreReleases.ApiUrl(core.Repo));
				request.Headers.Accept.ParseAdd("application/vnd.github+json");
				if (_token is not null) request.Headers.Authorization = new("Bearer", _token);
				if (cached?.ETag is { Length: not 0 } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

				using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);

				if (response.StatusCode is HttpStatusCode.NotModified && cached is not null)
				{
					return new CoreFeedResult { Releases = CoreReleases.Parse(cached.Body, core.Id), FromCache = true };
				}
				// 429 is not in net48's enum; GitHub uses both it and 403 for a limit
				if ((response.StatusCode is HttpStatusCode.Forbidden || (int) response.StatusCode is 429)
					&& RateLimited(response) is { } limitMessage)
				{
					return new CoreFeedResult { Error = limitMessage, Releases = CachedReleases(cached, core.Id), FromCache = true };
				}
				if (response.StatusCode is HttpStatusCode.NotFound)
				{
					return new CoreFeedResult { Error = $"{core.Repo} has no releases, or is not there any more" };
				}
				if (!response.IsSuccessStatusCode)
				{
					return new CoreFeedResult { Error = $"GitHub answered {(int) response.StatusCode} {response.ReasonPhrase} for {core.Repo}" };
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

		/// <summary>
		/// Turns a 403 into a sentence with a time in it. "Rate limited" on its own
		/// leaves somebody pressing the button again, which is exactly the wrong move.
		/// Returns null when the 403 was about something else, e.g. a bad token.
		/// </summary>
		public static string? RateLimited(HttpResponseMessage response)
		{
			if (!response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)) return null;
			if (remaining.FirstOrDefault() is not "0") return null;
			var wait = "a while";
			if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
				&& long.TryParse(reset.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
			{
				var minutes = (int) Math.Ceiling((DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow).TotalMinutes);
				wait = minutes <= 1 ? "a minute" : $"{minutes} minutes";
			}
			return $"GitHub is rate limiting this address; it will answer again in {wait}. (Downloads already started are unaffected.)";
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
