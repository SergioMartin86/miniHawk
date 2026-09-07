#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using Chimera.Emulation.Common.Engine;

using Newtonsoft.Json;

namespace Chimera.Client.Common
{
	/// <summary>
	/// What a core package says about its own terms.
	///
	/// This used to be a build-time question: the bundle carried every core, and
	/// tools/bundle-licenses.py computed one LICENSES.md from them. Chimera ships
	/// no cores now, so the question moves to install time - and it is a real
	/// question, because several cores (Genesis Plus GX, Opera, Snes9x) forbid
	/// commercial use and that binds whatever they are installed into, while others
	/// are GPL and require their corresponding source to stay identifiable.
	///
	/// Every package carries <c>licenses/licenses.json</c>, put there at package
	/// time; this reads it so the manager can say what a core demands rather than
	/// leaving somebody to open the zip.
	/// </summary>
	public sealed class CoreLicence
	{
		/// <summary>What the built core as a whole may be used for.</summary>
		[JsonProperty("effectiveTerms")]
		public string EffectiveTerms { get; set; } = "";

		/// <summary>"permitted", "prohibited", or absent.</summary>
		[JsonProperty("commercialUse")]
		public string CommercialUse { get; set; } = "";

		[JsonProperty("note")]
		public string Note { get; set; } = "";

		[JsonProperty("components")]
		public List<Component> Components { get; set; } = new();

		public sealed class Component
		{
			[JsonProperty("name")]
			public string Name { get; set; } = "";

			[JsonProperty("license")]
			public string License { get; set; } = "";

			[JsonProperty("url")]
			public string Url { get; set; } = "";
		}

		/// <summary>True when this core may not be used or redistributed commercially.</summary>
		public bool ForbidsCommercialUse
			=> CommercialUse.Equals("prohibited", StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// The terms in the few lines a window can show. Commercial use is stated
		/// first when it is forbidden, because that is the part that binds anything
		/// this core is installed into.
		/// </summary>
		public string Summary()
		{
			List<string> lines = new();
			if (ForbidsCommercialUse) lines.Add("Non-commercial: this core may not be used or redistributed commercially.");
			if (EffectiveTerms.Length is not 0) lines.Add(EffectiveTerms);
			if (Note.Length is not 0) lines.Add(Note);
			var named = Components.Where(static c => c.Name.Length is not 0).Select(static c => $"{c.Name} ({c.License})").ToList();
			if (named.Count is not 0) lines.Add(string.Join(", ", named));
			return string.Join(Environment.NewLine, lines);
		}

		public const string FileName = "licenses/licenses.json";

		/// <summary>
		/// Reads the declaration out of a package, or null where there is none. A
		/// package without one is not refused here - that judgement belongs to
		/// whoever assembles a distribution - but the manager says it stated nothing.
		/// </summary>
		public static CoreLicence? Read(string packagePath)
		{
			try
			{
				using var package = EnginePackage.Open(packagePath);
				var text = package?.EntryText(FileName);
				return text is null ? null : JsonConvert.DeserializeObject<CoreLicence>(text);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
