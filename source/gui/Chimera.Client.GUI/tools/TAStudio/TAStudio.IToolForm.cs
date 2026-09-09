using System;

using Chimera.Client.Common;
using Chimera.Emulation.Common;

namespace Chimera.Client.GUI
{
	public partial class TAStudio : IToolForm
	{
		[RequiredService]
		public IEmulator Emulator { get; private set; }

		[RequiredService]
		public IStatable StatableEmulator { get; private set; }

		[RequiredService]
		public IVideoProvider VideoProvider { get; private set; }

		private bool _initializing; // If true, will bypass restart logic, this is necessary since loading projects causes a movie to load which causes a rom to reload causing dialogs to restart

		private int _lastRefresh;
		private bool _doPause;
		private int _lastRecordAction = -1;

		private void UpdateProgressBar()
		{
			if (SeekingTo != -1)
			{
				int diff = Emulator.Frame - _seekStartFrame;
				int unit = SeekingTo - _seekStartFrame;
				double progress = 0;

				if (diff != 0 && unit != 0)
				{
					progress = (double)100d / unit * diff;
				}

				if (progress < 0)
				{
					progress = 0;
				}
				else if (progress > 100)
				{
					progress = 100;
				}

				ProgressBar.Value = (int)progress;
			}
			else
			{
				ProgressBar.Visible = false;
				MessageStatusLabel.Text = "";
			}
		}

		protected override void UpdateBefore()
		{
			if (CurrentTasMovie.IsAtEnd() && !CurrentTasMovie.IsRecording())
			{
				CurrentTasMovie.RecordFrame(CurrentTasMovie.Emulator.Frame, MovieSession.StickySource);
			}
		}

		protected override void FastUpdateBefore() => UpdateBefore();

		protected override void UpdateAfter()
		{
			if (!IsHandleCreated || IsDisposed || CurrentTasMovie == null)
			{
				return;
			}

			if (_exiting)
			{
				return;
			}

			bool refreshNeeded = _inputRolls[0].RowCount != CurrentTasMovie.InputLogLength + 1;
			if (Settings.AutoadjustInput)
			{
				//refreshNeeded = AutoAdjustInput();
			}

			CurrentTasMovie.TasSession.UpdateValues(Emulator.Frame, CurrentTasMovie.Branches.Current);
			MaybeFollowCursor();

			if (Settings.AutoPause && SeekingTo == -1)
			{
				if (_doPause && CurrentTasMovie.IsAtEnd()) MainForm.PauseEmulator();
				_doPause = !CurrentTasMovie.IsAtEnd();
			}

			if (!_seekingByEdit)
			{
				_shouldMoveGreenArrow = true;
			}

			FastUpdateAfter();
			RefreshDialog(refreshNeeded, refreshBranches: false);
			if (!refreshNeeded)
			{
				_inputRolls.ForEach(r => r.InvalidateRow(_lastRefresh));
				_inputRolls.ForEach(r => r.InvalidateRow(Emulator.Frame));
				_lastRefresh = Emulator.Frame;
			}
		}

		protected override void FastUpdateAfter()
		{
			if (SeekingTo != -1 && Emulator.Frame >= SeekingTo)
			{
				bool smga = _shouldMoveGreenArrow;
				StopSeeking();
				_shouldMoveGreenArrow = smga;
			}
			UpdateProgressBar();
		}

		public override void Restart()
		{
			if (!IsActive)
			{
				return;
			}

			if (_initializing)
			{
				return;
			}

			if (CurrentTasMovie != null)
			{
				bool loadRecent = Game.Hash == CurrentTasMovie.Hash && CurrentTasMovie.Filename == Settings.RecentTas.MostRecent;
				TastudioStopMovie();
				// try to load the most recent movie if it matches the currently loaded movie
				if (loadRecent)
				{
					LoadMostRecentOrStartNew();
				}
				else
				{
					StartNewTasMovie();
				}
			}
		}

		/// <summary>
		/// Ask whether changes should be saved. Returns false if cancelled, else true.
		/// </summary>
		public override bool AskSaveChanges()
		{
			if (_suppressAskSave)
			{
				return true;
			}

			if (CurrentTasMovie?.Changes is not true) return true;

			/* Nobody to ask. A script's client.exit() reaches here with a modal
			 * three-way question and no hand to answer it: the box waits on the
			 * desktop forever while the run loop spins beside it, which is what
			 * a "hang on exit" turned out to be. Take the branch that leaves the
			 * project on disk exactly as it was - the unsaved edits are the
			 * script's own, and a script that wanted them kept had the API to
			 * save them - and say so, because a discarded change should never be
			 * silent. */
			if (MainForm.ShutdownIsUnattended)
			{
				Console.Error.WriteLine(
					"[tastudio] closing unattended with unsaved changes; the project on disk is unchanged");
				CurrentTasMovie.ClearChanges();
				return true;
			}

			var shouldSaveResult = DialogController.DoWithTempMute(() => this.ModalMessageBox3(
				caption: "Closing with Unsaved Changes",
				icon: EMsgBoxIcon.Question,
				// the thing being saved is the PROJECT, whatever window you closed
				text: "Save Chimera Project?"));
			if (shouldSaveResult == true)
			{
				TryAgainResult saveResult = this.DoWithTryAgainBox(() => SaveTas(), "Failed to save movie.");
				return saveResult != TryAgainResult.Canceled;
			}
			if (shouldSaveResult is null) return false;
			else CurrentTasMovie.ClearChanges();
			return true;
		}
	}
}
