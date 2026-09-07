#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Chimera.Common.PathExtensions;
using Chimera.Emulation.Common.Engine;
using Chimera.Emulation.Common.Waterbox;

using Newtonsoft.Json;

namespace Chimera.Client.Common
{
	/// <summary>
	/// What a scan found at one path: enough to list it and identify it: read
	/// WITHOUT loading anything. Loading a package is irreversible in-process (it
	/// pins native modules and, for adapter packages, an assembly), so discovery
	/// must be able to describe a package it will never load: one that is broken,
	/// or one that duplicates another.
	/// </summary>
	public sealed class DiscoveredCorePackage
	{
		/// <summary>Absolute path of the package file (or directory, for dev packages).</summary>
		public string Path { get; init; } = "";

		/// <summary>
		/// True for a directory-form (dev) package. Derived from <see cref="Sha1"/>
		/// rather than stored: a zip is always hashed and a directory never can be, so
		/// two fields could only ever disagree with each other.
		/// </summary>
		public bool IsDirectoryForm => Sha1 is null;

		/// <summary>Display name, from waterbox.config's coreName or the manifest's name; falls back to the file name.</summary>
		public string Name { get; init; } = "";

		public IReadOnlyList<string> Systems { get; init; } = [ ];

		/// <summary>
		/// What the package says its version is: for one published by the automated
		/// build, the commit it was made from. A package built by hand says so ("+local"),
		/// and one that says nothing is empty.
		/// </summary>
		public string Version { get; init; } = "";

		/// <summary>Rom extension (leading dot, lowercase) -&gt; system id.</summary>
		public IReadOnlyDictionary<string, string> Extensions { get; init; } = new Dictionary<string, string>();

		/// <summary>
		/// SHA1 of the package file: the package's ground-truth identity. Null for
		/// directory-form packages, which have no file to hash.
		/// </summary>
		public string? Sha1 { get; init; }

		/// <summary>Non-null if the package could not be read; it is listed but not loadable.</summary>
		public string? Error { get; init; }

		/// <summary>
		/// The guest ABI the package declares (see <see cref="GuestAbi"/>). Zero for a
		/// package too broken to read one out of, and for adapter packages, which are
		/// versioned by their manifest's formatVersion instead.
		/// </summary>
		public int Abi { get; init; }

		/// <summary>
		/// True when the package is intact but this build cannot run it: its guest ABI
		/// is outside the supported range. Distinguished from an ordinary
		/// <see cref="Error"/> because the remedy is different - update one side or the
		/// other, rather than replace a corrupt file - and because the core manager
		/// greys these out rather than offering them.
		/// </summary>
		public bool IsAbiIncompatible { get; init; }

		/// <summary>
		/// Identity for deduplication: the SHA1 where there is one, so the same
		/// package reachable under two names or from two search directories is
		/// listed once; the path for directory-form packages, which have no hash.
		/// </summary>
		public string Key => Sha1 ?? Path;

		public bool IsLoadable => Error is null;

		/// <summary>
		/// The identity abbreviated for display, or "-" for a directory-form package.
		/// Does not assume a well-formed hash: this is display code, and a package
		/// that reached it with a short or odd identity should still render.
		/// </summary>
		public string ShortSha1
			=> Sha1 is null ? "-" : Sha1.Length <= 8 ? Sha1 : Sha1.Substring(0, 8);

		public override string ToString()
			=> $"{Name} [{ShortSha1}] {string.Join(",", Systems)}{(Error is null ? "" : $" ERROR: {Error}")}";
	}

	/// <summary>
	/// Finds core packages in the search directories and reads their identity
	/// without loading them. This is the discovery half of the charter's "cores
	/// arrive as packages, discovered from a Cores/ directory"; <see cref="CoreRegistry"/>
	/// does the loading.
	/// </summary>
	public static class CorePackageDiscovery
	{
		public const string DefaultDirName = "Cores";

		/// <summary>
		/// What a core package is called. The container is a zip, but the extension
		/// says what the file IS rather than how it is compressed: a Chimera core,
		/// not an archive of unknown contents that happens to hold one. A scan of
		/// Cores/ therefore never has to open an unrelated zip to find out.
		/// </summary>
		public const string Extension = ".chimeraCore";

		/// <summary>
		/// Packages were once plain <c>.zip</c>, and one downloaded then (or from the
		/// permanent core archive, whose older assets carry that name) is the SAME
		/// package: identity is the SHA1 of the bytes, which a rename does not touch.
		/// So a zip still LOADS when something points at one; only discovery is
		/// narrow, because scanning every zip in a directory is what the extension
		/// exists to avoid.
		/// </summary>
		public const string LegacyExtension = ".zip";

		/// <summary>Whether <paramref name="path"/> names a package file this can open.</summary>
		public static bool IsPackageFile(string path)
			=> path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase);

		/// <summary>The default search directory: <c>Cores/</c> beside the executable.</summary>
		public static string DefaultSearchPath
			=> System.IO.Path.Combine(PathUtils.ExeDirectoryPath, DefaultDirName);

		/// <summary>
		/// Scans <paramref name="searchPaths"/> (each a directory) for packages, in
		/// order. A directory entry is a package if it holds a waterbox package or a
		/// manifest; otherwise the scan does NOT recurse into it; packages live at
		/// the top of a search directory, so an unrelated folder of roms costs nothing.
		/// Missing search directories are skipped silently: an absent Cores/ is the
		/// normal state of a fresh checkout, not an error.
		/// </summary>
		/// <returns>one entry per package found, name-sorted, duplicates (same identity) collapsed</returns>
		public static IReadOnlyList<DiscoveredCorePackage> Scan(IEnumerable<string> searchPaths)
		{
			List<DiscoveredCorePackage> found = new();
			HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);
			HashSet<string> seenDirs = new(StringComparer.OrdinalIgnoreCase);
			foreach (var searchPath in searchPaths)
			{
				if (string.IsNullOrWhiteSpace(searchPath)) continue;
				string fullSearchPath;
				try
				{
					fullSearchPath = System.IO.Path.GetFullPath(searchPath);
				}
				catch (Exception)
				{
					continue; // unusable path string; not worth a diagnostic
				}
				if (!Directory.Exists(fullSearchPath) || !seenDirs.Add(fullSearchPath)) continue;

				List<string> candidates = new();
				try
				{
					candidates.AddRange(Directory.EnumerateFiles(fullSearchPath, "*" + Extension));
					candidates.AddRange(Directory.EnumerateDirectories(fullSearchPath));
				}
				catch (Exception ex)
				{
					// an unreadable search dir is worth saying out loud, once
					found.Add(new DiscoveredCorePackage { Path = fullSearchPath, Name = System.IO.Path.GetFileName(fullSearchPath), Error = ex.Message });
					continue;
				}

				foreach (var candidate in candidates.OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
				{
					var pkg = Peek(candidate);
					if (pkg is null) continue; // not a package at all; silently ignored
					if (!seenKeys.Add(pkg.Key)) continue; // same file reachable twice
					found.Add(pkg);
				}
			}
			return found.OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
		}

		/// <summary>
		/// The directories to scan, in order: the default <c>Cores/</c> beside the
		/// executable, then any the user added. The default is not removable: a
		/// package dropped into Cores/ must always be found, that being the whole
		/// point of the directory.
		/// </summary>
		public static IReadOnlyList<string> SearchPaths(Config config)
			=> new[] { DefaultSearchPath }.Concat(config.CorePackagePaths).ToList();

		/// <summary>Scans the directories <paramref name="config"/> designates.</summary>
		public static IReadOnlyList<DiscoveredCorePackage> ScanFor(Config config)
			=> Scan(SearchPaths(config));

		/// <summary>
		/// Reads one candidate path's identity. Returns null if it is not a core
		/// package at all (an ordinary zip or folder); returns an entry with
		/// <see cref="DiscoveredCorePackage.Error"/> set if it looks like one but
		/// cannot be read; that distinction is what keeps a broken package visible
		/// instead of silently missing.
		/// </summary>
		public static DiscoveredCorePackage? Peek(string path)
		{
			try
			{
				// the container is the engine's (see docs/engine-migration.md): what
				// counts as a package, its identity hash, its entries. Keep the old
				// gate on what is even worth asking about.
				if (!Directory.Exists(path) && !(IsPackageFile(path) && File.Exists(path)))
				{
					return null;
				}
				var full = System.IO.Path.GetFullPath(path);
				using var package = EnginePackage.Open(full);
				if (package is null) return null;
				var fallbackName = Directory.Exists(path)
					? System.IO.Path.GetFileName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
					: System.IO.Path.GetFileNameWithoutExtension(full);
				return package.IsWaterbox
					? FromWaterboxConfig(full, package.Sha1, fallbackName, package.EntryText(WaterboxCoreFactory.ConfigFileName)!)
					: FromManifest(full, package.Sha1, fallbackName, package.EntryText(CorePackageManifest.FILE_NAME)!);
			}
			catch (Exception ex)
			{
				return new DiscoveredCorePackage
				{
					Path = System.IO.Path.GetFullPath(path),
					Name = System.IO.Path.GetFileNameWithoutExtension(path),
					Error = ex.Message,
				};
			}
		}

		private static DiscoveredCorePackage FromWaterboxConfig(string path, string? sha1, string fallbackName, string json)
		{
			var cfg = WaterboxConfig.FromJson(json)
				?? throw new InvalidOperationException($"{WaterboxCoreFactory.ConfigFileName} is empty or invalid");
			var systems = cfg.SystemIds;
			if (systems.Count is 0)
			{
				throw new InvalidOperationException($"{WaterboxCoreFactory.ConfigFileName} names no machine (systemId, or machines)");
			}
			// An ABI this build does not understand is not an error in the package: it
			// is a perfectly good core for a different Chimera. It is still listed -
			// silently dropping it would leave someone staring at a Cores folder that
			// contains the core they just downloaded and a frontend that denies it.
			var refusal = GuestAbi.Refuse(cfg.Abi);
			return new DiscoveredCorePackage
			{
				Path = path,
				Sha1 = sha1,
				Name = string.IsNullOrWhiteSpace(cfg.CoreName) ? fallbackName : cfg.CoreName,
				Version = cfg.Version ?? "",
				Systems = systems,
				Extensions = NormaliseExtensions(cfg.AllExtensions),
				Abi = cfg.Abi,
				IsAbiIncompatible = refusal is not null,
				Error = refusal,
			};
		}

		private static DiscoveredCorePackage FromManifest(string path, string? sha1, string fallbackName, string json)
		{
			var manifest = JsonConvert.DeserializeObject<CorePackageManifest>(json)
				?? throw new InvalidOperationException($"{CorePackageManifest.FILE_NAME} deserialized to null");
			if (manifest.FormatVersion is not CorePackageLoader.SUPPORTED_FORMAT_VERSION)
			{
				throw new NotSupportedException($"manifest formatVersion {manifest.FormatVersion}, this build supports {CorePackageLoader.SUPPORTED_FORMAT_VERSION}");
			}
			var extensions = NormaliseExtensions(manifest.Extensions);
			return new DiscoveredCorePackage
			{
				Path = path,
				Sha1 = sha1,
				Name = string.IsNullOrWhiteSpace(manifest.Name) ? fallbackName : manifest.Name!,
				Systems = extensions.Values.Distinct().OrderBy(static s => s, StringComparer.Ordinal).ToList(),
				Extensions = extensions,
			};
		}

		private static Dictionary<string, string> NormaliseExtensions(IDictionary<string, string>? extensions)
		{
			Dictionary<string, string> result = new();
			if (extensions is null) return result;
			foreach (var (ext, sysID) in extensions) result[ext.ToLowerInvariant()] = sysID;
			return result;
		}
	}
}
