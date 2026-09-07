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

		public bool IsUsable => Id.Length is not 0 && Repo.Contains('/');
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
