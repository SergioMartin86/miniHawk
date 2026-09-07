#nullable enable

using System.Collections.Generic;

namespace Chimera.Client.Common
{
	/// <summary>
	/// What to call a system where a person reads it.
	///
	/// A core package names the machine it emulates with a short id ("DC", "A26")
	/// because that is the identity movies, projects and slots are keyed by, and
	/// those must never change. Nobody picking a core is choosing an "A26",
	/// though, so a window spells it out and keeps the id for the file formats.
	///
	/// An id with no entry here is shown as it is: a new core is readable before
	/// anyone remembers to come and add its name.
	/// </summary>
	public static class SystemNames
	{
		private static readonly Dictionary<string, string> Spelled = new()
		{
			["3DO"] = "3DO Interactive Multiplayer",
			["3DS"] = "Nintendo 3DS",
			["A26"] = "Atari 2600",
			["A78"] = "Atari 7800",
			["Amiga"] = "Commodore Amiga",
			["AmstradCPC"] = "Amstrad CPC",
			["AppleII"] = "Apple II",
			["C64"] = "Commodore 64",
			["ChannelF"] = "Fairchild Channel F",
			["Coleco"] = "ColecoVision",
			["DC"] = "Dreamcast",
			["DOS"] = "MS-DOS",
			["Dreamcast"] = "Dreamcast",
			["FDS"] = "Famicom Disk System",
			["GB"] = "Game Boy",
			["GBA"] = "Game Boy Advance",
			["GBC"] = "Game Boy Color",
			["GBL"] = "Game Boy Link",
			["GEN"] = "Mega Drive / Genesis",
			["GG"] = "Game Gear",
			["GGL"] = "Game Gear Link",
			["INTV"] = "Intellivision",
			["Jaguar"] = "Atari Jaguar",
			["Lynx"] = "Atari Lynx",
			["N64"] = "Nintendo 64",
			["NDS"] = "Nintendo DS",
			["NES"] = "Nintendo Entertainment System",
			["NGP"] = "Neo Geo Pocket",
			["O2"] = "Magnavox Odyssey 2",
			["PCE"] = "PC Engine / TurboGrafx-16",
			["PCECD"] = "PC Engine CD",
			["PCFX"] = "PC-FX",
			["PS2"] = "PlayStation 2",
			["PS3"] = "PlayStation 3",
			["PSP"] = "PlayStation Portable",
			["PSX"] = "PlayStation",
			["SAT"] = "Sega Saturn",
			["SG"] = "SG-1000",
			["SGB"] = "Super Game Boy",
			["SGX"] = "SuperGrafx",
			["SMS"] = "Master System",
			["SNES"] = "Super Nintendo",
			["TI83"] = "TI-83",
			["UZE"] = "Uzebox",
			["VB"] = "Virtual Boy",
			["VEC"] = "Vectrex",
			["WSWAN"] = "WonderSwan",
			["XBOX"] = "Xbox",
			["ZXSpectrum"] = "ZX Spectrum",
		};

		/// <summary>The full name of a system, or the id itself when it has none.</summary>
		public static string Of(string systemId)
			=> string.IsNullOrWhiteSpace(systemId) ? "" :
				Spelled.TryGetValue(systemId, out var name) ? name : systemId;

		/// <summary>Every system a package emulates, spelled out, in the order given.</summary>
		public static string Of(IEnumerable<string> systemIds)
			=> string.Join(", ", System.Linq.Enumerable.Select(systemIds, Of));
	}
}
