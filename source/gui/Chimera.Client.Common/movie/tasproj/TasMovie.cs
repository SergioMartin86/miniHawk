using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Chimera.Common.StringExtensions;
using Chimera.Emulation.Common;

namespace Chimera.Client.Common
{
	internal sealed partial class TasMovie : MovieBase, ITasMovie
	{
		public new const string Extension = "chimeraProject";
		private IInputPollable _inputPollable;

		public const double CurrentVersion = 1.1;

		/// <exception cref="InvalidOperationException">loaded core does not implement <see cref="IStatable"/></exception>
		internal TasMovie(IMovieSession session, string path)
			: base(session, path)
		{
			Branches = new TasBranchCollection(this);
			ChangeLog = new TasMovieChangeLog(this);
			Header[HeaderKeys.MovieVersion] = $"Chimera Project File v{CurrentVersion.ToString(CultureInfo.InvariantCulture)}";
			Markers = new TasMovieMarkerList(this);
			Markers.CollectionChanged += Markers_CollectionChanged;
		}

		public override void Attach(IEmulator emulator)
		{
			if (!emulator.HasSavestates())
			{
				throw new InvalidOperationException($"A core must be able to provide an {nameof(IStatable)} service");
			}

			if (!emulator.CanPollInput())
			{
				throw new InvalidOperationException($"A core must be able to provide an {nameof(IInputPollable)} service");
			}

			_inputPollable = emulator.AsInputPollable();

			// The history is the engine's (docs/state-manager.md). Attaching turns
			// it on, which captures the machine as it stands - frame zero, here -
			// as the anchor everything else is reached from.
			States = emulator.AsStateHistory();
			States.SpillTo(ProjectCache.Ensure(Project.Id));
			States.Enable((long)Session.Settings.GreenzoneBudgetMb * 1024 * 1024);
			// Read here and not with the rest of the cache, because until the
			// emulator arrives there is nowhere to put it. A machine a GPU drew
			// makes states good only in the session that made them, so it starts
			// cold (docs/gpu-bridge.md).
			if (!StatesMadeByGpu) States.Load(StateHistoryFilename, MachineIdentityOf(Project));
			RefreshPins();

			base.Attach(emulator);
		}

		public override bool StartsFromSavestate
		{
			get => base.StartsFromSavestate;
			set => base.StartsFromSavestate = value;
		}

		public IStringLog VerificationLog { get; } = StringLogUtil.MakeStringLog(); // For movies that do not begin with power-on, this is the input required to get into the initial state
		public ITasBranchCollection Branches { get; }
		public ITasSession TasSession { get; private set; } = new TasSession();

		public int LastEditedFrame { get; private set; } = -1;
		public bool LastEditWasRecording { get; private set; }
		public bool LastPositionStable { get; set; } = true;
		public TasMovieMarkerList Markers { get; private set; }
		public bool BindMarkersToInput { get; set; }

		public TasLagLog LagLog { get; } = new TasLagLog();

		public override string PreferredExtension => Extension;
		/// <summary>
		/// Where the machine has been. Held by the engine, and this is the remote
		/// control - there is deliberately no copy up here of which frames exist,
		/// because a second copy is a second thing to keep true.
		/// </summary>
		public IStateHistory States { get; private set; }

		public Action<int> GreenzoneInvalidated { get; set; }

		public ITasMovieRecord this[int index]
		{
			get
			{
				var lagIndex = index + 1;
				var lagged = LagLog[lagIndex];
				if (lagged == null)
				{
					if (IsAttached() && Emulator.Frame == lagIndex)
					{
						lagged = _inputPollable.IsLagFrame;
					}
				}

				return new TasMovieRecord
				{
					HasState = States is not null && States.Has(index),
					LogEntry = GetInputLogEntry(index),
					Lagged = lagged,
					WasLagged = LagLog.History(lagIndex),
				};
			}
		}

		public override void StartNewRecording()
		{
			ClearTasprojExtras();
			RefreshLastNonEmptyInput(0);
			Markers.RefreshPermanent();
			ClearChanges();

			base.StartNewRecording();
		}

		/// <summary>
		/// The last frame with anything pressed on it, or zero when nothing is
		/// pressed anywhere. Emptiness is the same emptiness Truncate uses - the
		/// log entry a DEFAULT controller generates - so the two agree about where
		/// a run's input really stops.
		/// </summary>
		public int LastNonEmptyInputFrame => _lastNonEmptyInput;

		private int _lastNonEmptyInput;

		/// <summary>
		/// Works the answer out again after an edit at <paramref name="frame"/>,
		/// knowing that nothing before that frame has changed.
		///
		/// It matters that this is cheap: recording calls it once per frame, and a
		/// run recorded past its last press would otherwise re-read a longer and
		/// longer log every frame. So the walk back from the end stops at the edit,
		/// which during recording is a step or two; and it only ever falls through
		/// to reading the whole log when the edit lands at or before the frame the
		/// answer was sitting on, which is the one case where that answer can have
		/// been erased.
		/// </summary>
		private void RefreshLastNonEmptyInput(int frame)
		{
			if (Log.Count == 0)
			{
				_lastNonEmptyInput = 0;
				return;
			}

			int floor = _lastNonEmptyInput < frame ? frame : 0;
			string empty = LogEntryGenerator.EmptyEntry(
				DefaultValueController ?? Session.MovieController);
			for (int i = Log.Count - 1; i >= floor; i--)
			{
				if (Log[i] != empty)
				{
					_lastNonEmptyInput = i;
					return;
				}
			}

			// nothing pressed from the edit onwards: what stands is whatever was
			// found before it, which the untouched frames still say is true
			if (floor == 0) _lastNonEmptyInput = 0;
		}

		// Removes lag log and greenzone after this frame
		private void InvalidateAfter(int frame)
		{
			if (_suspendInvalidation)
			{
				_minInvalidationFrame = Math.Min(_minInvalidationFrame, frame);
				return;
			}

			LagLog.RemoveFrom(frame);
			// asked before the drop, because afterwards there is nothing to see
			var anyStateInvalidated = States.Nearest(int.MaxValue) > frame;
			States.InvalidateAfter(frame);

			Changes = true;
			LastEditedFrame = frame;
			if (anyStateInvalidated && IsCountingRerecords)
			{
				Rerecords++;
			}

			// The run's own markers follow the movie. This is the one place every
			// editing path passes through - input, inserts, deletes, truncation -
			// so putting it here is what keeps them from ever being stale.
			RefreshLastNonEmptyInput(frame);
			Markers.RefreshPermanent();

			GreenzoneInvalidated?.Invoke(frame);
			LastEditWasRecording = false; // We can set it here; it's only used in the GreenzoneInvalidated action.
		}

		public void InvalidateEntireGreenzone()
			=> InvalidateAfter(0);

		private (int Frame, IMovieController Controller) _displayCache = (-1, null);

		/// <summary>
		/// Returns the mnemonic value for boolean buttons, and actual value for axes,
		/// for a given frame and button.
		/// </summary>
		public string DisplayValue(int frame, string buttonName, bool defaultAxisAsBlank)
		{
			if (_displayCache.Frame != frame || Log.Count == 1)
			{
				_displayCache.Controller ??= new MovieController(Session.MovieController.Definition, LogKey);
				_displayCache.Controller.SetFromMnemonic(Log[frame]);
				_displayCache.Frame = frame;
			}

			return CreateDisplayValueForButton(_displayCache.Controller, buttonName, defaultAxisAsBlank);
		}

		private string CreateDisplayValueForButton(IController adapter, string buttonName, bool defaultAxisAsBlank)
		{
			// those Contains checks could be avoided by passing in the button type
			// this should be considered if this becomes a significant performance issue
			if (adapter.Definition.BoolButtons.Contains(buttonName))
			{
				return adapter.IsPressed(buttonName)
					? adapter.Definition.MnemonicsCache![buttonName].ToString()
					: "";
			}

			if (adapter.Definition.Axes.ContainsKey(buttonName))
			{
				int value = adapter.AxisValue(buttonName);
				if (defaultAxisAsBlank)
				{
					int defaultValue = Session.MovieController.Definition.Axes[buttonName].Neutral;
					if (value == defaultValue) return "";
				}
				return value.ToString();
			}

			return "";
		}

		public void GreenzoneCurrentFrame()
		{
			LagLog[Emulator.Frame] = _inputPollable.IsLagFrame;

			// Every frame, unconditionally: the engine decides what a frame near
			// the playhead costs to keep and what it costs once the playhead has
			// moved on, which is what "force this one" used to be for.
			States.Capture(Emulator.Frame);
		}

		/// <summary>
		/// Before the machine moves, every frame. A delta is what changed since a
		/// marked moment, and the moment has to be marked first.
		/// </summary>
		public void GreenzoneBeforeFrame() => States?.BeforeAdvance();

		/// <summary>
		/// Tells the history which frames it must keep whatever its thinning would
		/// otherwise do with them. Markers want instant navigation, and a branch
		/// is somebody's alternative route; both are cheap to name and impossible
		/// for the engine to guess. Rebuilt wholesale because the sets are small
		/// and a missed removal would pin a frame forever.
		/// </summary>
		public void RefreshPins()
		{
			if (States is null) return;
			States.UnpinAll();
			// the frame BEFORE, because navigating to a marker emulates one frame
			// from there so the screen has a framebuffer
			foreach (var m in Markers)
			{
				if (m.WantsState && m.Frame > 0) States.Pin(m.Frame - 1, true);
			}
			foreach (var b in Branches) States.Pin(b.Frame, true);
		}


		public void CopyVerificationLog(IEnumerable<string> log)
		{
			foreach (string entry in log)
			{
				VerificationLog.Add(entry);
			}
		}

		// TODO: this is 99% copy pasting of bad code
		public override bool ExtractInputLog(TextReader reader, out string errorMessage)
		{
			errorMessage = "";
			int? stateFrame = null;

			var newLog = new List<string>();
			int? timelineBranchFrame = null;

			// We are in record mode so replace the movie log with the one from the savestate
			if (Session.Settings.EnableBackupMovies && MakeBackup && Log.Count != 0)
			{
				// TODO: This isn't ideal, but making it ideal would mean a big refactor.
				FileWriteResult saveResult = SaveBackup();
				if (saveResult.Exception != null) throw saveResult.Exception;
				MakeBackup = false;
			}

			int counter = 0;
			string line;
			while ((line = reader.ReadLine()) != null)
			{
				if (line.StartsWith('|'))
				{
					newLog.Add(line);
					if (!timelineBranchFrame.HasValue && counter < Log.Count && line != Log[counter])
					{
						timelineBranchFrame = counter;
					}

					counter++;
				}
				else if (line.StartsWithOrdinal("Frame "))
				{
					var split = line.Split(' ');
					try
					{
						stateFrame = int.Parse(split[1]);
					}
					catch
					{
						errorMessage = "Savestate Frame number failed to parse";
						return false;
					}
				}
				else if (line.StartsWithOrdinal("LogKey:"))
				{
					LogKey = line.Replace("LogKey:", "");
				}
			}

			Log.Clear();
			Log.AddRange(newLog);

			if (!stateFrame.HasValue)
			{
				errorMessage = "Savestate Frame number failed to parse";
			}

			var stateFrameValue = stateFrame ?? 0;

			if (stateFrameValue > 0 && stateFrameValue < Log.Count)
			{
				if (!Session.Settings.VBAStyleMovieLoadState)
				{
					Truncate(stateFrameValue);
				}
			}
			else if (stateFrameValue > Log.Count) // Post movie savestate
			{
				if (!Session.Settings.VBAStyleMovieLoadState)
				{
					Truncate(Log.Count);
				}

				Mode = MovieMode.Finished;
			}

			if (IsCountingRerecords)
			{
				Rerecords++;
			}

			if (timelineBranchFrame.HasValue)
			{
				LagLog.RemoveFrom(timelineBranchFrame.Value);
				States.InvalidateAfter(timelineBranchFrame.Value);
				GreenzoneInvalidated?.Invoke(timelineBranchFrame.Value);
			}

			return true;
		}

		public void LoadBranch(TasBranch branch)
		{
			int? divergentPoint = Log.DivergentPoint(branch.InputLog);

			Log?.Dispose();
			Log = branch.InputLog.Clone();

			if (BindMarkersToInput) // pretty critical not to erase them
			{
				Markers = branch.Markers.DeepClone();
			}

			ChangeLog = branch.ChangeLog.Clone();

			InvalidateAfter(divergentPoint ?? branch.InputLog.Count);
		}

		private void Markers_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
		{
			Changes = true;
		}

		public void ClearChanges() => Changes = false;
		public void FlagChanges() => Changes = true;

		public bool IsReserved(int frame)
		{
			// Why the frame before?
			// because we always navigate to the frame before and emulate 1 frame so that we ensure a proper frame buffer on the screen
			// users want instant navigation to markers, so to do this, we need to reserve the frame before the marker, not the marker itself
			return Markers.Exists(m => m.WantsState && m.Frame - 1 == frame)
				|| Branches.Any(b => b.Frame == frame); // Branches should already be in the reserved list, but it doesn't hurt to check
		}

		public override void Dispose()
		{
			base.Dispose();
			// not disposed: the history belongs to the emulator, which outlives
			// the movie and is disposed by whoever made it
			States = null;
		}
	}
}
