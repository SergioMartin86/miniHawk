using System.IO;
using System.Linq;

using Chimera.Common;
using Chimera.Emulation.Common;

namespace Chimera.Tests.Client.Common.Movie
{
	[Core("Fake", "Author", false, false)]
	internal class FakeEmulator : IEmulator, IStatable, IStateHistory, IInputPollable, IGpuRendered
	{
		/// <summary>Empty for a machine nobody's GPU drew, which is the default.</summary>
		public string GpuRenderer { get; set; } = "";

		/// <summary>False, as for a renderer that does not rebuild after a context change.</summary>
		public bool GpuStatesSurviveTheContext { get; set; }

		public int RenderWarmupFrames { get; set; }

		private BasicServiceProvider _serviceProvider;
		public IEmulatorServiceProvider ServiceProvider => _serviceProvider;

		private static readonly ControllerDefinition _cd = new ControllerDefinition("fake controller")
		{
			BoolButtons = { "A", "B" },
		}
			.AddAxis("Stick", (-100).RangeTo(100), 0)
			.MakeImmutable();

		static FakeEmulator()
		{
			_cd.BuildMnemonicsCache("fake");
		}

		public ControllerDefinition ControllerDefinition => _cd;

		public int Frame { get; set; }

		public string SystemId => "fake";

		public bool DeterministicEmulation => true;

		public bool AvoidRewind => false;

		// ---- IStateHistory ----
		//
		// The real one is the engine's and keeps deltas, bands and a spill file;
		// this keeps a set of frame numbers, which is all the movie machinery
		// above it can observe. It exists because a movie without a history is
		// not a case production has - every Chimera core is a waterbox core - and
		// a TasMovie that quietly worked without one would be hiding that.

		private readonly System.Collections.Generic.SortedSet<int> _states = new();
		private readonly System.Collections.Generic.HashSet<int> _pins = new();
		public long BudgetBytes { get; private set; }

		public long DiskBudgetBytes { get; private set; }
		public string SpillDirectory { get; private set; }

		public void Enable(long budgetBytes)
		{
			BudgetBytes = budgetBytes;
			_states.Clear();
			if (budgetBytes is not 0) _states.Add(Frame);
		}

		public void SpillTo(string directory) => SpillDirectory = directory;

		public void DiskBudget(long budgetBytes) => DiskBudgetBytes = budgetBytes;

		/// <summary>Set by a test that wants to see what a full disk does.</summary>
		public bool SpillFailed { get; set; }

		public long Count => _states.Count;

		public int Nearest(int frame)
		{
			var best = -1;
			foreach (var f in _states)
			{
				if (f > frame) break;
				best = f;
			}
			return best;
		}

		public bool Has(int frame) => _states.Contains(frame);

		public void BeforeAdvance() { }

		public void Capture(int frame) => _states.Add(frame);

		public bool RestoreTo(int frame)
		{
			if (!_states.Contains(frame)) return false;
			Frame = frame;
			return true;
		}


		public void InvalidateAfter(int afterFrame) => _states.RemoveWhere(f => f > afterFrame);

		/// <summary>
		/// A file, so the wiring above can be tested: that a movie writes its
		/// history where it says it does, reads it back when the emulator
		/// arrives, and refuses one of another machine. What is IN it is the
		/// engine's business and is proved in engine_state_history.
		/// </summary>
		public bool Save(string path, string machineId)
		{
			File.WriteAllLines(path, new[] { machineId }
				.Concat(_states.Select(static f => f.ToString())).ToArray());
			return true;
		}

		public bool Load(string path, string machineId)
		{
			_states.Clear();
			try
			{
				var lines = File.ReadAllLines(path);
				// a history of another machine is dropped, not refused: losing it
				// costs replaying and never work
				if (lines.Length > 0 && lines[0] == machineId)
				{
					foreach (var line in lines.Skip(1)) _states.Add(int.Parse(line));
				}
			}
			catch (IOException)
			{
				// no history yet is a cold greenzone, which is not a failure
			}
			if (_states.Count is 0) _states.Add(Frame);
			return true;
		}

		public void Pin(int frame, bool pinned)
		{
			if (pinned) _pins.Add(frame);
			else _pins.Remove(frame);
		}

		public void UnpinAll() => _pins.Clear();

		/// <summary>What the movie asked be kept, for a test that wants to look.</summary>
		public System.Collections.Generic.IReadOnlyCollection<int> Pinned => _pins;

		public int LagCount { get; set; }
		public bool IsLagFrame { get; set; }

		private InputCallbackSystem _inputCallbacks = new();
		public IInputCallbackSystem InputCallbacks => _inputCallbacks;

		public FakeEmulator()
		{
			_serviceProvider = new(this);
		}

		public bool PollInputOnFrameAdvance = true;

		public void Dispose() { }
		public bool FrameAdvance(IController controller, bool render, bool renderSound = true)
		{
			Frame++;
			if (PollInputOnFrameAdvance) InputCallbacks.Call();
			return true;
		}

		public void LoadStateBinary(BinaryReader reader)
		{
			Frame = reader.ReadInt32();
			LagCount = reader.ReadInt32();
			IsLagFrame = reader.ReadBoolean();
		}

		public void ResetCounters()
		{
			Frame = 0;
			LagCount = 0;
			IsLagFrame = false;
		}

		public void SaveStateBinary(BinaryWriter writer)
		{
			writer.Write(Frame);
			writer.Write(LagCount);
			writer.Write(IsLagFrame);
		}
	}
}
