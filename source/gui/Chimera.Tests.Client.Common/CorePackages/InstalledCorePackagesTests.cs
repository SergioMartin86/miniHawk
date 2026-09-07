#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Chimera.Client.Common;
using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// The frontend against real, published core packages.
	///
	/// Chimera used to build every core it shipped, so a change that broke the
	/// generic waterbox adapter could not get past its own build. Now the cores are
	/// published separately (docs/core-manager.md) and nothing in Chimera's CI
	/// compiles one - which would leave a frontend change free to break every core
	/// at once, unseen until somebody downloaded it.
	///
	/// So CI fills build/Cores with what each core last published
	/// (<c>tools/fetch-cores.sh</c>) and these run against it. They need no rom, no
	/// firmware and no emulation: what they check is the CONTRACT - that a package
	/// this frontend will be asked to open can be read, understood, and turned into
	/// a working factory. Whether the emulation is still right is each core
	/// repository's own gate, which runs against Chimera's main.
	/// </summary>
	[TestClass]
	public class InstalledCorePackagesTests
	{
		private static IReadOnlyList<string> Packages()
		{
			var files = InstalledPackages.Files;
			if (files.Count is 0)
			{
				Assert.Inconclusive($"no core packages in {InstalledPackages.Directory} (see tools/fetch-cores.sh)");
			}
			return files;
		}

		[TestMethod]
		public void EveryPackageIsReadable()
		{
			List<string> broken = new();
			foreach (var path in Packages())
			{
				var peeked = CorePackageDiscovery.Peek(path);
				if (peeked is null) broken.Add($"{InstalledPackages.NameOf(path)}: not recognised as a package at all");
				else if (peeked.Error is not null) broken.Add($"{InstalledPackages.NameOf(path)}: {peeked.Error}");
			}
			Assert.AreEqual(0, broken.Count, string.Join("; ", broken));
		}

		[TestMethod]
		public void EveryPackageIsBuiltForAnAbiThisFrontendRuns()
		{
			// the check that exists because cores and frontend are versioned apart:
			// if this fails, either GuestAbi.Current moved without the cores being
			// rebuilt, or MinimumSupported was raised and retired them
			var refused = Packages()
				.Select(CorePackageDiscovery.Peek)
				.Where(static p => p?.IsAbiIncompatible is true)
				.Select(static p => $"{p!.Name}: {p.Error}")
				.ToList();
			Assert.AreEqual(0, refused.Count, string.Join("; ", refused));
		}

		/// <summary>
		/// The declaration turned into the thing that runs it. The factory's
		/// constructor is where a package's machines, settings and controller are
		/// validated against each other, so this is the real test of whether the
		/// frontend still understands what the cores are saying.
		/// </summary>
		[TestMethod]
		public void EveryPackageBecomesAFactory()
		{
			List<string> failed = new();
			foreach (var path in Packages())
			{
				var dir = Extracted(path);
				if (dir is null) { failed.Add($"{InstalledPackages.NameOf(path)}: could not be opened"); continue; }
				try
				{
					if (!WaterboxCoreFactory.IsWaterboxPackage(dir))
					{
						failed.Add($"{InstalledPackages.NameOf(path)}: no core.wbx and waterbox.config");
						continue;
					}
					var factory = new WaterboxCoreFactory(dir);
					if (string.IsNullOrWhiteSpace(factory.CoreName)) failed.Add($"{InstalledPackages.NameOf(path)}: names no core");
					if (factory.SystemIds.Count is 0) failed.Add($"{InstalledPackages.NameOf(path)}: claims no machine");
				}
				catch (Exception ex)
				{
					failed.Add($"{InstalledPackages.NameOf(path)}: {ex.Message}");
				}
				finally
				{
					TryDelete(dir);
				}
			}
			Assert.AreEqual(0, failed.Count, string.Join("; ", failed));
		}

		/// <summary>
		/// A package declares the bindings its controller ships with, and the
		/// frontend has none of its own. A binding for a button the controller does
		/// not declare is a binding nothing can ever use.
		/// </summary>
		[TestMethod]
		public void KeybindsOnlyNameButtonsTheControllerHas()
		{
			List<string> stray = new();
			foreach (var path in Packages())
			{
				var dir = Extracted(path);
				if (dir is null) continue;
				try
				{
					var keybinds = PackageKeybinds.Read(dir);
					if (keybinds is null) continue;
					var config = WaterboxConfig.FromJson(File.ReadAllText(Path.Combine(dir, WaterboxCoreFactory.ConfigFileName)));
					// Compared WITHOUT the player prefix on either side. A multi-player
					// controller declares "P1 A" and binds "A": the bindings are per
					// controller and the frontend applies the player, so the prefix is
					// on the declaration and not on the binding.
					HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
					foreach (var button in AllButtons(config)) declared.Add(WithoutPlayer(button));
					if (declared.Count is 0) continue;
					foreach (var (controller, bindings) in keybinds.AllTrollers)
					{
						foreach (var button in bindings.Keys)
						{
							if (!declared.Contains(WithoutPlayer(button)))
							{
								stray.Add($"{InstalledPackages.NameOf(path)}/{controller}: {button}");
							}
						}
					}
				}
				catch (Exception ex)
				{
					stray.Add($"{InstalledPackages.NameOf(path)}: {ex.Message}");
				}
				finally
				{
					TryDelete(dir);
				}
			}
			Assert.AreEqual(0, stray.Count, string.Join("; ", stray));
		}

		/// <summary>
		/// What the manager will call each package in the store, checked against what
		/// the package says it is. A published package with no version cannot be
		/// filed, cited by a movie, or told apart from the next one.
		/// </summary>
		[TestMethod]
		public void EveryPackageStampsAVersion()
		{
			var unversioned = Packages()
				.Select(CorePackageDiscovery.Peek)
				.Where(static p => p is { Error: null } && p.Version.Length is 0)
				.Select(static p => p!.Name)
				.ToList();
			Assert.AreEqual(0, unversioned.Count, string.Join("; ", unversioned));
		}

		/// <summary>"P2 Start" -&gt; "Start"; anything without a player prefix unchanged.</summary>
		private static string WithoutPlayer(string control)
		{
			var space = control.IndexOf(' ');
			if (space < 2 || control[0] is not ('P' or 'p')) return control;
			for (var i = 1; i < space; i++)
			{
				if (control[i] is < '0' or > '9') return control;
			}
			return control.Substring(space + 1);
		}

		private static IEnumerable<string> AllButtons(WaterboxConfig? config)
		{
			if (config?.Input?.Buttons is { } buttons) foreach (var b in buttons) yield return b;
			foreach (var machine in config?.Machines ?? new List<WaterboxConfig.MachineConfig>())
			{
				foreach (var b in machine.Input?.Buttons ?? new List<string>()) yield return b;
			}
		}

		/// <summary>Unpacks a package to a temp directory, or null if it cannot be read.</summary>
		private static string? Extracted(string packagePath)
		{
			var dir = Path.Combine(Path.GetTempPath(), $"chimera-pkg-{Guid.NewGuid():N}");
			try
			{
				System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, dir);
				return dir;
			}
			catch (Exception)
			{
				TryDelete(dir);
				return null;
			}
		}

		private static void TryDelete(string dir)
		{
			try
			{
				if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
			}
			catch (Exception)
			{
				// a leftover temp dir is not a test failure
			}
		}
	}
}
