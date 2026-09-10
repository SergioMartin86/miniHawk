using Chimera.Client.Common;

namespace Chimera.Client.GUI
{
	public partial class TAStudio
	{
		/// <summary>
		/// Seek to the given frame, past or future, and load a state first if doing so gets us there faster.
		/// Does nothing if we are already on the given frame.
		/// </summary>
		public void GoToFrame(int frame, bool OnLeftMouseDown = false, bool skipLoadState = false)
		{
			_lastRecordAction = -1;
			if (frame == Emulator.Frame)
			{
				StopSeeking();
				return;
			}

			// Unpausing after a seek may seem like we aren't really seeking at all:
			// what is the significance of a seek to frame if we don't pause?
			// Answer: We use this in order to temporarily disable recording mode when the user navigates to a frame. (to avoid recording between whatever is the most recent state and the user-specified frame)
			// Other answer: turbo seek, navigating while unpaused
			_pauseAfterSeeking = MainForm.EmulatorPaused || (SeekingTo != -1 && _pauseAfterSeeking);
			WasRecording = CurrentTasMovie.IsRecording() || WasRecording;
			TastudioPlayMode();
			SeekingTo = frame; // must be before LoadState, since it calls UpdateAfter (potentially doing end-of-seek logic if prior seek was to before this frame) instead of SavestateLoaded for ??? reason.

			// Asking where the history would land is free and does not move the
			// machine, so the decision to load happens before anything is loaded.
			var closestState = PriorStateForFramebuffer(frame);
			if (closestState < 0 && frame < Emulator.Frame)
			{
				// Backwards, with nothing stored at or before the target. The
				// seek below would unpause and play FORWARD, at a frame already
				// past the one asked for, and arrive nowhere - which reads as the
				// piano roll refusing to rewind and never saying why. The history
				// keeps the beginning of the run precisely so this cannot happen
				// (docs/state-manager.md), so if it ever does, say so and stay put
				// rather than start a seek that cannot end.
				StopSeeking();
				MessageStatusLabel.Text = $"Cannot go back to frame {frame}: the state history has nothing at or before it.";
				MainForm.AddOnScreenMessage($"Nothing stored at or before frame {frame}");
				return;
			}
			if (closestState >= 0 && (frame < Emulator.Frame || (closestState > Emulator.Frame && !skipLoadState)))
			{
				LoadStateAt(closestState);
			}

			if (Emulator.Frame != frame)
			{
				_seekStartFrame = Emulator.Frame;
				_seekingByEdit = false;

				MainForm.UnpauseEmulator();

				if (SeekingTo - _seekStartFrame > 1)
				{
					MessageStatusLabel.Text = "Seeking...";
					ProgressBar.Visible = true;
				}
			}
			else
			{
				StopSeeking();
			}

			if (!OnLeftMouseDown)
			{
				MaybeFollowCursor();
			}
		}

		public void GoToPreviousMarker()
		{
			if (Emulator.Frame > 0)
			{
				var prevMarker = CurrentTasMovie.Markers.Previous(Emulator.Frame);
				var prev = prevMarker?.Frame ?? 0;
				GoToFrame(prev);
			}
		}

		public void GoToNextMarker()
		{
			var nextMarker = CurrentTasMovie.Markers.Next(Emulator.Frame);
			var next = nextMarker?.Frame ?? CurrentTasMovie.InputLogLength - 1;
			GoToFrame(next);
		}

		public void RestorePosition(bool byEdit = false)
		{
			if (RestorePositionFrame != -1)
			{
				// restore makes no sense without pausing
				// Pausing here ensures any seek done by GoToFrame pauses after completing.
				MainForm.PauseEmulator();
				GoToFrame(RestorePositionFrame, skipLoadState: !Config.TurboSeek && !byEdit);
			}
		}

		/// <summary>
		/// Makes the given frame visible. If no frame is given, makes the current frame visible.
		/// </summary>
		public void SetVisibleFrame(int? frame = null)
		{
			if (_leftButtonHeld)
			{
				return;
			}

			int scrollTo = frame ?? Emulator.Frame;
			if (Settings.ScrollSync)
			{
				_inputRolls.ForEach(r => r.ScrollToIndex(scrollTo));
			}
			else
			{
				_activeInputRoll.ScrollToIndex(scrollTo);
			}
		}

		private void MaybeFollowCursor()
		{
			if (TasPlaybackBox.FollowCursor)
			{
				SetVisibleFrame();
			}
		}

		public int GetSeekFrame()
		{
			return SeekingTo == -1 ? Emulator.Frame : SeekingTo;
		}
	}
}
