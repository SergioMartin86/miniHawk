#nullable enable

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

using Chimera.Emulation.Common;
using Chimera.Emulation.Common.Waterbox;

using Newtonsoft.Json;

namespace Chimera.Client.Common
{
	/// <summary>Parsed form of a package's <c>chimera-core.json</c>.</summary>
	public sealed class CorePackageManifest
	{
		public const string FILE_NAME = "chimera-core.json";

		[JsonProperty("formatVersion")]
		public int FormatVersion { get; set; }

		[JsonProperty("name")]
		public string? Name { get; set; }

		/// <summary>File name of the managed adapter assembly, relative to the package root.</summary>
		[JsonProperty("assembly")]
		public string? Assembly { get; set; }

		/// <summary>
		/// Full type names of the <see cref="ICoreFactory"/> implementations to instantiate.
		/// If empty, the assembly is scanned for public factory types.
		/// </summary>
		[JsonProperty("factoryTypes")]
		public List<string> FactoryTypes { get; set; } = new();

		/// <summary>
		/// Native library file names (relative to the package root) to preload before the
		/// adapter runs. Preloading from an absolute path pins the module, so the adapter's
		/// own by-name LoadLibrary/dlopen resolves to it regardless of search paths.
		/// </summary>
		[JsonProperty("natives")]
		public List<string> Natives { get; set; } = new();

		/// <summary>
		/// Legacy assembly (simple) names this package's adapter supersedes. Persisted
		/// configs and movie sync-settings embed "Type, Assembly" names; entries written
		/// before this core was packaged reference the old assembly (e.g.
		/// "Chimera.Emulation.Cores"), and the resolver will serve this package's
		/// assembly for those names so they keep round-tripping.
		/// </summary>
		[JsonProperty("supersedesAssemblies")]
		public List<string> SupersedesAssemblies { get; set; } = new();

		/// <summary>
		/// Rom file extension (with leading dot, lowercase) to system ID map. Chimera
		/// itself knows nothing about rom formats; this is how files get routed to
		/// systems (and thence to this package's cores).
		/// </summary>
		[JsonProperty("extensions")]
		public Dictionary<string, string> Extensions { get; set; } = new();
	}

	/// <summary>
	/// Discovers and loads external core packages: directories (or zips, which are
	/// extracted to a cache) containing a manifest, a managed adapter assembly built
	/// against the Chimera core contract, and the core's native library.
	/// </summary>
	public static class CorePackageLoader
	{
		public const int SUPPORTED_FORMAT_VERSION = 1;

		private static readonly Dictionary<string, Assembly> _loadedPackageAssemblies = new();

		private static bool _resolverHooked;

		/// <summary>
		/// Persisted settings/sync-settings and movie sync-settings JSON embed
		/// "Type, Assembly" names; for types from LoadFrom'd package assemblies the
		/// default resolution fails, so serve those assemblies from our table.
		/// </summary>
		private static void EnsureAssemblyResolverHooked()
		{
			if (_resolverHooked) return;
			_resolverHooked = true;
			AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
			{
				var simpleName = new AssemblyName(args.Name).Name;
				return simpleName is not null && _loadedPackageAssemblies.TryGetValue(simpleName, out var assy) ? assy : null;
			};
		}

		/// <summary>
		/// Loads a single core package: either a directory containing
		/// <see cref="CorePackageManifest.FILE_NAME"/>, or a zip of one (extracted to a
		/// cache first). Loading is always an explicit act (File &gt; Open Core, or the
		/// <c>--core</c> command-line option); there is no directory scanning.
		/// </summary>
		/// <exception cref="Exception">anything wrong with the package; callers surface the message</exception>
		public static (CorePackageManifest Manifest, IReadOnlyList<ICoreFactory> Factories, string PackageDir, string/*?*/ PackageSha1) LoadPackage(string path)
		{
			// A package's ground-truth identity is the SHA1 of its file; name, version,
			// and platform are secondary metadata. Directory-form packages (a dev
			// convenience) have no file to hash and therefore no identity.
			string packageDir;
			string packageSha1 = null;
			if (Directory.Exists(path))
			{
				packageDir = path;
			}
			else if (File.Exists(path) && CorePackageDiscovery.IsPackageFile(path))
			{
				packageSha1 = Chimera.Emulation.Common.Engine.ChimeraEngine.Sha1Hex(File.ReadAllBytes(path));
				packageDir = ExtractZipToCache(path, packageSha1);
			}
			else
			{
				throw new FileNotFoundException($"no core package at {path} (expecting a directory or a {CorePackageDiscovery.Extension} containing {CorePackageManifest.FILE_NAME})", path);
			}
			var (manifest, factories) = LoadPackageDir(packageDir);
			return (manifest, factories.ToList(), packageDir, packageSha1);
		}

		private static string ExtractZipToCache(string zipPath, string packageSha1)
		{
			// The cache dir name embeds the zip's SHA1 (its identity), and extraction goes
			// to a private temp dir followed by an atomic rename. Concurrent Chimera
			// instances (or a stale cache from an older zip) therefore never conflict:
			// whoever wins the rename provides the cache, losers just use it.
			var packageName = Path.GetFileNameWithoutExtension(zipPath);
			var cacheRoot = CacheStore.UnpackedCores;
			var cacheDir = Path.Combine(cacheRoot, $"{packageName}-{packageSha1}");
			if (Directory.Exists(cacheDir)) return cacheDir; // complete by construction (only ever appears via rename)
			Directory.CreateDirectory(cacheRoot);
			var tempDir = $"{cacheDir}.tmp-{Path.GetRandomFileName()}";
			ZipFile.ExtractToDirectory(zipPath, tempDir);
			try
			{
				Directory.Move(tempDir, cacheDir);
			}
			catch (IOException)
			{
				// another process won the race; theirs is complete, discard ours
				Directory.Delete(tempDir, recursive: true);
			}
			// best-effort cleanup of caches from other versions of this zip
			foreach (var oldDir in Directory.GetDirectories(cacheRoot, $"{packageName}-*"))
			{
				if (oldDir == cacheDir) continue;
				var suffix = Path.GetFileName(oldDir).Substring(packageName.Length + 1);
				var looksLikeOurs = suffix.Length is 40 && suffix.All(static c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f'));
				if (!looksLikeOurs && !long.TryParse(suffix, out _)) continue; // some other package whose name merely starts with ours
				try
				{
					Directory.Delete(oldDir, recursive: true);
				}
				catch (Exception)
				{
					// still in use by a process running the older version
				}
			}
			return cacheDir;
		}

		private static (CorePackageManifest Manifest, IEnumerable<ICoreFactory> Factories) LoadPackageDir(string packageDir)
		{
			// A waterbox package carries NO managed assembly: just core.wbx +
			// waterbox.config, driven by the built-in generic WaterboxCore (Chimera is
			// waterbox-only). Detect and route these first; the host (libminiboxhost)
			// ships with the frontend, so there is no manifest, assembly, or natives.
			if (WaterboxCoreFactory.IsWaterboxPackage(packageDir))
			{
				var wbxFactory = new WaterboxCoreFactory(packageDir);
				var wbxManifest = new CorePackageManifest
				{
					FormatVersion = SUPPORTED_FORMAT_VERSION,
					Name = wbxFactory.CoreName,
					Extensions = wbxFactory.Extensions,
				};
				return (wbxManifest, new ICoreFactory[] { wbxFactory });
			}

			var manifestPath = Path.Combine(packageDir, CorePackageManifest.FILE_NAME);
			if (!File.Exists(manifestPath)) throw new FileNotFoundException($"package has no {CorePackageManifest.FILE_NAME}", manifestPath);
			var manifest = JsonConvert.DeserializeObject<CorePackageManifest>(File.ReadAllText(manifestPath))
				?? throw new InvalidOperationException("manifest deserialized to null");
			if (manifest.FormatVersion is not SUPPORTED_FORMAT_VERSION)
			{
				throw new NotSupportedException($"package \"{manifest.Name}\" has manifest formatVersion {manifest.FormatVersion}, this build supports {SUPPORTED_FORMAT_VERSION}");
			}
			if (string.IsNullOrEmpty(manifest.Assembly)) throw new InvalidOperationException("manifest is missing \"assembly\"");

			EnsureAssemblyResolverHooked();

			// native libraries in the package resolve via LoadLibrary search, so put the package dir on PATH
			var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			if (!pathVar.Split(Path.PathSeparator).Contains(packageDir))
			{
				Environment.SetEnvironmentVariable("PATH", $"{packageDir}{Path.PathSeparator}{pathVar}");
			}

			// preload declared natives (of this platform's flavor) from absolute paths:
			// the OS loader dedupes by module name, so the adapter's later by-name
			// load resolves to these pinned modules
			var nativeExt = Chimera.Common.OSTailoredCode.IsUnixHost ? ".so" : ".dll";
			foreach (var native in manifest.Natives)
			{
				if (!native.EndsWith(nativeExt, StringComparison.OrdinalIgnoreCase)) continue; // other platform's build
				var nativePath = Path.Combine(packageDir, native);
				if (!File.Exists(nativePath))
				{
					throw new FileNotFoundException($"package \"{manifest.Name}\" declares native {native} but it is missing", nativePath);
				}
				_ = Chimera.Common.OSTailoredCode.LinkedLibManager.LoadOrThrow(nativePath);
			}

			var assyPath = Path.Combine(packageDir, manifest.Assembly);
			var assy = Assembly.LoadFrom(assyPath);
			_loadedPackageAssemblies[assy.GetName().Name] = assy;
			foreach (var legacyName in manifest.SupersedesAssemblies)
			{
				if (!_loadedPackageAssemblies.ContainsKey(legacyName)) _loadedPackageAssemblies[legacyName] = assy;
			}

			var factoryTypes = manifest.FactoryTypes.Count is not 0
				? manifest.FactoryTypes.Select(name => assy.GetType(name, throwOnError: true)!)
				: assy.GetExportedTypes().Where(static t => !t.IsAbstract && typeof(ICoreFactory).IsAssignableFrom(t));

			var factories = factoryTypes.Select(static t => (ICoreFactory) Activator.CreateInstance(t)!).ToList();
			if (factories.Count is 0) throw new InvalidOperationException($"package \"{manifest.Name}\" contains no {nameof(ICoreFactory)} implementations");
			return (manifest, factories);
		}
	}
}
