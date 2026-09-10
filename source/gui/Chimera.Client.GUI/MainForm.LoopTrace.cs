using System;
using System.Diagnostics;

namespace Chimera.Client.GUI
{
	public partial class MainForm
	{
		/// <summary>
		/// Where a frame's wall time goes, phase by phase, when CHIMERA_LOOP_TRACE=1.
		///
		/// The engine says what a frame costs to emulate and to capture
		/// (CHIMERA_HISTORY_TRACE); this says what the client adds on top - the
		/// tools it updates, the render, the throttle, the message pump - because a
		/// seek through the piano roll runs this loop once per frame, and on a core
		/// whose frame is a millisecond the loop is the seek. Printed every 300
		/// emulated frames, to stderr, as milliseconds per frame. Costs nothing
		/// when off: every timestamp is behind the flag.
		/// </summary>
		private static class LoopTrace
		{
			public static readonly bool Enabled = Environment.GetEnvironmentVariable("CHIMERA_LOOP_TRACE") is "1";

			public const int Top = 0, Core = 1, ToolsBefore = 2, Advance = 3, ToolsAfter = 4, Render = 5, Throttle = 6, Messages = 7;
			private static readonly string[] Names = { "top", "core", "tools-before", "advance", "tools-after", "render", "throttle", "messages" };
			private const int Every = 300;

			private static readonly long[] Ticks = new long[Names.Length];
			private static int _frames, _served;
			private static long _windowStarted;

			/// <summary>The host was served this iteration: input, render, messages.</summary>
			public static void Served()
			{
				if (Enabled) _served++;
			}

			public static void Add(int phase, long started)
			{
				if (!Enabled) return;
				Ticks[phase] += Stopwatch.GetTimestamp() - started;
			}

			public static void Frame()
			{
				if (!Enabled) return;
				if (_windowStarted == 0) _windowStarted = Stopwatch.GetTimestamp();
				if (++_frames % Every != 0) return;
				var now = Stopwatch.GetTimestamp();
				double perFrameMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / Every;
				var line = $"[loop] frames {_frames - Every}-{_frames}: {perFrameMs(now - _windowStarted):F2} ms/frame, host served {_served} times:";
				_served = 0;
				for (var i = 0; i < Names.Length; i++)
				{
					line += $" {Names[i]} {perFrameMs(Ticks[i]):F2}";
					Ticks[i] = 0;
				}
				Console.Error.WriteLine(line);
				_windowStarted = now;
			}
		}
	}
}
