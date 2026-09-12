using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

using Chimera.Client.Common;
using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// Compiles a game's code for a core that recompiles, before the game is
	/// ever run (docs/compile-cache.md). The objects are a pure function of the
	/// game, the package and the target CPU, so this is not a decision anybody
	/// has to make - only work that is better done once, in parallel, with
	/// something on screen, than a minute at a time at every boot.
	///
	/// The work happens in child processes: this same frontend, headless, each
	/// one a precompile session compiling its share. They report every object
	/// they store, and this collects them into the game's manifest.
	/// </summary>
	public static class PrecompileOrchestrator
	{
		/// <summary>"[cache] stored &lt;name&gt; &lt;sha1&gt;", as the engine's cache bridge prints it.</summary>
		private static readonly Regex CacheLine = new(@"^\[cache\] (stored|fetched) (\S+) ([0-9A-Fa-f]{40})\s*$", RegexOptions.Compiled);

		/// <summary>"Precompiled 3/21 modules", as a session prints it.</summary>
		private static readonly Regex ProgressLine = new(@"^Precompiled (\d+)/(\d+) modules", RegexOptions.Compiled);

		/// <summary>
		/// What a session said as it refused the job. A precompile session is a
		/// headless frontend, so the thing that stopped it is a modal it could not
		/// show: "[headless] text: ..." is that modal's words. Without this the
		/// wizard can only say the compile stopped - and on Windows the parent is a
		/// GUI-subsystem process whose Console.Error goes nowhere, so the reason
		/// was invisible in every log.
		/// </summary>
		private static readonly Regex RefusalLine = new(@"^\[headless\] text: (.+)$", RegexOptions.Compiled);

		/// <summary>Why the last Run returned null, in the words of whatever refused it; null when it succeeded.</summary>
		public static string LastFailure { get; private set; }

		/// <summary>How much one session is assumed to need, measured rather than guessed.</summary>
		/// <remarks>
		/// A session holds a PlayStation 3's address space and an LLVM compiler at
		/// once, but almost all of that is RESERVED and never touched: one
		/// compiling Ultra Street Fighter IV, measured on Windows, peaked at
		/// 1.75 GB of commit and a 3.0 GB working set, and eight of them together
		/// held 8.7 GB while the machine never dropped below 79 GB free. So 4 GB
		/// is a wide margin, and the figure it replaces (8 GB, times everything
		/// the machine called free, with nothing kept back) was wrong in both
		/// directions at once.
		///
		/// Do not measure this with maximum resident set size on Linux: the same
		/// session reports 83 GB there, because the sandbox's arena is mapped and
		/// its touched pages are counted. The Windows commit figure is the one
		/// that means "what the machine has to find".
		/// </remarks>
		private const double GigabytesPerSession = 4;

		/// <summary>Left for the system and for this process, never handed to sessions.</summary>
		private const double GigabytesReserved = 4;

		/// <summary>
		/// How many sessions run side by side: one per <see cref="GigabytesPerSession"/>
		/// of what the machine can spare AFTER <see cref="GigabytesReserved"/>, never
		/// more than half the cores, never more than eight, never fewer than one.
		/// Fewer sessions is slower; too many is a machine that swaps, or kills them.
		/// </summary>
		public static int Workers
		{
			get
			{
				var byCores = Math.Max(1, Environment.ProcessorCount / 2);
				var spare = Math.Max(0, AvailableGigabytes() - GigabytesReserved);
				var byMemory = Math.Max(1, (int)(spare / GigabytesPerSession));
				return Math.Min(8, Math.Min(byCores, byMemory));
			}
		}

		/// <summary>
		/// What an exit code says about how a session ended. A session that dies
		/// prints nothing on its way out, so its code is the only evidence there
		/// is - and reporting "failed without saying why" while holding it was
		/// throwing the evidence away. It was worth more than the sentence: the
		/// code said 0xC0000005, which is what turned a supposed memory problem
		/// into the real one (a JIT region that wrapped onto its own code, fixed
		/// in the rpcs3 core, 2026-09-12).
		/// </summary>
		internal static string WhyItDied(int code) => code switch
		{
			137 => "killed by the system (out of memory)",          // 128 + SIGKILL
			139 => "crashed (segmentation fault)",                  // 128 + SIGSEGV
			134 => "aborted",                                       // 128 + SIGABRT
			-1073741819 => "crashed (access violation)",            // 0xC0000005
			-1073741801 => "out of memory",                         // 0xC0000017
			-1073740791 => "crashed (stack buffer overrun)",        // 0xC0000409
			_ => $"exit code {code}",
		};

		/// <summary>Whether a code is one the machine gives for running out of memory.</summary>
		internal static bool DiedForWantOfMemory(int code) => code is 137 or -1073741801;

		/// <summary>What the machine can spare right now, in GB; 8 when it will not say.</summary>
		private static double AvailableGigabytes()
		{
			try
			{
				if (File.Exists("/proc/meminfo"))
				{
					foreach (var line in File.ReadLines("/proc/meminfo"))
					{
						if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
						var kb = double.Parse(new string(line.Where(char.IsDigit).ToArray()));
						return kb / (1024 * 1024);
					}
					return 8;
				}
				MemoryStatusEx status = new() { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
				return GlobalMemoryStatusEx(ref status) ? status.AvailPhys / (1024.0 * 1024 * 1024) : 8;
			}
			catch (Exception)
			{
				return 8; // an unknowable machine gets the middle of the road
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MemoryStatusEx
		{
			public uint Length;
			public uint MemoryLoad;
			public ulong TotalPhys;
			public ulong AvailPhys;
			public ulong TotalPageFile;
			public ulong AvailPageFile;
			public ulong TotalVirtual;
			public ulong AvailVirtual;
			public ulong AvailExtendedVirtual;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

		/// <summary>An object as the wizard shows it: named, hashed, and either there or not.</summary>
		public sealed class Entry
		{
			public string Name { get; set; }
			public string Sha1 { get; set; }
			public bool Present { get; set; }
		}

		/// <summary>What a game needs and what of it is there, from the manifest a previous compile left.</summary>
		public static List<Entry> Survey(string cacheDir, string romSha1)
		{
			var manifest = CoreCacheManifest.Load(cacheDir, romSha1);
			if (manifest is null) return [ ];
			return manifest.Files
				.Select(f => new Entry
				{
					Name = f.Name,
					Sha1 = f.Sha1,
					Present = string.Equals(CoreCacheManifest.HashOf(cacheDir, f.Name), f.Sha1, StringComparison.OrdinalIgnoreCase),
				})
				.ToList();
		}

		/// <summary>Everything the game needs is on disk and unchanged.</summary>
		public static bool Satisfied(string cacheDir, string romSha1)
		{
			var entries = Survey(cacheDir, romSha1);
			return entries.Count is not 0 && entries.All(e => e.Present);
		}

		public static long CacheBytes(string cacheDir)
			=> cacheDir is null || !Directory.Exists(cacheDir)
				? 0
				: new DirectoryInfo(cacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

		public static void Clear(string cacheDir)
		{
			if (cacheDir is not null && Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
		}

		/// <summary>
		/// Runs the sessions and collects what they compile. onEntry is called as
		/// each object lands, so a list can fill while the work happens; onProgress
		/// gets the modules finished across every session and the game's module
		/// count, which they all report the same. Returns the manifest that was
		/// written, or null when nothing usable came of it.
		/// </summary>
		public static CoreCacheManifest Run(
			string packagePath, string configPath, string romPath, string romSha1, string cacheDir,
			Action<Entry> onEntry, Action<uint, uint> onProgress, Func<bool> cancelled,
			IReadOnlyDictionary<string, string> firmware = null)
		{
			LastFailure = null;
			if (string.IsNullOrEmpty(romPath) || !File.Exists(romPath) || cacheDir is null)
			{
				LastFailure = "there is no game file to compile";
				return null;
			}

			var found = new Dictionary<string, string>(StringComparer.Ordinal);
			LastFailure = null;
			var workers = Workers;
			// kept across attempts: a session's own words are worth more than
			// anything inferred from an exit code, whichever attempt said them
			string refused = null;

			// A session the system killed took its share of the work with it, and a
			// manifest written from what the others managed would be a list of what
			// this game needs with holes in it. The answer is not to give up: it is
			// to want less at once. Halving the sessions and going again turns the
			// one failure a person cannot act on into a slower success they do not
			// have to know about.
			for (;;)
			{
				var done = new uint[workers];
				var total = new uint[workers];
				var processes = new List<Process>();
				string failure = null;

				void Line(int index, string line)
				{
					var refusal = RefusalLine.Match(line);
					if (refusal.Success)
					{
						// first one wins: the others are the same modal in the other sessions
						lock (found)
						{
							failure ??= refusal.Groups[1].Value;
							refused ??= refusal.Groups[1].Value;
						}
						return;
					}
					var m = CacheLine.Match(line);
					if (m.Success)
					{
						lock (found)
						{
							// stored and fetched both count: a worker that found an
							// object another one had just written still needs it
							found[m.Groups[2].Value] = m.Groups[3].Value.ToUpperInvariant();
						}
						onEntry?.Invoke(new Entry { Name = m.Groups[2].Value, Sha1 = m.Groups[3].Value.ToUpperInvariant(), Present = true });
						return;
					}
					m = ProgressLine.Match(line);
					if (!m.Success) return;
					// Every session reports the game's module count, and finishes a
					// share of it: one total, and the dones add up.
					done[index] = uint.Parse(m.Groups[1].Value);
					total[index] = uint.Parse(m.Groups[2].Value);
					onProgress?.Invoke((uint)done.Sum(v => (long)v), total.Max());
				}

				for (var i = 0; i < workers; i++)
				{
					var args = new List<string> { "--headless" };
					if (!string.IsNullOrEmpty(configPath)) args.Add($"--config={configPath}");
					args.Add($"--core={packagePath}");
					args.Add($"--precompile={i}/{workers}");
					// Say the firmware outright. Going through the config means the
					// parent must have written it there first, and the wizard can hold
					// a path that never gets written - a core may call a firmware
					// optional that this GAME cannot boot without. A session refused at
					// boot compiles nothing.
					foreach (var (id, path) in firmware ?? new Dictionary<string, string>())
					{
						args.Add($"--firmware={id}={path}");
					}
					args.Add(romPath);
					var index = i;
					var p = SelfProcess.Start(args, line => Line(index, line));
					if (p is not null) processes.Add(p);
				}
				if (processes.Count is 0) return null;

				var stopped = false;
				while (processes.Any(p => !p.HasExited))
				{
					Thread.Sleep(150);
					if (stopped || cancelled?.Invoke() != true) continue;
					stopped = true;
					foreach (var p in processes.Where(p => !p.HasExited))
					{
						try { p.Kill(); } catch (Exception) { /* it finished on its own */ }
					}
				}
				foreach (var p in processes) p.WaitForExit();
				if (stopped) return null;

				// A session that died took its share with it, and a manifest written
				// from what the others managed would be a list of what this game
				// needs with holes in it - which is exactly what must not happen.
				var dead = processes.Where(p => p.ExitCode != 0).ToList();
				if (dead.Count is 0) break;

				var why = string.Join(", ",
					dead.Select(p => WhyItDied(p.ExitCode)).Distinct(StringComparer.Ordinal));
				Console.Error.WriteLine(
					$"precompile: {dead.Count} of {processes.Count} sessions failed ({why})");

				if (failure is null && workers > 1 && dead.All(p => DiedForWantOfMemory(p.ExitCode)))
				{
					workers = Math.Max(1, workers / 2);
					Console.Error.WriteLine($"precompile: retrying with {workers} session(s)");
					continue;
				}

				// Whatever else happened, it is not "without saying why" any more.
				LastFailure = failure
					?? $"{dead.Count} of {processes.Count} compile sessions failed: {why}."
						+ (workers > 1
							? " Closing other programs, or a machine with more memory, would let it run fewer at once."
							: " This ran one session at a time already, so memory is unlikely to be the reason.");
				return null;
			}

			// What the sessions compiled is what this game needs. Written beside
			// the objects; the project records the same list.
			var manifest = new CoreCacheManifest
			{
				RomName = Path.GetFileName(romPath),
				RomSha1 = romSha1,
				Files = found.OrderBy(kv => kv.Key, StringComparer.Ordinal)
					.Select(kv => new CoreCacheFile { Name = kv.Key, Sha1 = kv.Value })
					.ToList(),
			};
			if (manifest.Files.Count is 0)
			{
				LastFailure = refused ?? "the sessions compiled nothing for this game";
				return null;
			}
			manifest.Save(cacheDir, romSha1);
			return manifest;
		}
	}

	/// <summary>Launches this same frontend as a child process (headless), streaming its output lines.</summary>
	public static class SelfProcess
	{
		/// <summary>
		/// One argument, quoted the way Windows will parse it back.
		/// 
		/// A backslash is ONLY special before a quote: CommandLineToArgvW reads
		/// 2n of them plus a quote as n backslashes and a delimiter, 2n+1 as n
		/// backslashes and a literal quote, and leaves any other run alone. So
		/// escaping every backslash - which is what this did - doubles every
		/// separator in a path, and "C:\games\x.iso" reaches the child as
		/// "C:\\games\\x.iso", which does not exist. It only bit paths that
		/// needed quoting in the first place, i.e. ones containing a space,
		/// which is why it survived: the precompile sessions all failed at once
		/// on a game whose folder had one.
		/// </summary>
		private static string Quote(string a)
		{
			if (a.Length > 0 && a.IndexOfAny([ ' ', '"', '\t' ]) < 0) return a;
			var sb = new System.Text.StringBuilder(a.Length + 8);
			sb.Append('"');
			for (var i = 0; i < a.Length; i++)
			{
				var slashes = 0;
				while (i < a.Length && a[i] == '\\') { slashes++; i++; }
				if (i == a.Length)
				{
					// run at the end: doubled, so the closing quote stays a delimiter
					sb.Append('\\', slashes * 2);
					break;
				}
				if (a[i] == '"')
				{
					sb.Append('\\', slashes * 2 + 1).Append('"');
				}
				else
				{
					sb.Append('\\', slashes).Append(a[i]);
				}
			}
			sb.Append('"');
			return sb.ToString();
		}

		public static Process Start(IEnumerable<string> args, Action<string> onLine)
		{
			var psi = new ProcessStartInfo
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
			var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
			var underMono = Type.GetType("Mono.Runtime") is not null;
			var all = new List<string>();
			psi.FileName = exe; // under mono: the runtime itself, then the assembly
			if (underMono) all.Add(entry);
			all.AddRange(args);
			psi.Arguments = string.Join(" ", all.Select(Quote));
			try
			{
				var p = Process.Start(psi);
				if (p is null) return null;
				p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
				p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
				p.BeginOutputReadLine();
				p.BeginErrorReadLine();
				return p;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"precompile: could not start a worker: {ex.Message}");
				return null;
			}
		}
	}
}
