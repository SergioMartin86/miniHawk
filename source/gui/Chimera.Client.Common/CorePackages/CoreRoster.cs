#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Chimera.Common.PathExtensions;

using Newtonsoft.Json;

namespace Chimera.Client.Common
{
	/// <summary>One official core: where it is published, and which build was tested.</summary>
	public sealed class RosterCore
	{
		/// <summary>
		/// The core's short id, which is also the base name of its published asset and
		/// of the file in the store (<c>gpgx</c> -&gt; <c>gpgx-&lt;version&gt;.chimeraCore</c>).
		/// </summary>
		[JsonProperty("id")]
		public string Id { get; set; } = "";

		/// <summary>How the core introduces itself, matching its package's coreName.</summary>
		[JsonProperty("name")]
		public string Name { get; set; } = "";

		[JsonProperty("systems")]
		public List<string> Systems { get; set; } = new();

		/// <summary><c>owner/repo</c> on GitHub: the only place versions of this core come from.</summary>
		[JsonProperty("repo")]
		public string Repo { get; set; } = "";

		/// <summary>
		/// The version this Chimera's CI matrix passed against, or empty where there is
		/// no such build yet. Empty means "take the newest of the chosen channel"; it
		/// is not an error, and a fresh core has it until the matrix has run once.
		/// </summary>
		[JsonProperty("tested")]
		public string Tested { get; set; } = "";

		/// <summary>
		/// True for a core somebody added by hand (File &gt; Core Manager &gt; Add
		/// external core) rather than one this build ships a roster entry for. Not
		/// serialised into the shipped roster - every entry there is official by
		/// definition; it is set when the config's added cores are merged in.
		/// </summary>
		[JsonIgnore]
		public bool IsExternal { get; set; }

		[JsonIgnore]
		public bool IsUsable => Id.Length is not 0 && Repo.Contains('/');

		/// <summary>
		/// The <c>owner/repo</c> in a GitHub page address, or null if there is not
		/// one. Accepts what somebody actually has to hand - the page they are
		/// looking at, with or without scheme, trailing slash, .git suffix or a
		/// deeper path - and the bare <c>owner/repo</c> they might type instead.
		/// </summary>
		public static string? RepoFromUrl(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return null;
			var s = text.Trim();
			foreach (var prefix in new[] { "https://", "http://", "git@github.com:", "github.com/", "www.github.com/" })
			{
				if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(prefix.Length);
			}
			if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) s = s.Substring("github.com/".Length);
			if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
			var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2) return null;
			// a deeper page - /releases, /tree/main - still names the repository
			var owner = parts[0];
			var repo = parts[1];
			if (owner.Length is 0 || repo.Length is 0) return null;
			foreach (var c in owner + repo)
			{
				if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return null;
			}
			return $"{owner}/{repo}";
		}
	}

	/// <summary>
	/// The list of official cores that ships with Chimera: what exists, who
	/// publishes it, and which build this frontend was tested against.
	///
	/// It carries no versions. Versions live in each core's GitHub releases and are
	/// fetched when the user asks for them, never in the background - so the roster
	/// is what lets the manager show a core somebody does NOT have yet, which is the
	/// one thing a release feed cannot do until you already know where to look.
	/// </summary>
	public static class CoreRoster
	{
		public const string FileName = "official-cores.json";

		public const int SupportedFormatVersion = 1;

		/// <summary>The roster as shipped, beside the executable.</summary>
		public static string DefaultPath => System.IO.Path.Combine(PathUtils.ExeDirectoryPath, FileName);

		/// <summary>
		/// Reads the roster. A missing or unreadable file is an EMPTY roster, not an
		/// error: a Chimera whose roster did not survive being copied about should
		/// still run every core already installed, and say it knows of none to fetch.
		/// </summary>
		public static IReadOnlyList<RosterCore> Read(string? path = null)
		{
			try
			{
				var file = path ?? DefaultPath;
				if (!File.Exists(file)) return [ ];
				return Parse(File.ReadAllText(file));
			}
			catch (Exception)
			{
				return [ ];
			}
		}

		/// <summary>
		/// The shipped roster plus whatever cores somebody added by hand, official
		/// first. A hand-added core whose repository is already in the roster is
		/// dropped rather than listed twice - adding gpgx by URL should not produce a
		/// second gpgx.
		/// </summary>
		public static IReadOnlyList<RosterCore> WithExternal(IReadOnlyList<RosterCore> official, IEnumerable<RosterCore> external)
		{
			HashSet<string> known = new(official.Select(static c => c.Repo), StringComparer.OrdinalIgnoreCase);
			List<RosterCore> result = new(official);
			foreach (var core in external)
			{
				if (!core.IsUsable || !known.Add(core.Repo)) continue;
				core.IsExternal = true;
				result.Add(core);
			}
			return result;
		}

		/// <summary>Parses roster JSON. Throws on anything it cannot make sense of.</summary>
		public static IReadOnlyList<RosterCore> Parse(string json)
		{
			var doc = JsonConvert.DeserializeObject<RosterFile>(json)
				?? throw new InvalidOperationException($"{FileName} deserialized to null");
			if (doc.FormatVersion is not SupportedFormatVersion)
			{
				throw new NotSupportedException($"{FileName} formatVersion {doc.FormatVersion}, this build supports {SupportedFormatVersion}");
			}
			return doc.Cores
				.Where(static c => c.IsUsable)
				.OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		private sealed class RosterFile
		{
			[JsonProperty("formatVersion")]
			public int FormatVersion { get; set; }

			[JsonProperty("cores")]
			public List<RosterCore> Cores { get; set; } = new();
		}
	}
}
