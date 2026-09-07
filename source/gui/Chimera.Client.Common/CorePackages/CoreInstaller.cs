#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Client.Common
{
	/// <summary>What installing one version of one core produced.</summary>
	public sealed class CoreInstallResult
	{
		/// <summary>Where the package now is, if it was installed.</summary>
		public string? Path { get; init; }

		/// <summary>What the installed package turned out to be. Null on failure.</summary>
		public DiscoveredCorePackage? Package { get; init; }

		/// <summary>Non-null if it was not installed; showable as-is.</summary>
		public string? Error { get; init; }

		public bool Ok => Error is null && Path is not null;
	}

	/// <summary>
	/// Downloads one published core and puts it in the store.
	///
	/// Everything arrives over HTTPS from github.com, which is what says the bytes
	/// came from the right place. What this adds is the check that they are what
	/// they claimed to BE: a readable package, of the core that was asked for, at
	/// the version that was asked for, built for an ABI this Chimera runs. A
	/// truncated download and a release whose asset was built from the wrong branch
	/// both fail here rather than at the moment somebody tries to emulate with it.
	/// </summary>
	public sealed class CoreInstaller
	{
		private readonly HttpClient _http;

		public CoreInstaller(HttpClient? http = null)
			=> _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

		/// <summary>
		/// Fetches <paramref name="release"/> of <paramref name="core"/> into the store.
		/// <paramref name="progress"/>, if given, is called with (bytes so far, total or
		/// -1 when the server did not say).
		/// </summary>
		public async Task<CoreInstallResult> InstallAsync(
			RosterCore core,
			CoreRelease release,
			Action<long, long>? progress = null,
			CancellationToken cancel = default)
		{
			var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chimera-core-{Guid.NewGuid():N}.part");
			try
			{
				var downloadError = await DownloadAsync(release, temp, progress, cancel).ConfigureAwait(false);
				if (downloadError is not null) return new CoreInstallResult { Error = downloadError };

				if (release.Digest is { Length: not 0 } digest && DigestMismatch(temp, digest) is { } mismatch)
				{
					return new CoreInstallResult { Error = mismatch };
				}

				// read it as the frontend will: if discovery cannot make sense of it,
				// installing it would only move the failure somewhere less explicable
				var peeked = CorePackageDiscovery.Peek(temp);
				if (peeked is null) return new CoreInstallResult { Error = "what was downloaded is not a core package" };
				if (peeked.IsAbiIncompatible) return new CoreInstallResult { Error = $"{core.Name} {release.DisplayVersion} is {peeked.Error}" };
				if (peeked.Error is not null) return new CoreInstallResult { Error = $"the downloaded package could not be read: {peeked.Error}" };

				if (release.Version.Length is not 0
					&& peeked.Version.Length is not 0
					&& !string.Equals(peeked.Version, release.Version, StringComparison.OrdinalIgnoreCase))
				{
					// the release said one build and the package says another: something
					// upstream attached the wrong asset, and installing it would file a
					// core in the store under a version it is not
					return new CoreInstallResult { Error = $"the release offers {release.Version} but the package says it is {peeked.Version}" };
				}

				var version = peeked.Version.Length is not 0 ? peeked.Version : release.Version;
				var installed = CoreStore.Adopt(temp, CoreStore.FileNameFor(core.Id, version));
				return new CoreInstallResult { Path = installed, Package = CorePackageDiscovery.Peek(installed) };
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				return new CoreInstallResult { Error = ex.Message };
			}
			finally
			{
				try
				{
					if (File.Exists(temp)) File.Delete(temp);
				}
				catch (Exception)
				{
					// a leftover .part in the temp directory is not worth telling anybody about
				}
			}
		}

		private async Task<string?> DownloadAsync(CoreRelease release, string temp, Action<long, long>? progress, CancellationToken cancel)
		{
			// a release asset URL redirects to object storage, so redirects are followed
			using var response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return $"downloading {release.AssetName} failed: {(int) response.StatusCode} {response.ReasonPhrase}";
			}
			var total = response.Content.Headers.ContentLength ?? (release.AssetSize > 0 ? release.AssetSize : -1);
			using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
			using (FileStream sink = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				var buffer = new byte[81920];
				long done = 0;
				int read;
				while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) > 0)
				{
					await sink.WriteAsync(buffer, 0, read, cancel).ConfigureAwait(false);
					done += read;
					progress?.Invoke(done, total);
				}
				if (total > 0 && done != total) return $"the download stopped early ({done} of {total} bytes)";
			}
			return null;
		}

		/// <summary>
		/// Checks the digest GitHub reported, where it reported one. Newer releases
		/// carry <c>sha256:...</c>; older ones carry nothing, and an unknown algorithm
		/// is not a reason to refuse a package HTTPS already vouched for.
		/// </summary>
		private static string? DigestMismatch(string path, string digest)
		{
			var parts = digest.Split(new[] { ':' }, 2);
			if (parts.Length is not 2 || !parts[0].Equals("sha256", StringComparison.OrdinalIgnoreCase)) return null;
			using var sha = SHA256.Create();
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
			return actual.Equals(parts[1], StringComparison.OrdinalIgnoreCase)
				? null
				: $"the download does not match the digest GitHub published for it (expected {parts[1]}, got {actual})";
		}
	}
}
